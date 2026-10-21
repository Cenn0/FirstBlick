import { describe, expect, it } from 'vitest';
import { GaebPreview } from '../src/app/core/trade-import.models';
import {
  applyGaebPriceWorksheet, exportGaebPriceWorksheet, gaebWorksheetColumns,
  maximumGaebWorksheetBytes, normalizeGaebUnitPrice, readGaebWorksheetFile,
  reviewGaebPriceWorksheet, GaebWorksheetPrice
} from '../src/app/core/gaeb-price-worksheet';

function source(count = 2): GaebPreview {
  return { contract: 'firstblick.gaeb-structural/1.0.0', sourceSha256: 'a'.repeat(64),
    exchangePhase: 'X83', schemaVersion: '3.3', schemaEdition: '2021-05', currency: 'EUR',
    scheduleName: 'Synthetisches LV', externalSchemaValidationPerformed: false, reviewIssues: [],
    items: Array.from({ length: count }, (_, i) => ({ positionKey: `01.${String(i + 1).padStart(4, '0')}`,
      positionId: `item-${i}`, description: `Leistung ${i + 1}`, tenderQuantity: '12.500',
      unit: 'm²', offeredUnitPrice: null, itemDigest: (i % 2 ? 'c' : 'b').repeat(64), reviewIssues: [] })) };
}
function prices(doc: GaebPreview): GaebWorksheetPrice[] {
  return doc.items.map(item => ({ positionKey: item.positionKey, expectedItemDigest: item.itemDigest,
    unitPrice: '', notOffered: false, priceEvidence: '' }));
}
function prepared(doc = source()): GaebWorksheetPrice[] {
  return prices(doc).map((p, i) => ({ ...p, unitPrice: i ? '' : '12.345678', notOffered: i > 0, priceEvidence: 'Synthetische Kalkulation; geprüft' }));
}
function replaceCell(csv: string, before: string, after: string): string {
  const q = (s: string) => '"' + s.replace(/"/g, '""') + '"';
  if (!csv.includes(q(before))) throw new Error('Invalid test mutation');
  return csv.replace(q(before), q(after));
}

describe('GAEB source-bound price worksheets', () => {
  it('round-trips pending rows without turning a missing price into zero', () => {
    const doc = source(), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    const review = reviewGaebPriceWorksheet(csv, doc, current);
    expect(review.pending).toBe(2); expect(review.missingEvidence).toBe(2); expect(review.changes).toEqual([]);
    const applied = applyGaebPriceWorksheet(review, doc, current);
    expect(applied).toEqual(current); expect(applied).not.toBe(current); expect(applied[0]).not.toBe(current[0]);
    expect(csv.startsWith('\ufeff')).toBe(true); expect(csv).toContain('"pos:01.0001"');
  });
  it('previews all changes and only applies after a separate confirmation call', () => {
    const doc = source(), current = prices(doc), target = prepared(doc), before = structuredClone(current);
    const review = reviewGaebPriceWorksheet(exportGaebPriceWorksheet(doc, target), doc, current);
    expect(current).toEqual(before); expect(review.changes).toHaveLength(2);
    expect(review.offered).toBe(1); expect(review.notOffered).toBe(1); expect(review.pending).toBe(0);
    expect(applyGaebPriceWorksheet(review, doc, current)).toEqual(target); expect(current).toEqual(before);
  });
  it('accepts reordered rows but restores original position order', () => {
    const doc = source(), current = prices(doc), csv = exportGaebPriceWorksheet(doc, prepared(doc));
    const [header, first, second] = csv.trimEnd().split('\r\n');
    const review = reviewGaebPriceWorksheet([header, second, first].join('\n'), doc, current);
    expect(applyGaebPriceWorksheet(review, doc, current)).toEqual(prepared(doc));
  });
  it('preserves literal descriptions with quotes, separators, newlines and formula-looking text', () => {
    const doc = source(1); doc.items[0].description = '=HYPERLINK("https://example.test");\r\nLiteral';
    doc.items[0].unit = '@SUM(1)'; doc.items[0].positionKey = '00001';
    const current = prices(doc); current[0].priceEvidence = '+cmd|literal';
    const csv = exportGaebPriceWorksheet(doc, current);
    expect(csv).toContain('"text:=HYPERLINK('); expect(csv).toContain('"unit:@SUM(1)"');
    expect(csv).toContain('"text:+cmd|literal"'); expect(csv).toContain('"pos:00001"');
    expect(applyGaebPriceWorksheet(reviewGaebPriceWorksheet(csv, doc, current), doc, current)).toEqual(current);
  });
  it('normalizes exact decimal commas and keeps an intentional zero distinct from pending', () => {
    const doc = source(), current = prices(doc), target = prepared(doc); target[0].unitPrice = '0,000000';
    const result = applyGaebPriceWorksheet(reviewGaebPriceWorksheet(exportGaebPriceWorksheet(doc, target), doc, current), doc, current);
    expect(result[0].unitPrice).toBe('0'); expect(result[0].notOffered).toBe(false);
  });
  it('allows an incomplete price-evidence draft but reports it as incomplete', () => {
    const doc = source(1), current = prices(doc); current[0].unitPrice = '1';
    const review = reviewGaebPriceWorksheet(exportGaebPriceWorksheet(doc, current), doc, current);
    expect(review.offered).toBe(1); expect(review.missingEvidence).toBe(1); expect(review.changes).toEqual([]);
  });
  it.each([
    ['source_sha256', 'sha256:' + 'a'.repeat(64), 'sha256:' + 'd'.repeat(64)],
    ['position_key', 'pos:01.0001', 'pos:01.9999'],
    ['item_sha256', 'sha256:' + 'b'.repeat(64), 'sha256:' + 'e'.repeat(64)],
    ['quantity', 'qty:12.500', 'qty:13'], ['unit', 'unit:m²', 'unit:m'],
    ['description', 'text:Leistung 1', 'text:Andere Leistung'], ['currency', 'EUR', 'USD']
  ])('rejects changed immutable %s without partial application', (_column, original, changed) => {
    const doc = source(), current = prices(doc), csv = exportGaebPriceWorksheet(doc, prepared(doc)), before = structuredClone(current);
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, original, changed), doc, current)).toThrow();
    expect(current).toEqual(before);
  });
  it('rejects duplicate rows, even when their total count matches', () => {
    const doc = source(), csv = exportGaebPriceWorksheet(doc, prices(doc)), [header, first] = csv.trimEnd().split('\r\n');
    expect(() => reviewGaebPriceWorksheet([header, first, first].join('\n'), doc, prices(doc))).toThrow(/doppelte/);
  });
  it('rejects missing rows and additional columns', () => {
    const doc = source(), csv = exportGaebPriceWorksheet(doc, prices(doc)), lines = csv.trimEnd().split('\r\n');
    expect(() => reviewGaebPriceWorksheet(lines.slice(0, 2).join('\n'), doc, prices(doc))).toThrow(/jede Originalposition/);
    expect(() => reviewGaebPriceWorksheet(csv.replace('\r\n', ';extra\r\n'), doc, prices(doc))).toThrow(/Spalten/);
  });
  it('rejects an unknown contract, reordered header and removed protective prefixes', () => {
    const doc = source(), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    for (const changed of [csv.replace('/1.0.0', '/9.0.0'), csv.replace('"contract";"source_sha256"', '"source_sha256";"contract"'), replaceCell(csv, 'text:', '=HYPERLINK("https://example.test")')])
      expect(() => reviewGaebPriceWorksheet(changed, doc, current)).toThrow();
  });
  it.each(['=1+2', '+2', '-1', '1e3', '1.234,56', '1,234.56', '0.0000001', '1000000000', '999999999.000001', 'NaN', 'Infinity', ''])('rejects unsafe or unsupported price %j', value => {
    expect(() => normalizeGaebUnitPrice(value)).toThrow();
  });
  it.each([['0', '0'], ['12,345678', '12.345678'], ['999999999.000000', '999999999'], [' 12.340000 ', '12.34']])('normalizes %s without floating-point arithmetic', (input, expected) => {
    expect(normalizeGaebUnitPrice(input)).toBe(expected);
  });
  it.each(['not-offered', 'pending'])('rejects a hidden price for decision %s', decision => {
    const doc = source(1), current = prices(doc), csv = exportGaebPriceWorksheet(doc, prepared(doc));
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, 'offer', decision), doc, current)).toThrow(/versteckten/);
  });
  it('rejects unknown decisions and offer without a price', () => {
    const doc = source(1), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, 'pending', 'yes'), doc, current)).toThrow(/Entscheidung/);
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, 'pending', 'offer'), doc, current)).toThrow(/Einheitspreis/);
  });
  it.each(['a'.repeat(2001), 'bad\nline', 'bad\tcell', 'bad\u0000cell'])('rejects overlong or control-bearing evidence', bad => {
    const doc = source(1), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, 'text:', 'text:' + bad), doc, current)).toThrow();
  });
  it.each(['"unterminated', '"value"suffix', 'bad"quote', ''])('rejects malformed CSV %j', bad => {
    const doc = source(1);
    expect(() => reviewGaebPriceWorksheet(bad, doc, prices(doc))).toThrow();
  });
  it('accepts unquoted valid cells, optional BOM and LF without dialect inference', () => {
    const doc = source(1), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current).replace(/^\ufeff/, '').replace(/"/g, '').replace(/\r\n/g, '\n');
    expect(reviewGaebPriceWorksheet(csv, doc, current).changes).toEqual([]);
    expect(() => reviewGaebPriceWorksheet(csv.replace(/;/g, ','), doc, current)).toThrow();
  });
  it('rejects blank extra rows and excessive cell or document sizes', () => {
    const doc = source(1), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    expect(() => reviewGaebPriceWorksheet(csv + '\n', doc, current)).toThrow();
    expect(() => reviewGaebPriceWorksheet(replaceCell(csv, 'text:', 'text:' + 'x'.repeat(8193)), doc, current)).toThrow(/Größenbudget/);
    expect(() => reviewGaebPriceWorksheet('x'.repeat(maximumGaebWorksheetBytes + 1), doc, current)).toThrow(/4 MiB/);
  });
  it('supports exactly 1000 source positions', () => {
    const doc = source(1000), current = prices(doc), csv = exportGaebPriceWorksheet(doc, current);
    expect(reviewGaebPriceWorksheet(csv, doc, current).pending).toBe(1000);
    const extra = csv + csv.split('\r\n')[1] + '\r\n';
    expect(() => reviewGaebPriceWorksheet(extra, doc, current)).toThrow(/1000/);
  });
  it('rejects unknown currency and incomplete current mappings before export', () => {
    const doc = source();
    expect(() => exportGaebPriceWorksheet({ ...doc, currency: 'USD' }, prices(doc))).toThrow(/EUR/);
    expect(() => exportGaebPriceWorksheet(doc, prices(doc).slice(0, 1))).toThrow();
    const duplicate = prices(doc); duplicate[1] = { ...duplicate[0] };
    expect(() => exportGaebPriceWorksheet(doc, duplicate)).toThrow(/doppelte/);
  });
  it('rejects stale price, evidence and original changes at confirmation', () => {
    const doc = source(), current = prices(doc), review = reviewGaebPriceWorksheet(exportGaebPriceWorksheet(doc, prepared(doc)), doc, current);
    for (const update of [{ unitPrice: '5' }, { priceEvidence: 'new evidence' }, { notOffered: true }]) {
      const edited = structuredClone(current); Object.assign(edited[0], update);
      expect(() => applyGaebPriceWorksheet(review, doc, edited)).toThrow(/seit der CSV-Prüfung/);
    }
    expect(() => applyGaebPriceWorksheet(review, { ...doc, sourceSha256: 'f'.repeat(64) }, current)).toThrow();
    const changed = structuredClone(doc); changed.items[0].unit = 'm';
    expect(() => applyGaebPriceWorksheet(review, changed, current)).toThrow();
  });
  it('freezes preview values and returns independent mutable copies for the form', () => {
    const doc = source(), current = prices(doc), review = reviewGaebPriceWorksheet(exportGaebPriceWorksheet(doc, prepared(doc)), doc, current);
    expect(Object.isFrozen(review)).toBe(true); expect(Object.isFrozen(review.changes[0].after)).toBe(true);
    const applied = applyGaebPriceWorksheet(review, doc, current); applied[0].unitPrice = '88';
    expect(review.changes[0].after.unitPrice).toBe('12.345678'); expect(current[0].unitPrice).toBe('');
  });
  it('reads only bounded strict UTF-8 including an optional BOM', async () => {
    expect(await readGaebWorksheetFile(new Blob(['\ufefftext:ä']))).toBe('text:ä');
    await expect(readGaebWorksheetFile(new Blob([new Uint8Array([0xc3, 0x28])]))).rejects.toThrow(/UTF-8/);
    await expect(readGaebWorksheetFile(new Blob([]))).rejects.toThrow(/1 Byte/);
    await expect(readGaebWorksheetFile(new Blob(['x'.repeat(maximumGaebWorksheetBytes + 1)]))).rejects.toThrow(/4 MiB/);
  });
  it('exports the eleven documented columns in stable order', () => {
    const doc = source(1), csv = exportGaebPriceWorksheet(doc, prices(doc));
    expect(csv.split('\r\n')[0]).toBe('\ufeff' + gaebWorksheetColumns.map(s => '"' + s + '"').join(';'));
  });
});
