import { describe, expect, it } from 'vitest';
import { InspectionDocument, inspectionFileType, maximumOriginalBytes, validateInspectionDocument,
  validateInspectionDocuments, validateOriginalSignature, verifiedInspectionDownload } from '../src/app/core/equipment-documents.models';
import { EquipmentInspection } from '../src/app/core/equipment-workflow.models';
import { sha256Blob } from '../src/app/core/integrations.models';

const inspection = { id: '40000000-0000-4000-8000-000000000001', equipmentId: '40000000-0000-4000-8000-000000000002',
  revision: 2, status: 'Completed', completion: { digest: 'a'.repeat(64), recordedAt: '2026-10-21T10:00:00Z' } } as EquipmentInspection;
const body = new Blob(['%PDF-1.7\nSynthetic original\n%%EOF'], { type: 'application/pdf' });
const document = (changes: Partial<InspectionDocument> = {}): InspectionDocument => ({
  id: '40000000-0000-4000-8000-000000000003', tenantId: 'tenant-a', name: 'Prüfung.pdf', contentType: 'application/pdf',
  size: body.size, sha256: 'b'.repeat(64), versionId: 'immutable-version', state: 'Ready', revision: 2, projectId: null,
  sourceKind: 'equipment-inspections', sourceId: inspection.id, sourceDigest: inspection.completion!.digest,
  rendererVersion: 'equipment-inspection-original-v1', createdAt: '2026-10-21T11:00:00Z', ...changes
});

describe('inspection originals', () => {
  it('accepts only the exact completion, tenant and retained version', () => {
    expect(validateInspectionDocument(document(), inspection, 'tenant-a').name).toBe('Prüfung.pdf');
    expect(validateInspectionDocument(document({ state: 'Archived' }), inspection, 'tenant-a').state).toBe('Archived');
    expect(validateInspectionDocument(document({ state: 'Staged', versionId: null, revision: 1 }), inspection, 'tenant-a').state).toBe('Staged');
  });
  it.each([
    { tenantId: 'tenant-b' }, { sourceId: inspection.equipmentId }, { sourceDigest: 'c'.repeat(64) },
    { rendererVersion: 'other-renderer' }, { versionId: null }, { versionId: 'null' }, { revision: 0 },
    { createdAt: '2026-10-21T09:00:00Z' }, { sha256: 'not-a-digest' }, { name: '../report.pdf' },
    { contentType: 'image/png' }, { projectId: inspection.equipmentId }
  ])('rejects substituted or incomplete metadata %j', changes => {
    expect(() => validateInspectionDocument(document(changes as Partial<InspectionDocument>), inspection, 'tenant-a')).toThrow();
  });
  it('rejects pending inspections and inconsistent paginated source lists', () => {
    expect(() => validateInspectionDocument(document(), { ...inspection, status: 'Scheduled', completion: null }, 'tenant-a')).toThrow();
    const page = { inspectionId: inspection.id, equipmentId: inspection.equipmentId, sourceDigest: inspection.completion!.digest,
      items: [document()], total: 1, page: 1, pageSize: 20 };
    expect(validateInspectionDocuments(page, inspection, 'tenant-a', 1).items).toHaveLength(1);
    expect(() => validateInspectionDocuments({ ...page, items: [document(), document()], total: 2 }, inspection, 'tenant-a', 1)).toThrow();
    expect(() => validateInspectionDocuments(page, inspection, 'tenant-a', 2)).toThrow();
    expect(() => validateInspectionDocuments({ ...page, equipmentId: inspection.id }, inspection, 'tenant-a', 1)).toThrow();
  });
  it('checks name, extension, size and magic before an upload is started', async () => {
    expect(inspectionFileType('Prüfung.PDF', 1)).toBe('application/pdf');
    for (const [name, size] of [['original.pdf', 0], ['original.pdf', maximumOriginalBytes + 1], ['bad\n.pdf', 5], ['report.exe', 5], ['..\\report.pdf', 5]] as const)
      expect(() => inspectionFileType(name, size)).toThrow();
    await expect(validateOriginalSignature(body, 'application/pdf')).resolves.toBeUndefined();
    await expect(validateOriginalSignature(new Blob(['plain text']), 'application/pdf')).rejects.toThrow();
    await expect(validateOriginalSignature(new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])]), 'image/png')).resolves.toBeUndefined();
    await expect(validateOriginalSignature(new Blob([new Uint8Array([255, 216, 255])]), 'image/jpeg')).resolves.toBeUndefined();
  });
  it('requires matching bytes, metadata and HTTP checksum on download', async () => {
    const doc = document({ sha256: await sha256Blob(body) });
    const response = (data: Blob = body, digest: string = doc.sha256, type = doc.contentType) => new Response(data,
      { headers: { 'Content-Type': type, 'X-Content-SHA256': digest } });
    expect((await verifiedInspectionDownload(response(), doc)).size).toBe(body.size);
    await expect(verifiedInspectionDownload(response(body, 'c'.repeat(64)), doc)).rejects.toThrow();
    await expect(verifiedInspectionDownload(response(body, doc.sha256, 'text/html'), doc)).rejects.toThrow();
    await expect(verifiedInspectionDownload(response(new Blob(['%PDF-1.7\nChanged__original\n%%EOF'])), doc)).rejects.toThrow();
    const fake = new Blob(['wrong signature']); const fakeDoc = document({ size: fake.size, sha256: await sha256Blob(fake) });
    await expect(verifiedInspectionDownload(response(fake, fakeDoc.sha256), fakeDoc)).rejects.toThrow();
  });
});
