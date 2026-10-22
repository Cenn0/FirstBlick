import { EquipmentInspection, validEquipmentId } from './equipment-workflow.models';
import { sha256Blob } from './integrations.models';

export const inspectionOriginalVersion = 'equipment-inspection-original-v1';
export const maximumOriginalBytes = 25 * 1024 * 1024;
export interface InspectionDocument {
  id: string; tenantId: string; name: string; contentType: string; size: number; sha256: string;
  versionId: string | null; state: 'Staged' | 'Ready' | 'Archived'; revision: number;
  projectId: null; sourceKind: 'equipment-inspections'; sourceId: string; sourceDigest: string;
  rendererVersion: string; createdAt: string;
}
export interface InspectionDocuments {
  inspectionId: string; equipmentId: string; sourceDigest: string;
  items: InspectionDocument[]; total: number; page: number; pageSize: number;
}

const types: Record<string, string> = { pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg' };
export function inspectionFileType(name: string, size: number): string {
  const type = types[name.split('.').pop()?.toLowerCase() || ''];
  if (!type || !name.trim() || name.length > 180 || /[\u0000-\u001f\u007f-\u009f/\\]/.test(name)
    || !Number.isSafeInteger(size) || size <= 0 || size > maximumOriginalBytes)
    throw new Error('Wählen Sie eine PDF-, PNG- oder JPEG-Datei zwischen einem Byte und 25 MiB mit einem einfachen Dateinamen.');
  return type;
}

export function validateInspectionDocument(raw: InspectionDocument, inspection: EquipmentInspection, tenantId: string): InspectionDocument {
  if (!raw || !inspection.completion || inspection.status !== 'Completed' || !validEquipmentId(raw.id)
    || raw.tenantId !== tenantId || raw.sourceKind !== 'equipment-inspections' || raw.sourceId !== inspection.id
    || raw.sourceDigest !== inspection.completion.digest || raw.projectId !== null
    || raw.rendererVersion !== inspectionOriginalVersion || !Number.isSafeInteger(raw.revision) || raw.revision < 1
    || typeof raw.name !== 'string' || typeof raw.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(raw.sha256)
    || !['Staged', 'Ready', 'Archived'].includes(raw.state) || typeof raw.createdAt !== 'string'
    || !Number.isFinite(Date.parse(raw.createdAt)) || Date.parse(raw.createdAt) < Date.parse(inspection.completion.recordedAt)
    || (raw.state === 'Staged' ? raw.versionId !== null
      : typeof raw.versionId !== 'string' || !raw.versionId.trim() || raw.versionId === 'null'))
    throw new Error('Der Originalbeleg gehört nicht zum ausgewählten Betrieb und Prüfergebnis oder enthält unvollständige Speichernachweise.');
  if (inspectionFileType(raw.name, raw.size) !== raw.contentType)
    throw new Error('Dateiformat und gespeicherter Originalbeleg stimmen nicht überein.');
  return raw;
}

export function validateInspectionDocuments(raw: InspectionDocuments, inspection: EquipmentInspection, tenantId: string, page: number): InspectionDocuments {
  if (!raw || !inspection.completion || raw.inspectionId !== inspection.id || raw.equipmentId !== inspection.equipmentId
    || raw.sourceDigest !== inspection.completion.digest || raw.page !== page || raw.pageSize !== 20
    || !Number.isSafeInteger(raw.total) || raw.total < 0 || !Array.isArray(raw.items) || raw.items.length > 20
    || raw.items.length > raw.total || raw.items.length > 0 && (page - 1) * 20 + raw.items.length > raw.total)
    throw new Error('Die Originalbelege passen nicht zur ausgewählten Prüfakte oder Seite.');
  const ids = new Set<string>();
  for (const item of raw.items) {
    validateInspectionDocument(item, inspection, tenantId);
    if (ids.has(item.id)) throw new Error('Die Originalliste enthält doppelte Belege. Bitte neu laden.');
    ids.add(item.id);
  }
  return raw;
}

export async function validateOriginalSignature(blob: Blob, type: string): Promise<void> {
  const bytes = new Uint8Array(await blob.slice(0, 8).arrayBuffer());
  const expected = type === 'application/pdf' ? [37, 80, 68, 70, 45]
    : type === 'image/png' ? [137, 80, 78, 71, 13, 10, 26, 10] : type === 'image/jpeg' ? [255, 216, 255] : [];
  if (!expected.length || expected.some((byte, index) => bytes[index] !== byte))
    throw new Error('Der Dateiinhalt passt nicht zum angegebenen PDF- oder Bildformat.');
}

export async function verifiedInspectionDownload(response: Response, document: InspectionDocument): Promise<Blob> {
  if (response.headers.get('Content-Type')?.split(';')[0].trim() !== document.contentType
    || response.headers.get('X-Content-SHA256') !== document.sha256)
    throw new Error('Der Download bestätigt nicht das gespeicherte Dateiformat und die Prüfsumme.');
  const blob = await response.blob();
  if (blob.size !== document.size || await sha256Blob(blob) !== document.sha256)
    throw new Error('Der Originalbeleg ist beschädigt oder seine Prüfsumme stimmt nicht.');
  await validateOriginalSignature(blob, document.contentType);
  return blob;
}
