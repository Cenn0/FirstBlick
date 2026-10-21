import { GaebPreview, validateGaebPreview } from './trade-import.models';

/** A FirstBlick working format, not a GAEB exchange phase or an accepted bid. */
export const gaebWorksheetContract = 'firstblick.gaeb-price-worksheet/1.0.0';
export const maximumGaebWorksheetBytes = 4 * 1024 * 1024;
export const gaebWorksheetColumns = [
  'contract', 'source_sha256', 'position_key', 'item_sha256', 'quantity', 'unit',
  'description', 'currency', 'decision', 'unit_price_eur', 'price_evidence'
] as const;
export interface GaebWorksheetPrice {
  positionKey: string; expectedItemDigest: string; unitPrice: string;
  notOffered: boolean; priceEvidence: string;
}
export type GaebWorksheetDecision = 'pending' | 'offer' | 'not-offered';
export interface GaebWorksheetChange {
  readonly positionKey: string;
  readonly before: Readonly<GaebWorksheetPrice>;
  readonly after: Readonly<GaebWorksheetPrice>;
}
export interface GaebWorksheetReview {
  readonly sourceSha256: string;
  readonly baseline: string;
  readonly csv: string;
  readonly changes: readonly GaebWorksheetChange[];
  readonly offered: number;
  readonly notOffered: number;
  readonly pending: number;
  readonly missingEvidence: number;
}

