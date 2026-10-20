import { decimalInput } from './decimal-input';
import { asObject, canonicalJson, isDigest } from './technical-workbench.models';

export type TradeKind = 'gaeb' | 'e-invoice';
export const maximumTradeBytes = 1024 * 1024;
export interface TradeSource { name: string; blob: Blob; base64: string; sha256: string }
export interface GaebItem { positionKey: string; positionId: string; description: string; tenderQuantity: string | null; unit: string; offeredUnitPrice: string | null; itemDigest: string; reviewIssues: string[] }
export interface GaebPreview { contract: string; sourceSha256: string; exchangePhase: string; schemaVersion: string; schemaEdition: string; scheduleName: string; currency: string; items: GaebItem[]; reviewIssues: string[]; externalSchemaValidationPerformed: false }
export interface InvoiceLine { reference: string; description: string; quantity: string; unitCode: string; netAmount: string }
export interface InvoicePreview { contract: string; sourceSha256: string; syntax: string; profileIdentifier: string; invoiceNumber: string; documentKind: string; issueDate: string; currency: string; sellerName: string; buyerName: string; netAmount: string; taxAmount: string; grossAmount: string; payableAmount: string; lines: InvoiceLine[]; reviewIssues: string[]; externalValidationPerformed: false; mayPostAutomatically: false }
export type CostKey = 'unitMaterialCost' | 'unitLaborHours' | 'hourlyRate' | 'markupPercent' | 'vatPercent';
export interface CatalogSelection { positionKey: string; unitMaterialCost: string; unitLaborHours: string; hourlyRate: string; markupPercent: string; vatPercent: string; priceEvidence: string }
export interface CatalogRequest { sourceBase64: string; selections: CatalogSelection[] }
export interface CatalogCandidate extends Omit<CatalogSelection, 'priceEvidence'> { description: string; unit: string; tenderQuantity: string | null; offeredUnitPrice: string | null; origin: { sourceSha256: string; positionKey: string; itemDigest: string; exchangePhase: string; priceEvidence: string } }
export interface CatalogPreview { contract: string; sourceSha256: string; phase: string; candidates: CatalogCandidate[]; digest: string; reviewIssues: string[]; schemaValidated: false }
export interface CatalogReceipt { contract: string; sourceSha256: string; reviewedPreviewDigest: string; items: { positionKey: string; catalogItemId: string; name: string; unit: string }[] }
export const costFields: { key: CostKey; label: string }[] = [
  { key: 'unitMaterialCost', label: 'Eigene Materialkosten je Einheit (€)' }, { key: 'unitLaborHours', label: 'Eigene Arbeitsstunden je Einheit' }, { key: 'hourlyRate', label: 'Eigener Stundensatz (€)' }, { key: 'markupPercent', label: 'Zuschlag (%)' }, { key: 'vatPercent', label: 'Umsatzsteuer (%)' }
];
export function emptySelection(positionKey: string): CatalogSelection { return { positionKey, unitMaterialCost: '', unitLaborHours: '', hourlyRate: '', markupPercent: '', vatPercent: '', priceEvidence: '' }; }
export function sourceBudget(size: number): void { if (!Number.isSafeInteger(size) || size < 1 || size > maximumTradeBytes) throw new Error('Eine XML-Datei mit 1 Byte bis höchstens 1 MiB auswählen. PDF-Anhänge werden hier nicht verarbeitet.'); }
export function bytesBase64(bytes: Uint8Array): string { let binary = ''; for (let offset = 0; offset < bytes.length; offset += 16384) binary += String.fromCharCode(...bytes.subarray(offset, offset + 16384)); return btoa(binary); }
export async function readTradeSource(file: File): Promise<TradeSource> {
  sourceBudget(file.size); const buffer = await file.arrayBuffer(); sourceBudget(buffer.byteLength);
  try { new TextDecoder('utf-8', { fatal: true }).decode(buffer); } catch { throw new Error('Die Datei ist kein gültiges UTF-8. Originaldatei nicht automatisch umkodieren.'); }
  const digest = await crypto.subtle.digest('SHA-256', buffer);
  return { name: file.name, blob: new Blob([buffer], { type: 'application/xml;charset=utf-8' }), base64: bytesBase64(new Uint8Array(buffer)), sha256: Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('') };
}
function text(value: unknown): asserts value is string { if (typeof value !== 'string') throw new Error('Die Importvorschau enthält unvollständige Textfelder.'); }
function issues(value: unknown): asserts value is string[] { if (!Array.isArray(value) || value.some(x => typeof x !== 'string')) throw new Error('Die Prüfhinweise fehlen in der Vorschau.'); }
function decimal(value: unknown): asserts value is string { if (typeof value !== 'string' || !/^-?\d+(?:\.\d+)?$/.test(value)) throw new Error('Geldbeträge und Mengen müssen als exakte Dezimaltexte vorliegen.'); }
function optionalDecimal(value: unknown): void { if (value !== null) decimal(value); }
export function normalizedCost(value: string): string {
  const parsed = decimalInput(value);
  if (!/^\d+(?:\.\d{1,6})?$/.test(parsed)) throw new Error('Eigene Kosten, Stunden und Prozentsätze ausdrücklich als nichtnegative Dezimalzahl mit höchstens sechs Nachkommastellen eingeben.');
  const [whole, fraction = ''] = parsed.split('.'); const rest = fraction.replace(/0+$/, '');
  return whole.replace(/^0+(?=\d)/, '') + (rest ? '.' + rest : '');
}
export function validateGaebPreview(value: unknown, sourceSha: string): GaebPreview {
  const result = asObject(value);
  if (result['contract'] !== 'firstblick.gaeb-structural/1.0.0' || !isDigest(sourceSha) || result['sourceSha256'] !== sourceSha || result['externalSchemaValidationPerformed'] !== false || result['schemaVersion'] !== '3.3' || result['schemaEdition'] !== '2021-05' || !['X83', 'X84'].includes(String(result['exchangePhase']))) throw new Error('Dateiprüfsumme, GAEB-Version oder Prüfstatus der Vorschau stimmt nicht.');
  text(result['scheduleName']); text(result['currency']); issues(result['reviewIssues']);
  if (!Array.isArray(result['items']) || result['items'].length < 1 || result['items'].length > 1000) throw new Error('Die GAEB-Positionsliste ist unvollständig oder zu groß.');
  const positions = new Set<string>();
  for (const raw of result['items']) { const item = asObject(raw); for (const key of ['positionKey', 'positionId', 'description', 'unit']) text(item[key]); const key = item['positionKey'] as string; if (!key || positions.has(key) || !isDigest(item['itemDigest'])) throw new Error('GAEB-Positionskennungen oder Herkunftsdigests sind ungültig.'); positions.add(key); optionalDecimal(item['tenderQuantity']); optionalDecimal(item['offeredUnitPrice']); issues(item['reviewIssues']); }
  return value as GaebPreview;
}
export function validateInvoicePreview(value: unknown, sourceSha: string): InvoicePreview {
  const result = asObject(value);
  if (result['contract'] !== 'firstblick.einvoice-preview/1.0.0' || !isDigest(sourceSha) || result['sourceSha256'] !== sourceSha || result['externalValidationPerformed'] !== false || result['mayPostAutomatically'] !== false || result['currency'] !== 'EUR' || !['UBL-2.1-Invoice', 'UBL-2.1-CreditNote', 'CII-D16B-structure'].includes(String(result['syntax']))) throw new Error('Die E-Rechnungsvorschau ist nicht sicher an Datei und Prüfvertrag gebunden.');
  for (const key of ['invoiceNumber', 'documentKind', 'issueDate', 'sellerName', 'buyerName', 'profileIdentifier']) text(result[key]);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(result['issueDate'] as string)) throw new Error('Das Rechnungsdatum ist unvollständig.');
  for (const key of ['netAmount', 'taxAmount', 'grossAmount', 'payableAmount']) decimal(result[key]); issues(result['reviewIssues']);
  if (!Array.isArray(result['lines']) || result['lines'].length < 1 || result['lines'].length > 1000) throw new Error('Die Rechnungspositionen sind unvollständig oder zu groß.');
  const seen = new Set<string>();
  for (const raw of result['lines']) { const line = asObject(raw); for (const key of ['reference', 'description', 'unitCode']) text(line[key]); decimal(line['quantity']); decimal(line['netAmount']); const ref = line['reference'] as string; if (!ref || seen.has(ref)) throw new Error('Rechnungspositionen müssen eindeutige Kennungen haben.'); seen.add(ref); }
  return value as InvoicePreview;
}
export function canSelect(item: GaebItem): boolean { return !!item.description.trim() && item.description.length <= 240 && !!item.unit.trim() && !item.reviewIssues.includes('description_truncated_for_preview'); }
export function catalogRequest(source: TradeSource, preview: GaebPreview, selections: CatalogSelection[]): CatalogRequest {
  if (preview.sourceSha256 !== source.sha256 || preview.currency !== 'EUR') throw new Error('Für die Katalogübernahme ist eine dateigebundene Vorschau mit ausdrücklich deklarierter Währung EUR erforderlich.');
  if (selections.length < 1 || selections.length > 100 || new Set(selections.map(s => s.positionKey)).size !== selections.length) throw new Error('Ein bis 100 eindeutige Positionen auswählen.');
  return { sourceBase64: source.base64, selections: selections.map(selection => {
    const item = preview.items.find(i => i.positionKey === selection.positionKey); if (!item || !canSelect(item)) throw new Error('Eine gewählte Position ist unvollständig oder stammt nicht aus dieser Datei.');
    if (!selection.priceEvidence.trim() || selection.priceEvidence.length > 2000 || /[\u0000-\u001f\u007f-\u009f]/u.test(selection.priceEvidence)) throw new Error('Für jede Position eine nachvollziehbare Preisgrundlage mit höchstens 2.000 Zeichen angeben.');
    return { positionKey: selection.positionKey, unitMaterialCost: normalizedCost(selection.unitMaterialCost), unitLaborHours: normalizedCost(selection.unitLaborHours), hourlyRate: normalizedCost(selection.hourlyRate), markupPercent: normalizedCost(selection.markupPercent), vatPercent: normalizedCost(selection.vatPercent), priceEvidence: selection.priceEvidence.trim() };
  }) };
}
export function validateCatalogPreview(value: unknown, source: TradeSource, document: GaebPreview, request: CatalogRequest): CatalogPreview {
  const result = asObject(value);
  if (result['contract'] !== 'firstblick.gaeb-catalog-preview/1.0.0' || result['sourceSha256'] !== source.sha256 || result['phase'] !== document.exchangePhase || result['schemaValidated'] !== false || !isDigest(result['digest']) || !Array.isArray(result['candidates']) || result['candidates'].length !== request.selections.length) throw new Error('Die Katalogvorschau passt nicht zur geprüften Datei und Auswahl.');
  issues(result['reviewIssues']); const seen = new Set<string>();
  for (const raw of result['candidates']) {
    const candidate = asObject(raw), origin = asObject(candidate['origin']); const selection = request.selections.find(s => s.positionKey === candidate['positionKey']), line = document.items.find(i => i.positionKey === candidate['positionKey']);
    if (!selection || !line || seen.has(selection.positionKey) || candidate['description'] !== line.description || candidate['unit'] !== line.unit || origin['sourceSha256'] !== source.sha256 || origin['positionKey'] !== line.positionKey || origin['itemDigest'] !== line.itemDigest || origin['exchangePhase'] !== document.exchangePhase || origin['priceEvidence'] !== selection.priceEvidence) throw new Error('Die Katalogherkunft oder Positionszuordnung stimmt nicht.');
    seen.add(selection.positionKey); for (const field of costFields) { decimal(candidate[field.key]); if (normalizedCost(candidate[field.key] as string) !== normalizedCost(selection[field.key])) throw new Error('Eigene Kosten wurden in der Vorschau unerwartet verändert.'); }
  }
  return value as CatalogPreview;
}
export function validateCatalogReceipt(value: unknown, preview: CatalogPreview): CatalogReceipt {
  const result = asObject(value);
  if (result['contract'] !== 'firstblick.gaeb-catalog-import/1.0.0' || result['sourceSha256'] !== preview.sourceSha256 || result['reviewedPreviewDigest'] !== preview.digest || !Array.isArray(result['items']) || result['items'].length !== preview.candidates.length) throw new Error('Der Importbeleg ist unvollständig. Den Katalog prüfen, bevor eine weitere Übernahme gestartet wird.');
  const seen = new Set<string>(), ids = new Set<string>();
  for (const raw of result['items']) { const item = asObject(raw), candidate = preview.candidates.find(c => c.positionKey === item['positionKey']); if (!candidate || seen.has(candidate.positionKey) || typeof item['catalogItemId'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(item['catalogItemId']) || ids.has(item['catalogItemId']) || item['name'] !== candidate.description || item['unit'] !== candidate.unit) throw new Error('Der Importbeleg enthält eine abweichende oder doppelte Katalogzuordnung.'); seen.add(candidate.positionKey); ids.add(item['catalogItemId']); }
  return value as CatalogReceipt;
}
export function sameCatalogRequest(a: CatalogRequest, b: CatalogRequest): boolean { return canonicalJson(a) === canonicalJson(b); }
