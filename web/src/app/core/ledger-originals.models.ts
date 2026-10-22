import { InspectionDocument, inspectionFileType, verifiedInspectionDownload } from './equipment-documents.models';
import { recordDigest, recordId, recordInstant, recordObject, recordPage, recordRevision, recordText } from './ledger-contracts';

export type LedgerOriginalKind = 'customer-communications' | 'vehicle-costs';
export interface LedgerOriginalSource { id: string; tenantId: string; revision: number; digest: string; recordedAt: string }
export interface LedgerOriginalContext { sourceKind: LedgerOriginalKind; source: LedgerOriginalSource; title: string; parentKind: 'customers' | 'equipment'; parentId: string }
export interface LedgerOriginal extends Omit<InspectionDocument, 'sourceKind'> { sourceKind: LedgerOriginalKind }
export interface LedgerOriginals {
  sourceKind: LedgerOriginalKind; sourceId: string; sourceDigest: string; canAttach: boolean;
  items: LedgerOriginal[]; total: number; page: number; pageSize: number;
}
export const ledgerOriginalVersions: Record<LedgerOriginalKind, string> = {
  'customer-communications': 'customer-communication-original-v1', 'vehicle-costs': 'vehicle-cost-original-v1'
};
export { inspectionFileType as ledgerOriginalFileType, verifiedInspectionDownload as verifiedLedgerOriginalDownload };
export const isLedgerOriginalKind = (value: unknown): value is LedgerOriginalKind => value === 'customer-communications' || value === 'vehicle-costs';

export function validLedgerOriginalContext(raw: unknown, kind: LedgerOriginalKind, sourceId: string, digest: string, tenantId: string): LedgerOriginalContext {
  if (!recordObject(raw) || raw['sourceKind'] !== kind || !recordText(raw['title'], 240, true)
    || raw['parentKind'] !== (kind === 'customer-communications' ? 'customers' : 'equipment') || !recordId(raw['parentId'])
    || !recordObject(raw['source']) || raw['source']['id'] !== sourceId || !recordId(sourceId) || raw['source']['tenantId'] !== tenantId
    || raw['source']['revision'] !== 1 || !recordDigest(digest) || raw['source']['digest'] !== digest || !recordInstant(raw['source']['recordedAt']))
    throw new Error('Die Originalakte bestätigt nicht den ausgewählten Beleg und Betrieb. Laden Sie die Dokumentliste neu.');
  return raw as unknown as LedgerOriginalContext;
}

export function validLedgerOriginal(raw: unknown, kind: LedgerOriginalKind, source: LedgerOriginalSource, tenantId: string): LedgerOriginal {
  if (!recordObject(raw) || !recordId(raw['id']) || !recordId(source.id) || source.tenantId !== tenantId || source.revision !== 1
    || !recordDigest(source.digest) || !recordInstant(source.recordedAt) || raw['tenantId'] !== tenantId
    || raw['sourceKind'] !== kind || raw['sourceId'] !== source.id || raw['sourceDigest'] !== source.digest
    || raw['projectId'] !== null || raw['rendererVersion'] !== ledgerOriginalVersions[kind] || !recordRevision(raw['revision'])
    || typeof raw['name'] !== 'string' || !recordDigest(raw['sha256']) || !['Staged', 'Ready', 'Archived'].includes(String(raw['state']))
    || !recordInstant(raw['createdAt']) || Date.parse(raw['createdAt']) < Date.parse(source.recordedAt)
    || (raw['state'] === 'Staged' ? raw['versionId'] !== null || raw['revision'] !== 1
      : typeof raw['versionId'] !== 'string' || !raw['versionId'].trim() || raw['versionId'] === 'null' || Number(raw['revision']) < 2))
    throw new Error('Der Originalbeleg gehört nicht eindeutig zu diesem Betrieb und Originaleintrag oder enthält unvollständige Speichernachweise.');
  if (typeof raw['size'] !== 'number' || inspectionFileType(raw['name'], raw['size']) !== raw['contentType'])
    throw new Error('Dateiformat und gespeicherter Originalbeleg stimmen nicht überein.');
  return raw as unknown as LedgerOriginal;
}

export function validLedgerOriginals(raw: unknown, kind: LedgerOriginalKind, source: LedgerOriginalSource, tenantId: string, page: number): LedgerOriginals {
  if (!recordPage(raw, page, 20) || raw['sourceKind'] !== kind || raw['sourceId'] !== source.id || raw['sourceDigest'] !== source.digest
    || typeof raw['canAttach'] !== 'boolean')
    throw new Error('Die Originalbelege passen nicht zum ausgewählten Eintrag oder zur angeforderten Seite.');
  const ids = new Set<string>();
  for (const item of raw.items) {
    const document = validLedgerOriginal(item, kind, source, tenantId);
    if (ids.has(document.id)) throw new Error('Die Originalliste enthält doppelte Belege. Bitte neu laden.');
    ids.add(document.id);
  }
  return raw as unknown as LedgerOriginals;
}