/** Decimal text throughout: no Number conversion, exponent, rounding or implicit zero. */
export function normalizeGaebUnitPrice(input: string): string {
  const value = input.trim().replace(',', '.');
  if (!/^(?:0|[1-9]\d{0,8})(?:\.\d{1,6})?$/.test(value))
    throw new Error('Einheitspreis ausdrücklich als nichtnegative Dezimalzahl mit höchstens sechs Nachkommastellen eingeben; keine Formel, Exponentialschreibweise oder Tausendertrennzeichen.');
  const [whole, fraction = ''] = value.split('.');
  if (BigInt(whole) * 1_000_000n + BigInt(fraction.padEnd(6, '0')) > 999_999_999_000_000n)
    throw new Error('Der Einheitspreis darf 999999999 EUR nicht überschreiten.');
  const tail = fraction.replace(/0+$/, '');
  return whole + (tail ? '.' + tail : '');
}
export function worksheetDecision(price: Readonly<GaebWorksheetPrice>): GaebWorksheetDecision {
  return price.notOffered ? 'not-offered' : price.unitPrice.trim() ? 'offer' : 'pending';
}
function evidence(value: string): string {
  if (value.length > 2000 || /[\u0000-\u001f\u007f-\u009f\ufeff]/u.test(value))
    throw new Error('Die Preisgrundlage darf höchstens 2000 Zeichen und keine Steuerzeichen enthalten.');
  // An empty value remains an explicitly incomplete draft. Final bid validation requires it.
  return value.trim();
}
function orderedPrices(preview: GaebPreview, prices: readonly GaebWorksheetPrice[]): GaebWorksheetPrice[] {
  validateGaebPreview(preview, preview.sourceSha256);
  if (preview.currency !== 'EUR') throw new Error('Die Preisarbeitsliste setzt eine im Original deklarierte Währung EUR voraus. Keine Währungsumrechnung.');
  if (prices.length !== preview.items.length) throw new Error('Für jede LV-Position ist genau eine Preiszeile erforderlich.');
  const indexed = new Map<string, GaebWorksheetPrice>();
  for (const price of prices) {
    if (!price || typeof price.positionKey !== 'string' || indexed.has(price.positionKey) ||
        typeof price.unitPrice !== 'string' || typeof price.priceEvidence !== 'string' || typeof price.notOffered !== 'boolean')
      throw new Error('Die Preiszeilen sind ungültig oder enthalten doppelte Positionen.');
    indexed.set(price.positionKey, price);
  }
  return preview.items.map(item => {
    const price = indexed.get(item.positionKey);
    if (!price || price.expectedItemDigest !== item.itemDigest)
      throw new Error('Eine Preiszeile gehört nicht zur aktuellen Originalposition.');
    return price;
  });
}
function baseline(preview: GaebPreview, prices: readonly GaebWorksheetPrice[]): string {
  const ordered = orderedPrices(preview, prices);
  return JSON.stringify({
    source: preview.sourceSha256, phase: preview.exchangePhase, version: preview.schemaVersion,
    edition: preview.schemaEdition, currency: preview.currency,
    items: preview.items.map(item => [item.positionKey, item.itemDigest, item.tenderQuantity, item.unit, item.description]),
    prices: ordered.map(p => [p.positionKey, p.expectedItemDigest, p.unitPrice, p.notOffered, p.priceEvidence])
  });
}
function budget(size: number): void {
  if (!Number.isSafeInteger(size) || size < 1 || size > maximumGaebWorksheetBytes)
    throw new Error('Die UTF-8-Preisarbeitsliste muss zwischen 1 Byte und 4 MiB groß sein.');
}
export async function readGaebWorksheetFile(file: Blob): Promise<string> {
  budget(file.size);
  const bytes = await file.arrayBuffer(); budget(bytes.byteLength);
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { throw new Error('Die Preisarbeitsliste ist kein gültiges UTF-8. Als CSV UTF-8 speichern; nicht stillschweigend umkodieren.'); }
}
function quote(value: string): string { return '"' + value.replace(/"/g, '""') + '"'; }

/**
 * Every nonnumeric user-controlled cell has a literal, required prefix. Quoting alone
 * does not stop spreadsheet formula execution. Prefixes also protect leading-zero IDs.
 * The importer deliberately rejects removed prefixes instead of guessing Excel coercions.
 */
export function exportGaebPriceWorksheet(preview: GaebPreview, prices: readonly GaebWorksheetPrice[]): string {
  const ordered = orderedPrices(preview, prices);
  const records: string[][] = [[...gaebWorksheetColumns]];
  preview.items.forEach((item, index) => {
    const price = ordered[index], decision = worksheetDecision(price);
    records.push([
      gaebWorksheetContract, 'sha256:' + preview.sourceSha256, 'pos:' + item.positionKey,
      'sha256:' + item.itemDigest, 'qty:' + (item.tenderQuantity ?? '?'), 'unit:' + item.unit,
      'text:' + item.description, 'EUR', decision,
      decision === 'offer' ? normalizeGaebUnitPrice(price.unitPrice) : '', 'text:' + evidence(price.priceEvidence)
    ]);
  });
  const csv = '\ufeff' + records.map(row => row.map(quote).join(';')).join('\r\n') + '\r\n';
  budget(new TextEncoder().encode(csv).byteLength);
  return csv;
}

/** Bounded semicolon CSV, with quoted newlines and escaped quotes but no dialect guessing. */
function parseCsv(input: string): string[][] {
  budget(new TextEncoder().encode(input).byteLength);
  const text = input.startsWith('\ufeff') ? input.slice(1) : input;
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(text))
    throw new Error('Die CSV-Datei enthält unzulässige Steuerzeichen.');
  const rows: string[][] = []; let row: string[] = [], cell = '';
  let state: 'start' | 'plain' | 'quoted' | 'closed' = 'start';
  const append = (character: string) => {
    cell += character;
    if (cell.length > 8192) throw new Error('Eine CSV-Zelle überschreitet das Größenbudget.');
  };
  const endCell = () => {
    row.push(cell); cell = ''; state = 'start';
    if (row.length > gaebWorksheetColumns.length) throw new Error('Die CSV-Datei enthält zusätzliche Spalten.');
  };
  const endRow = () => {
    endCell();
    if (row.length !== gaebWorksheetColumns.length) throw new Error('Jede CSV-Zeile muss genau die dokumentierten elf Spalten enthalten.');
    rows.push(row); row = [];
    if (rows.length > 1001) throw new Error('Die Preisarbeitsliste darf höchstens 1000 Positionen enthalten.');
  };
  for (let i = 0; i < text.length; i++) {
    const character = text[i];
    if (state === 'quoted') {
      if (character === '"') {
        if (text[i + 1] === '"') { append('"'); i++; } else state = 'closed';
      } else append(character);
    } else if (character === ';') endCell();
    else if (character === '\r' || character === '\n') {
      if (character === '\r' && text[i + 1] === '\n') i++;
      endRow();
    } else if (state === 'closed') throw new Error('Nach einem schließenden CSV-Anführungszeichen sind nur Trennzeichen oder Zeilenende zulässig.');
    else if (character === '"') {
      if (state !== 'start') throw new Error('CSV-Anführungszeichen müssen die gesamte Zelle umschließen.');
      state = 'quoted';
    } else { state = 'plain'; append(character); }
  }
  if (state === 'quoted') throw new Error('Eine CSV-Zelle hat kein schließendes Anführungszeichen.');
  if (row.length || cell.length || state !== 'start') endRow();
  if (!rows.length || rows[0].some((value, i) => value !== gaebWorksheetColumns[i]))
    throw new Error('Die Spaltenüberschrift entspricht nicht der FirstBlick-Preisarbeitsliste.');
  return rows.slice(1);
}

/** Atomic preview only: the caller's current prices are never mutated. */
export function reviewGaebPriceWorksheet(csv: string, preview: GaebPreview, current: readonly GaebWorksheetPrice[]): GaebWorksheetReview {
  const before = orderedPrices(preview, current), snapshot = baseline(preview, current);
  const rows = parseCsv(csv);
  if (rows.length !== preview.items.length) throw new Error('Die Arbeitsliste muss jede Originalposition genau einmal enthalten; keine Teilübernahme.');
  const sourceItems = new Map(preview.items.map(item => ['pos:' + item.positionKey, item]));
  const candidates = new Map<string, GaebWorksheetPrice>();
  let offered = 0, notOffered = 0, pending = 0, missingEvidence = 0;
  for (const row of rows) {
    const [contract, source, position, itemHash, quantity, unit, description, currency, decision, rawPrice, rawEvidence] = row;
    if (contract !== gaebWorksheetContract || source !== 'sha256:' + preview.sourceSha256)
      throw new Error('Vertrag oder Original-SHA-256 passt nicht zur ausgewählten GAEB-Datei.');
    const item = sourceItems.get(position);
    if (!item || candidates.has(item.positionKey)) throw new Error('Die Arbeitsliste enthält eine unbekannte oder doppelte Position.');
    if (itemHash !== 'sha256:' + item.itemDigest || quantity !== 'qty:' + (item.tenderQuantity ?? '?') ||
        unit !== 'unit:' + item.unit || description !== 'text:' + item.description || currency !== 'EUR')
      throw new Error(`Unveränderliche Quelldaten wurden bei Position ${item.positionKey} geändert. Nur Entscheidung, Preis und Preisgrundlage bearbeiten.`);
    if (!rawEvidence.startsWith('text:')) throw new Error('Das schützende text:-Präfix der Preisgrundlage darf nicht entfernt werden.');
    const priceEvidence = evidence(rawEvidence.slice(5));
    let unitPrice = '', skip = false;
    if (decision === 'offer') { unitPrice = normalizeGaebUnitPrice(rawPrice); offered++; }
    else if (decision === 'not-offered' || decision === 'pending') {
      if (rawPrice.trim()) throw new Error('Nicht angebotene oder noch offene Positionen dürfen keinen versteckten Einheitspreis enthalten.');
      skip = decision === 'not-offered'; if (skip) notOffered++; else pending++;
    } else throw new Error('Entscheidung muss genau pending, offer oder not-offered lauten.');
    if (!priceEvidence) missingEvidence++;
    candidates.set(item.positionKey, { positionKey: item.positionKey, expectedItemDigest: item.itemDigest, unitPrice, notOffered: skip, priceEvidence });
  }
  const changes: GaebWorksheetChange[] = [];
  before.forEach(price => {
    const after = candidates.get(price.positionKey)!;
    if (price.unitPrice !== after.unitPrice || price.notOffered !== after.notOffered || price.priceEvidence !== after.priceEvidence)
      changes.push(Object.freeze({ positionKey: price.positionKey, before: Object.freeze({ ...price }), after: Object.freeze({ ...after }) }));
  });
  return Object.freeze({ sourceSha256: preview.sourceSha256, baseline: snapshot, csv,
    changes: Object.freeze(changes), offered, notOffered, pending, missingEvidence });
}

/** Recheck both source and current edits at confirmation; never merge a stale review. */
export function applyGaebPriceWorksheet(review: GaebWorksheetReview, preview: GaebPreview, current: readonly GaebWorksheetPrice[]): GaebWorksheetPrice[] {
  if (review.sourceSha256 !== preview.sourceSha256 || review.baseline !== baseline(preview, current))
    throw new Error('Original oder aktuelle Preise wurden seit der CSV-Prüfung geändert. Arbeitsliste erneut prüfen.');
  const fresh = reviewGaebPriceWorksheet(review.csv, preview, current);
  const changes = new Map(fresh.changes.map(change => [change.positionKey, change.after]));
  return orderedPrices(preview, current).map(price => ({ ...(changes.get(price.positionKey) ?? price) }));
}
