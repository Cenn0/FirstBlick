import { describe, expect, it } from 'vitest';
import { LedgerOriginal, LedgerOriginalKind, ledgerOriginalVersions, validLedgerOriginal, validLedgerOriginalContext, validLedgerOriginals, verifiedLedgerOriginalDownload } from '../src/app/core/ledger-originals.models';
import { sha256Blob } from '../src/app/core/integrations.models';

const source = { id: '73000000-0000-4000-8000-000000000001', tenantId: 'tenant-a', revision: 1, digest: 'a'.repeat(64), recordedAt: '2026-10-22T01:00:00Z' };
const bytes = new Blob(['%PDF-1.7\nSynthetic ledger original\n%%EOF'], { type: 'application/pdf' });
const document = (kind: LedgerOriginalKind, changes: Partial<LedgerOriginal> = {}): LedgerOriginal => ({ id: '73000000-0000-4000-8000-000000000002',
  tenantId: 'tenant-a', name: 'Original.pdf', contentType: 'application/pdf', size: bytes.size, sha256: 'b'.repeat(64), versionId: 'retained-version-1',
  state: 'Ready', revision: 2, projectId: null, sourceKind: kind, sourceId: source.id, sourceDigest: source.digest,
  rendererVersion: ledgerOriginalVersions[kind], createdAt: '2026-10-22T02:00:00Z', ...changes });

for (const kind of ['customer-communications', 'vehicle-costs'] as const) describe(`${kind} original files`, () => {
  it('opens only the original source confirmed by the selected document and active tenant', () => {
    const context = { sourceKind: kind, source, title: 'Originaleintrag', parentKind: kind === 'customer-communications' ? 'customers' : 'equipment', parentId: '73000000-0000-4000-8000-000000000003' };
    expect(validLedgerOriginalContext(context, kind, source.id, source.digest, 'tenant-a')).toEqual(context);
    for (const changed of [{ ...context, source: { ...source, id: context.parentId } }, { ...context, source: { ...source, tenantId: 'tenant-b' } },
      { ...context, source: { ...source, digest: 'b'.repeat(64) } }, { ...context, source: { ...source, revision: 2 } },
      { ...context, parentKind: 'projects' }, { ...context, sourceKind: 'invoices' }, { ...context, parentId: '' }])
      expect(() => validLedgerOriginalContext(changed, kind, source.id, source.digest, 'tenant-a')).toThrow();
  });
  it('binds ready, staged and archived files to the exact immutable source', () => {
    expect(validLedgerOriginal(document(kind), kind, source, 'tenant-a').state).toBe('Ready');
    expect(validLedgerOriginal(document(kind, { state: 'Staged', revision: 1, versionId: null }), kind, source, 'tenant-a').state).toBe('Staged');
    expect(validLedgerOriginal(document(kind, { state: 'Archived', revision: 3 }), kind, source, 'tenant-a').state).toBe('Archived');
  });
  it.each([
    { tenantId: 'tenant-b' }, { sourceId: '73000000-0000-4000-8000-000000000004' }, { sourceDigest: 'c'.repeat(64) },
    { rendererVersion: 'unsupported' }, { versionId: null }, { versionId: 'null' }, { revision: 1 }, { createdAt: '2026-10-21T00:00:00Z' },
    { projectId: source.id }, { sha256: 'bad' }, { contentType: 'image/png' }, { size: 0 }, { name: '../Original.pdf' }, { state: 'Staged', versionId: null, revision: 2 }
  ])('rejects substituted or incomplete metadata %j', changes => expect(() => validLedgerOriginal(document(kind, changes as Partial<LedgerOriginal>), kind, source, 'tenant-a')).toThrow());
  it('rejects another source kind and an invalid original revision or active tenant', () => {
    const other = kind === 'vehicle-costs' ? 'customer-communications' : 'vehicle-costs';
    expect(() => validLedgerOriginal(document(other), kind, source, 'tenant-a')).toThrow();
    expect(() => validLedgerOriginal(document(kind), kind, { ...source, revision: 2 }, 'tenant-a')).toThrow();
    expect(() => validLedgerOriginal(document(kind), kind, source, 'tenant-b')).toThrow();
  });
  it('validates page, capabilities and each unique file without inferring upload authority', () => {
    const page = { sourceKind: kind, sourceId: source.id, sourceDigest: source.digest, canAttach: false, items: [document(kind)], total: 1, page: 1, pageSize: 20 };
    expect(validLedgerOriginals(page, kind, source, 'tenant-a', 1).canAttach).toBe(false);
    for (const changed of [{ ...page, canAttach: undefined }, { ...page, sourceDigest: 'c'.repeat(64) }, { ...page, page: 2 },
      { ...page, items: [document(kind), document(kind)], total: 2 }, { ...page, pageSize: 25 }, { ...page, total: 0 }])
      expect(() => validLedgerOriginals(changed, kind, source, 'tenant-a', 1)).toThrow();
  });
  it('requires matching download headers and retained bytes', async () => {
    const doc = document(kind, { sha256: await sha256Blob(bytes) });
    const response = (body = bytes, hash = doc.sha256, type = doc.contentType) => new Response(body, { headers: { 'Content-Type': type, 'X-Content-SHA256': hash } });
    await expect(verifiedLedgerOriginalDownload(response(), doc)).resolves.toHaveProperty('size', bytes.size);
    await expect(verifiedLedgerOriginalDownload(response(bytes, 'c'.repeat(64)), doc)).rejects.toThrow();
    await expect(verifiedLedgerOriginalDownload(response(bytes, doc.sha256, 'text/html'), doc)).rejects.toThrow();
    await expect(verifiedLedgerOriginalDownload(response(new Blob(['%PDF-1.7\nChanged ledger original__\n%%EOF'])), doc)).rejects.toThrow();
  });
});
