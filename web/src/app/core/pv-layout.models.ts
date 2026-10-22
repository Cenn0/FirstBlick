export type PvOrientation = 'Portrait' | 'Landscape' | 'MostModules';
export interface PvExclusion { name: string; x: number; y: number; widthM: number; heightM: number }
export interface PvInput {
  roofPlaneWidthM: number; roofPlaneHeightM: number;
  moduleWidthM: number; moduleHeightM: number; moduleRatedPowerWp: number;
  edgeClearanceM: number; moduleGapXM: number; moduleGapYM: number;
  exclusionClearanceM: number; orientation: PvOrientation;
  exclusions: PvExclusion[]; roofMeasurementSource: string; moduleDataSource: string;
}
export interface PvPlacement { row: number; column: number; x: number; y: number; widthM: number; heightM: number }
export interface PvResult {
  input: PvInput; selectedOrientation: 'Portrait' | 'Landscape'; roofPlaneAreaM2: number;
  gridCandidateCount: number; obstructionRejectedCount: number; installedModules: number;
  installedModuleAreaM2: number; nominalDcPowerWp: number;
  placements: PvPlacement[]; structureOrElectricalDesignApproved: false;
  provenance: { methodId: 'pv-roof-plane-grid'; methodVersion: '1.0.0'; inputSha256: string;
    isNormative: false; limitations: string[] };
}
export type PvDraft = {
  [K in keyof Omit<PvInput, 'orientation' | 'exclusions' | 'roofMeasurementSource' | 'moduleDataSource'>]: number | null;
} & { orientation: PvOrientation; exclusions: Array<{ name: string; x: number | null; y: number | null;
  widthM: number | null; heightM: number | null }>; roofMeasurementSource: string; moduleDataSource: string };

