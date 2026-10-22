import { canonicalJson, isDigest } from './technical-workbench.models';

export type AdvancedMode = 'balance' | 'drying' | 'boards' | 'assembly';
export type AdvancedResult = Record<string, unknown> & { input: Record<string, unknown>;
  provenance: { methodId: string; methodVersion: string; inputSha256: string; isNormative: false; limitations: string[] } };
export type AdvancedSaved = { id: string; revision: number; tenantId: string; projectId: string; method: string;
  methodVersion: string; input: unknown; result: AdvancedResult; digest: string; isNormative: false };
export const advancedMethods: Record<AdvancedMode, { name: string; method: string; endpoint: string; provenance: string }> = {
  balance: { name: 'Tauwasser- und Jahresbilanz (ISO 13788)', method: 'moisture.iso13788.balance',
    endpoint: 'technical/moisture/iso13788/balance', provenance: 'iso13788-monthly-diffusion-balance' },
  drying: { name: 'Austrocknung (ISO 13788, Abschnitt 7)', method: 'moisture.iso13788.drying',
    endpoint: 'technical/moisture/iso13788/drying', provenance: 'iso13788-excess-water-drying' },
  boards: { name: 'Gefälleplatten aus Herstellerkatalog', method: 'roof.tapered-board-layout',
    endpoint: 'technical/roof/tapered-board-layout', provenance: 'manufacturer-catalog-tapered-board-layout' },
  assembly: { name: 'Mehrfeld-Verlegeplanung', method: 'roof.tapered-assembly',
    endpoint: 'technical/roof/tapered-assembly', provenance: 'manufacturer-catalog-multifield-tapered-board-layout' }
};
type Row = Record<string, unknown>;
function require(value: unknown, message = 'Die Rechenantwort ist unvollständig oder widersprüchlich.'): asserts value {
  if (!value) throw new Error(message);
}
function row(value: unknown): Row {
  require(value !== null && typeof value === 'object' && !Array.isArray(value)); return value as Row;
}
function list(value: unknown, min: number, max: number): Row[] {
  require(Array.isArray(value) && value.length >= min && value.length <= max); return value.map(row);
}
function finite(value: unknown, min = 0, max = Number.MAX_VALUE): number {
  require(typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max); return value;
}
function integer(value: unknown, min: number, max: number): number {
  const n = finite(value, min, max); require(Number.isSafeInteger(n)); return n;
}
/** Explicit decimal wire amounts only; the UI accepts cents, never exponent or grouped money. */
function euro(value: unknown): number {
  require(typeof value === 'number' || typeof value === 'string' && /^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/.test(value));
  const amount = finite(typeof value === 'string' ? Number(value) : value, 0, 5e12);
  const cents = Math.round(amount * 100);
  require(Number.isSafeInteger(cents) && Math.abs(amount - cents / 100) <= 1e-10);
  return cents / 100;
}
/** The preview serializer writes decimals as strings; embedded historic result JSON uses numbers. */
function comparableResult(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(comparableResult);
  if (value !== null && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) =>
    [key, ['pricePerBoardEur', 'estimatedPackCostEur', 'totalEstimatedPackCostEur'].includes(key)
      ? item === null ? null : euro(item) : comparableResult(item)]));
  return value;
}
function close(actual: unknown, expected: number, tolerance = 1e-8): void {
  require(Math.abs(finite(actual, -Number.MAX_VALUE) - expected) <= tolerance * Math.max(1, Math.abs(expected)),
    'Die Mengen- oder Wasserbilanz der Rechenantwort ist widersprüchlich.');
}
function strings(value: unknown): void {
  require(Array.isArray(value) && value.length > 0 && value.length <= 100 &&
    value.every(x => typeof x === 'string' && x.trim().length > 0 && x.length <= 10000));
}
const ordinal = (a: unknown, b: unknown): number => String(a) < String(b) ? -1 : String(a) > String(b) ? 1 : 0;
/** Only documented server normalization: months, SKU/field order and decimal price serialization. */
export function normalizeAdvancedInput(input: unknown, mode: AdvancedMode): Row {
  const normalized = structuredClone(row(input));
  if (mode === 'drying') normalized['assembly'] = normalizeAdvancedInput(normalized['assembly'], 'balance');
  else if (mode === 'balance') normalized['months'] = list(normalized['months'], 12, 12).sort((a, b) => Number(a['month']) - Number(b['month']));
  else if (mode === 'assembly') normalized['fields'] = list(normalized['fields'], 1, 32).map((f): Row => ({ ...f,
    layout: normalizeAdvancedInput(f['layout'], 'boards') })).sort((a, b) => ordinal(a['fieldId'], b['fieldId']));
  else {
    const catalog = row(normalized['catalog']);
    catalog['boards'] = list(catalog['boards'], 1, 500).map(b => {
      const price = b['pricePerBoardEur'];
      if (typeof price === 'string') {
        require(/^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/.test(price)); b['pricePerBoardEur'] = finite(Number(price), 0, 10000000);
      }
      return b;
    }).sort((a, b) => ordinal(a['sku'], b['sku']));
  }
  return normalized;
}
function sameInput(actual: unknown, expected: unknown, mode: AdvancedMode): void {
  require(canonicalJson(normalizeAdvancedInput(actual, mode)) === canonicalJson(normalizeAdvancedInput(expected, mode)),
    'Die Rechenantwort gehört nicht zu den aktuellen Eingaben.');
}
function moisture(value: Row, mode: 'balance' | 'drying'): void {
  const standard = row(value['standardProcedure']);
  require(standard['standard'] === 'ISO 13788:2012' && standard['procedureVersion'] === '1.0.0' &&
    standard['implementsSpecifiedProcedure'] === true && standard['constructionComplianceAssessed'] === false &&
    standard['din4108Part3Assessment'] === false, 'Der ISO-Verfahrensnachweis behauptet eine ungeprüfte Freigabe.');
  strings(standard['requiredFurtherChecks']);
  const input = row(value['input']), assembly = mode === 'drying' ? row(input['assembly']) : input;
  const grid = list(value['grid'], 2, 256);
  grid.forEach((g, i) => {
    require(g['index'] === i && typeof g['name'] === 'string' && g['name'].trim().length > 0);
    finite(g['layerFraction'], 0, 1); integer(g['layerIndex'], -1, list(assembly['layers'], 1, 60).length - 1);
    const r = finite(g['cumulativeResistanceM2KW']), sd = finite(g['cumulativeSdM']);
    if (!i) require(r === 0 && sd === 0 && g['layerIndex'] === -1);
    else require(r >= Number(grid[i - 1]['cumulativeResistanceM2KW']) && sd > Number(grid[i - 1]['cumulativeSdM']));
  });
  const months = list(value['months'], mode === 'balance' ? 12 : 1, mode === 'balance' ? 12 : 120);
  const start = integer(mode === 'balance' ? value['startingMonth'] : input['startingMonth'], 1, 12);
  let water = mode === 'drying' ? 1 : 0, condensed = 0, evaporated = 0;
  const prior = new Map<number, number>(), maxima = new Map<number, { mass: number; month: number }>();
  if (mode === 'drying') {
    require(value['initialExcessMassKgM2'] === 1);
    const node = integer(value['initiallyWettedNode'], 1, grid.length - 2);
    require(grid[node]['layerIndex'] === input['wettedLayerIndex'] && grid[node]['layerFraction'] === .5);
    prior.set(node, 1);
  }
  for (const [i, month] of months.entries()) {
    const calendarMonth = (start - 1 + i) % 12 + 1;
    require(month['sequence'] === i + 1 && month['month'] === calendarMonth);
    const year = integer(assembly['climateCalendarYear'], 1900, 3000);
    require(month['days'] === new Date(Date.UTC(year, calendarMonth, 0)).getUTCDate());
    integer(month['subperiods'], 1, 258);
    for (const key of ['insideSurfaceResistanceM2KW', 'outsideSurfaceResistanceM2KW', 'insideVapourPressurePa',
      'outsideVapourPressurePa', 'insideSurfaceRelativeHumidityPercent', 'outsideSurfaceRelativeHumidityPercent']) finite(month[key]);
    finite(month['outsideThermalTemperatureC'], -50, 80);
    const c = finite(month['condensedKgM2']), e = finite(month['evaporatedKgM2']), end = finite(month['endStoredMassKgM2']);
    close(month['massConservationErrorKgM2'], 0, 1e-8 * Math.max(1, Math.abs(end - water))); close(end, water + c - e);
    const wet = list(month['wetInterfaces'], 0, grid.length - 2), seen = new Set<number>();
    let wetEnd = 0, wetC = 0, wetE = 0;
    for (const item of wet) {
      const node = integer(item['nodeIndex'], 1, grid.length - 2); require(!seen.has(node)); seen.add(node);
      finite(item['temperatureC'], -50, 80); finite(item['saturationPressurePa']); finite(item['endVapourPressurePa']);
      const before = finite(item['startMassKgM2']), after = finite(item['endMassKgM2']);
      const added = finite(item['condensedKgM2']), removed = finite(item['evaporatedKgM2']), peak = finite(item['maximumMassKgM2']);
      close(before, prior.get(node) ?? 0); close(after, before + added - removed);
      require(peak + 1e-10 >= Math.max(before, after) && peak <= before + added + 1e-10);
      if (!maxima.has(node) || peak > maxima.get(node)!.mass) maxima.set(node, { mass: peak, month: calendarMonth });
      prior.set(node, after); wetEnd += after; wetC += added; wetE += removed;
    }
    for (const [node, mass] of prior) require(seen.has(node) || mass <= 1e-12);
    close(end, wetEnd); close(c, wetC); close(e, wetE);
    water = end; condensed += c; evaporated += e;
  }
  const annual = list(value['interfaceMaxima'], 0, grid.length - 2), ids = new Set<number>();
  for (const item of annual) {
    const node = integer(item['nodeIndex'], 1, grid.length - 2); require(!ids.has(node)); ids.add(node);
    const peak = maxima.get(node); require(peak); close(item['maximumMassKgM2'], peak.mass);
    require(item['month'] === peak.month); close(item['endMassKgM2'], prior.get(node) ?? 0);
  }
  require([...maxima.entries()].filter(([, p]) => p.mass > 1e-12).every(([id]) => ids.has(id)));
  if (mode === 'balance') {
    close(value['totalCondensedKgM2'], condensed); close(value['totalEvaporatedKgM2'], evaporated);
    close(value['residualCondensateKgM2'], water);
    require(value['classification'] === (water > 1e-12 ? 'ResidualCondensate' : condensed > 1e-12 ? 'CondensateFullyReevaporates' : 'NoInterstitialCondensation'));
  } else {
    close(value['residualMassKgM2'], water);
    const moved = [...maxima].some(([node, peak]) => node !== value['initiallyWettedNode'] && peak.mass > 1e-12);
    require(water <= 1e-12 ? value['dryingTimeMonths'] === months.length &&
      value['classification'] === (moved ? 'DriedWithRedistribution' : 'DriedWithoutRedistribution') :
      value['dryingTimeMonths'] === null && months.length === 120 && value['classification'] === 'NotDryWithinTenYears');
  }
}
function boardLayout(value: Row, mode: 'boards' | 'assembly'): void {
  require(value['catalogIndependentlyVerified'] === false && value['drainageOrStructureApproved'] === false,
    'Der Plattennachweis behauptet eine ungeprüfte Hersteller- oder Entwässerungsfreigabe.');
  const input = row(value['input']), placements = list(value['placements'], 1, 2500), orders = list(value['orders'], 1, 2500);
  require(value['installedBoards'] === placements.length);
  close(value['roofAreaM2'], finite(input['roofLengthM']) * finite(input['roofWidthM']));
  const groups = new Map<string, { count: number; cuts: number; volume: number; waste: number; pack: number; price: number | null }>();
  const key = (catalog: Row, sku: unknown): string => canonicalJson(mode === 'assembly'
    ? [catalog['manufacturer'], catalog['productRange'], catalog['catalogRevision'], catalog['sourceReference'], sku] : sku);
  const cells = new Set<string>();
  let volume = 0, waste = 0, area = 0;
  for (const placement of placements) {
    const p = mode === 'assembly' ? row(placement['panel']) : placement;
    integer(p['row'], 1, 2500); integer(p['column'], 1, 2500);
    require(typeof p['sku'] === 'string' && p['sku'].length > 0 && typeof p['requiresCut'] === 'boolean');
    require(typeof p['rotationDegrees'] === 'number' && [0, 90, 180, 270].includes(p['rotationDegrees']));
    const l = finite(p['lengthM'], Number.MIN_VALUE), w = finite(p['widthM'], Number.MIN_VALUE);
    const thin = finite(p['thinEdgeM'], .01, 2), thick = finite(p['thickEdgeM'], .01, 2); require(thick >= thin);
    const x = finite(placement[mode === 'assembly' ? 'globalX' : 'x']), y = finite(placement[mode === 'assembly' ? 'globalY' : 'y']);
    require(x + l <= Number(input['roofLengthM']) + 1e-8 && y + w <= Number(input['roofWidthM']) + 1e-8);
    close(p['installedVolumeM3'], l * w * (thin + thick) / 2);
    if (mode === 'boards') {
      const catalog = row(input['catalog']), sku = list(catalog['boards'], 1, 500).find(b => b['sku'] === p['sku']);
      require(sku, 'Die Platzierung enthält einen Artikel außerhalb des erfassten Katalogs.');
      close(thin, finite(sku['thinEdgeM']), 1e-7);
      const run = finite(catalog['boardRunM']), cross = finite(catalog['boardCrossM']);
      const axisX = input['flowAxis'] === 'AlongLength', low = input['lowEdgeAtCoordinateZero'] === true;
      const flow = Number(input[axisX ? 'roofLengthM' : 'roofWidthM']), span = Number(input[axisX ? 'roofWidthM' : 'roofLengthM']);
      const columns = Math.ceil(flow / run - 1e-10), rows = Math.ceil(span / cross - 1e-10);
      const column = integer(p['column'], 1, columns), index = integer(p['row'], 1, rows);
      require(placements.length === rows * columns && !cells.has(`${index}/${column}`)); cells.add(`${index}/${column}`);
      const alongStart = (column - 1) * run, crossStart = (index - 1) * cross;
      const expectedRun = Math.min(run, flow - alongStart), expectedCross = Math.min(cross, span - crossStart);
      close(l, axisX ? expectedRun : expectedCross); close(w, axisX ? expectedCross : expectedRun);
      close(x, axisX ? (low ? alongStart : flow - alongStart - expectedRun) : crossStart);
      close(y, axisX ? crossStart : (low ? alongStart : flow - alongStart - expectedRun));
      close(thin, Number(input['lowEdgeThicknessM']) + alongStart * Number(input['slopePercent']) / 100);
      require(p['rotationDegrees'] === (axisX ? (low ? 0 : 180) : (low ? 90 : 270)));
      const along = input['flowAxis'] === 'AlongLength' ? l : w, across = input['flowAxis'] === 'AlongLength' ? w : l;
      require(along <= run + 1e-9 && across <= cross + 1e-9);
      close(thick, thin + along * Number(input['slopePercent']) / 100);
      close(p['cutWasteVolumeM3'], run * cross * (Number(sku['thinEdgeM']) + Number(sku['thickEdgeM'])) / 2 - Number(p['installedVolumeM3']));
      require(p['requiresCut'] === (along < run - 1e-9 || across < cross - 1e-9));
      const identity = key(catalog, sku['sku']), group = groups.get(identity) ?? {
        count: 0, cuts: 0, volume: 0, waste: 0, pack: integer(sku['unitsPerPack'], 1, 200),
        price: sku['pricePerBoardEur'] === null ? null : euro(sku['pricePerBoardEur']) };
      group.count++; group.cuts += p['requiresCut'] ? 1 : 0;
      group.volume += Number(p['installedVolumeM3']); group.waste += Number(p['cutWasteVolumeM3']); groups.set(identity, group);
    }
    volume += finite(p['installedVolumeM3']); waste += finite(p['cutWasteVolumeM3']); area += l * w;
  }
  close(value['roofAreaM2'], area); close(value['installedVolumeM3'], volume); close(value['cutWasteVolumeM3'], waste);
  if (mode === 'boards') {
    close(value['requiredContinuousVolumeM3'], volume);
    close(value['thinEdgeM'], finite(input['lowEdgeThicknessM']));
    close(value['highEdgeM'], Number(input['lowEdgeThicknessM']) + Number(input['slopePercent']) / 100 *
      Number(input[input['flowAxis'] === 'AlongLength' ? 'roofLengthM' : 'roofWidthM']));
  } else {
    const fields = list(value['fields'], 1, 32), expected = list(input['fields'], 1, 32);
    require(fields.length === expected.length);
    for (let i = 0; i < fields.length; i++) {
      const field = fields[i], source = expected[i];
      require(field['fieldId'] === source['fieldId'] && field['originX'] === source['originX'] && field['originY'] === source['originY']);
      const layout = validateAdvancedResult(field['layout'], 'boards', row(source['layout'])), catalog = row(layout.input['catalog']);
      for (const order of list(layout['orders'], 1, 2500)) {
        const sku = list(catalog['boards'], 1, 500).find(b => b['sku'] === order['sku']); require(sku);
        const identity = key(catalog, order['sku']), pack = Number(order['unitsPerPack']);
        const price = sku['pricePerBoardEur'] === null ? null : euro(sku['pricePerBoardEur']);
        const group = groups.get(identity) ?? { count: 0, cuts: 0, volume: 0, waste: 0, pack, price };
        require(group.pack === pack && group.price === price);
        group.count += Number(order['usedBoards']); group.cuts += Number(order['boardsRequiringCut']);
        group.volume += Number(order['installedVolumeM3']); group.waste += Number(order['unusedCutVolumeM3']); groups.set(identity, group);
      }
    }
    const expectedPlacements = fields.flatMap(f => list(row(f['layout'])['placements'], 1, 2500).map(p => ({
      fieldId: f['fieldId'], globalX: Number(f['originX']) + Number(p['x']), globalY: Number(f['originY']) + Number(p['y']), panel: p })));
    require(canonicalJson(placements) === canonicalJson(expectedPlacements));
  }
  let used = 0, cuts = 0, orderVolume = 0, orderWaste = 0, cost = 0, known = true;
  const identities = new Set<string>();
  for (const line of orders) {
    const order = mode === 'assembly' ? row(line['quantity']) : line;
    const identity = key(line, order['sku']);
    require(!identities.has(identity)); identities.add(identity);
    const count = integer(order['usedBoards'], 1, 2500), pack = integer(order['unitsPerPack'], 1, 200);
    const cut = integer(order['boardsRequiringCut'], 0, count), packs = Math.ceil(count / pack);
    const group = groups.get(identity); require(group && group.count === count && group.pack === pack && group.cuts === cut);
    close(order['installedVolumeM3'], group.volume); close(order['unusedCutVolumeM3'], group.waste);
    if (group.price === null) require(order['estimatedPackCostEur'] === null);
    else close(euro(order['estimatedPackCostEur']), Math.round(group.price * 100) * packs * pack / 100, 1e-10);
    require(order['packsToOrder'] === packs && order['boardsOrdered'] === packs * pack && order['additionalBoardsFromPacks'] === packs * pack - count);
    used += count; cuts += cut; orderVolume += finite(order['installedVolumeM3']); orderWaste += finite(order['unusedCutVolumeM3']);
    if (order['estimatedPackCostEur'] === null) known = false; else cost += euro(order['estimatedPackCostEur']);
  }
  require(identities.size === groups.size);
  close(used, placements.length); close(cuts, placements.filter(p => row(mode === 'assembly' ? p['panel'] : p)['requiresCut']).length);
  close(orderVolume, volume); close(orderWaste, waste);
  require(known ? value['totalEstimatedPackCostEur'] !== null : value['totalEstimatedPackCostEur'] === null);
  if (known) close(euro(value['totalEstimatedPackCostEur']), cost, 1e-10);
}
/** Integrity checks are client safeguards, not an independent engineering or standards approval. */
export function validateAdvancedResult(raw: unknown, mode: AdvancedMode, expectedInput: Row): AdvancedResult {
  const value = row(raw), provenance = row(value['provenance']);
  require(provenance['methodId'] === advancedMethods[mode].provenance && provenance['methodVersion'] === '1.0.0' &&
    provenance['isNormative'] === false && isDigest(provenance['inputSha256'])); strings(provenance['limitations']);
  sameInput(value['input'], expectedInput, mode);
  if (mode === 'balance' || mode === 'drying') moisture(value, mode); else boardLayout(value, mode);
  return value as AdvancedResult;
}
export function validateAdvancedSaved(raw: unknown, mode: AdvancedMode, preview: AdvancedResult,
  tenantId: string | null, projectId: string, title: string): AdvancedSaved {
  const value = row(raw);
  require(value['title'] === title && value['tenantId'] === tenantId && value['projectId'] === projectId && value['method'] === advancedMethods[mode].method &&
    value['methodVersion'] === '1.0.0' && value['isNormative'] === false && isDigest(value['digest']) &&
    typeof value['id'] === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value['id']) &&
    value['id'] !== '00000000-0000-0000-0000-000000000000', 'Der gespeicherte Bericht gehört nicht zum ausgewählten Betrieb, Verfahren oder Projekt.');
  integer(value['revision'], 1, Number.MAX_SAFE_INTEGER); sameInput(value['input'], preview.input, mode);
  const result = validateAdvancedResult(value['result'], mode, preview.input);
  require(canonicalJson(comparableResult(result)) === canonicalJson(comparableResult(preview)), 'Der gespeicherte Rechenbericht unterscheidet sich von der freigegebenen Vorschau.');
  return value as AdvancedSaved;
}
