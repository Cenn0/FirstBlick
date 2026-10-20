import { asObject, isDigest } from './technical-workbench.models';
import { GaebPreview, InvoicePreview, TradeKind, maximumTradeBytes, validateGaebPreview, validateInvoicePreview } from './trade-import.models';

export interface OriginalSource {
  contract: string; id: string; tenantId: string; format: TradeKind; documentId: string;
  fileName: string; sha256: string; size: number; objectVersionId: string;
  receivedBy: string; receivedAt: string; digest: string;
}
export interface OriginalReceipt { source: OriginalSource; alreadyStored: boolean; contentVerified: true }
export interface OriginalPage { items: OriginalSource[]; total: number; page: number; pageSize: number; contentVerified: false }
export interface SourceCatalogLink { id: string; positionKey: string; name: string; unit: string; archived: boolean }
export interface OriginalDetail { source: OriginalSource; preview: GaebPreview | InvoicePreview; catalogItems: SourceCatalogLink[]; contentVerified: true }
const uuid = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(v) && v !== '00000000-0000-0000-0000-000000000000';
const visibleText = (v: unknown, maximum: number): v is string => typeof v === 'string' && !!v.trim() && v.length <= maximum && !/[\u0000-\u001f\u007f-\u009f]/u.test(v);
function bad(): never { throw new Error('Die Originalakte ist unvollständig oder gehört nicht zum aktuellen Betrieb und Dateiverfahren.'); }

export function originalSource(value: unknown, tenant: string, kind: TradeKind): OriginalSource {
  const s = asObject(value);
  if (s['contract'] !== 'firstblick.trade-source/1.0.0' || s['tenantId'] !== tenant || s['format'] !== kind
    || !uuid(s['id']) || !uuid(s['tenantId']) || !uuid(s['documentId']) || !uuid(s['receivedBy'])
    || !isDigest(s['sha256']) || !isDigest(s['digest']) || !visibleText(s['fileName'], 180)
    || !visibleText(s['objectVersionId'], 2048) || s['objectVersionId'] === 'null'
    || !Number.isSafeInteger(s['size']) || (s['size'] as number) < 1 || (s['size'] as number) > maximumTradeBytes
    || typeof s['receivedAt'] !== 'string' || !/(?:Z|\+00:00)$/.test(s['receivedAt']) || !Number.isFinite(Date.parse(s['receivedAt']))) bad();
  const name = s['fileName'] as string;
  if (name !== name.trim() || /[/\\:]/.test(name) || !(kind === 'gaeb' ? /\.(xml|x83|x84)$/i : /\.xml$/i).test(name)) bad();
  return value as OriginalSource;
}
export function originalReceipt(value: unknown, tenant: string, kind: TradeKind, sha: string, size: number): OriginalReceipt {
  const r = asObject(value), source = originalSource(r['source'], tenant, kind);
  if (r['contentVerified'] !== true || typeof r['alreadyStored'] !== 'boolean' || source.sha256 !== sha || source.size !== size) bad();
  return { source, alreadyStored: r['alreadyStored'], contentVerified: true };
}
export function originalPage(value: unknown, tenant: string, kind: TradeKind, page: number, pageSize: number): OriginalPage {
  const r = asObject(value);
  if (r['contentVerified'] !== false || r['page'] !== page || r['pageSize'] !== pageSize
    || !Number.isSafeInteger(r['total']) || (r['total'] as number) < 0 || !Array.isArray(r['items'])) bad();
  const items = (r['items'] as unknown[]).map(item => originalSource(item, tenant, kind));
  if (items.length !== Math.min(pageSize, Math.max(0, (r['total'] as number) - (page - 1) * pageSize))
    || new Set(items.map(x => x.id)).size !== items.length) bad();
  return { items, total: r['total'] as number, page, pageSize, contentVerified: false };
}
export function originalDetail(value: unknown, expected: OriginalSource): OriginalDetail {
  const r = asObject(value), source = originalSource(r['source'], expected.tenantId, expected.format);
  if (r['contentVerified'] !== true || source.id !== expected.id || source.digest !== expected.digest
    || source.sha256 !== expected.sha256 || source.documentId !== expected.documentId || source.size !== expected.size
    || source.objectVersionId !== expected.objectVersionId || source.fileName !== expected.fileName
    || !Array.isArray(r['catalogItems']) || r['catalogItems'].length > 1000) bad();
  const preview = source.format === 'gaeb' ? validateGaebPreview(r['preview'], source.sha256) : validateInvoicePreview(r['preview'], source.sha256);
  const links = r['catalogItems'] as unknown[]; const seen = new Set<string>(), positions = new Set<string>();
  if (source.format === 'e-invoice' && links.length) bad();
  const catalogItems = links.map(value => {
    const item = asObject(value);
    if (!uuid(item['id']) || !visibleText(item['positionKey'], 1000) || !visibleText(item['name'], 240)
      || !visibleText(item['unit'], 32) || typeof item['archived'] !== 'boolean'
      || seen.has(item['id']) || positions.has(item['positionKey'])) bad();
    if ('items' in preview && !preview.items.some(p => p.positionKey === item['positionKey'])) bad();
    seen.add(item['id'] as string); positions.add(item['positionKey'] as string);
    return value as SourceCatalogLink;
  });
  return { source, preview, catalogItems, contentVerified: true };
}

/** Validate a bounded byte stream against the captured original, not just its response headers. */
export async function verifiedOriginal(response: Response, source: OriginalSource): Promise<Blob> {
  if (!response.ok || response.headers.get('X-Content-SHA256') !== source.sha256
    || response.headers.get('Content-Type')?.split(';')[0] !== 'application/octet-stream' || !response.body)
    throw new Error('Die Originaldatei wurde nicht mit passender Prüfsumme und sicherem Downloadtyp geliefert.');
  const length = response.headers.get('Content-Length');
  if (length !== null && Number(length) !== source.size) throw new Error('Die Größe der Originaldatei stimmt nicht mit dem Eingangsbeleg überein.');
  const reader = response.body.getReader(); const buffer = new Uint8Array(source.size); let offset = 0;
  try {
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      if (offset + value.length > source.size) throw new Error('Der Originaldownload überschreitet die belegte Dateigröße.');
      buffer.set(value, offset); offset += value.length;
    }
  } catch (error) { await reader.cancel().catch(() => undefined); throw error; }
  finally { reader.releaseLock(); }
  if (offset !== source.size) throw new Error('Der Originaldownload wurde unvollständig übertragen.');
  const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', buffer)), b => b.toString(16).padStart(2, '0')).join('');
  if (hash !== source.sha256) throw new Error('Die Originalbytes stimmen nicht mit dem gespeicherten SHA-256 überein.');
  return new Blob([buffer], { type: 'application/octet-stream' });
}