const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
function bounded(value: unknown, field: string, minimum: number, maximum: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum || value > maximum)
    throw new Error(field + ': endliche Zahl zwischen ' + minimum + ' und ' + maximum + ' erforderlich.');
  return value;
}
function source(value: unknown, field: string, max = 500): string {
  if (typeof value !== 'string' || !value.trim() || value.length > max || /[\u0000-\u001f\u007f-\u009f]/u.test(value))
    throw new Error(field + ': prüfbare, nichtleere Quellenangabe erforderlich.');
  return value.trim();
}
export function validPvInput(value: unknown): PvInput {
  if (!record(value)) throw new Error('Der PV-Entwurf enthält keine Eingaben.');
  const roofPlaneWidthM = bounded(value['roofPlaneWidthM'], 'Dachbreite', .1, 100);
  const roofPlaneHeightM = bounded(value['roofPlaneHeightM'], 'Dachhöhe', .1, 100);
  const moduleWidthM = bounded(value['moduleWidthM'], 'Modulbreite', .1, 5);
  const moduleHeightM = bounded(value['moduleHeightM'], 'Modulhöhe', .1, 5);
  const moduleRatedPowerWp = bounded(value['moduleRatedPowerWp'], 'Modulnennleistung', 10, 1500);
  const edgeClearanceM = bounded(value['edgeClearanceM'], 'Randabstand', 0, 5);
  const moduleGapXM = bounded(value['moduleGapXM'], 'Abstand x', 0, 1);
  const moduleGapYM = bounded(value['moduleGapYM'], 'Abstand y', 0, 1);
  const exclusionClearanceM = bounded(value['exclusionClearanceM'], 'Hindernisabstand', 0, 5);
  const orientation = value['orientation'];
  if (orientation !== 'Portrait' && orientation !== 'Landscape' && orientation !== 'MostModules')
    throw new Error('Modulausrichtung nicht unterstützt.');
  if (!Array.isArray(value['exclusions']) || value['exclusions'].length > 64)
    throw new Error('Es sind höchstens 64 Hindernisse zulässig.');
  const exclusions = value['exclusions'].map((item: unknown, i: number): PvExclusion => {
    if (!record(item)) throw new Error('Hindernis ' + (i + 1) + ' ist ungültig.');
    const name = source(item['name'], 'Hindernisbezeichnung', 200);
    const x = bounded(item['x'], 'Hindernis x', 0, roofPlaneWidthM);
    const y = bounded(item['y'], 'Hindernis y', 0, roofPlaneHeightM);
    const widthM = bounded(item['widthM'], 'Hindernisbreite', .001, roofPlaneWidthM);
    const heightM = bounded(item['heightM'], 'Hindernishöhe', .001, roofPlaneHeightM);
    if (x + widthM > roofPlaneWidthM + 1e-9 || y + heightM > roofPlaneHeightM + 1e-9)
      throw new Error('Hindernisse müssen vollständig innerhalb der Dachfläche liegen.');
    return { name, x, y, widthM, heightM };
  });
  return { roofPlaneWidthM, roofPlaneHeightM, moduleWidthM, moduleHeightM, moduleRatedPowerWp,
    edgeClearanceM, moduleGapXM, moduleGapYM, exclusionClearanceM, orientation, exclusions,
    roofMeasurementSource: source(value['roofMeasurementSource'], 'Aufmaßquelle'),
    moduleDataSource: source(value['moduleDataSource'], 'Modulquelle') };
}
const close = (a: number, b: number): boolean => Math.abs(a - b) <= 1e-7 * Math.max(1, Math.abs(a), Math.abs(b));
export function validPvResult(raw: unknown, requested: PvInput): PvResult {
  if (!record(raw) || !record(raw['input']) || !record(raw['provenance']))
    throw new Error('Die PV-Antwort ist unvollständig.');
  const input = validPvInput(raw['input']);
  if (JSON.stringify(input) !== JSON.stringify(requested))
    throw new Error('Die PV-Antwort enthält andere Eingaben als der bestätigte Entwurf.');
  const orientation = raw['selectedOrientation'];
  if ((orientation !== 'Portrait' && orientation !== 'Landscape') ||
      requested.orientation !== 'MostModules' && orientation !== requested.orientation ||
      raw['structureOrElectricalDesignApproved'] !== false)
    throw new Error('Der Server hat eine abweichende Ausrichtung oder Freigabe zurückgegeben.');
  const count = bounded(raw['installedModules'], 'Modulanzahl', 0, 2500);
  const candidates = bounded(raw['gridCandidateCount'], 'Rasterkandidaten', 0, 2500);
  const rejected = bounded(raw['obstructionRejectedCount'], 'Verworfene Kandidaten', 0, 2500);
  if (![count, candidates, rejected].every(Number.isInteger) || count + rejected !== candidates ||
      !Array.isArray(raw['placements']) || raw['placements'].length !== count)
    throw new Error('Die Modulanzahlen oder Rasterpositionen stimmen nicht überein.');
  const width = orientation === 'Landscape' ? input.moduleHeightM : input.moduleWidthM;
  const height = orientation === 'Landscape' ? input.moduleWidthM : input.moduleHeightM;
  for (const item of raw['placements']) {
    if (!record(item) || !Number.isSafeInteger(item['row']) || !Number.isSafeInteger(item['column']) ||
      (item['row'] as number) < 1 || (item['column'] as number) < 1)
      throw new Error('Eine Modulposition ist unvollständig.');
    const x = bounded(item['x'], 'Modulposition x', 0, 100);
    const y = bounded(item['y'], 'Modulposition y', 0, 100);
    if (!close(bounded(item['widthM'], 'Modulbreite', .1, 5), width) ||
        !close(bounded(item['heightM'], 'Modulhöhe', .1, 5), height) ||
        x + width > input.roofPlaneWidthM - input.edgeClearanceM + 1e-9 ||
        y + height > input.roofPlaneHeightM - input.edgeClearanceM + 1e-9 ||
        x < input.edgeClearanceM - 1e-9 || y < input.edgeClearanceM - 1e-9)
      throw new Error('Eine Modulposition verletzt die geometrischen Grenzen.');
  }
  const area = bounded(raw['roofPlaneAreaM2'], 'Dachfläche', 0, 10000);
  const usedArea = bounded(raw['installedModuleAreaM2'], 'Modulfläche', 0, 10000);
  const power = bounded(raw['nominalDcPowerWp'], 'Nennleistung', 0, 3750000);
  if (!close(area, input.roofPlaneWidthM * input.roofPlaneHeightM) ||
      !close(usedArea, count * input.moduleWidthM * input.moduleHeightM) ||
      !close(power, count * input.moduleRatedPowerWp))
    throw new Error('Flächen oder Modulnennleistung stimmen nicht mit der Stückzahl überein.');
  const provenance = raw['provenance'];
  if (provenance['methodId'] !== 'pv-roof-plane-grid' || provenance['methodVersion'] !== '1.0.0' ||
      provenance['isNormative'] !== false || typeof provenance['inputSha256'] !== 'string' ||
      !/^[a-f0-9]{64}$/.test(provenance['inputSha256']) ||
      !Array.isArray(provenance['limitations']) ||
      provenance['limitations'].length < 3 || provenance['limitations'].some(x => typeof x !== 'string' || !x.trim()))
    throw new Error('Die Berechnung ist nicht vollständig mit ihren Grenzen nachgewiesen.');
  return raw as unknown as PvResult;
}
