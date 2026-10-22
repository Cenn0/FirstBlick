import { PvDraft, PvInput, PvResult, validPvInput, validPvResult } from './pv-layout.models';

export interface PvRoofSetInput {
  roofSetSource: string;
  planes: { name: string; layout: PvInput }[];
}
export interface PvRoofSetResult {
  input: PvRoofSetInput;
  installedModules: number;
  nominalDcPowerWp: number;
  roofPlaneAreaM2: number;
  installedModuleAreaM2: number;
  gridCandidateCount: number;
  obstructionRejectedCount: number;
  planes: { name: string; layout: PvResult }[];
  structureOrElectricalDesignApproved: false;
  provenance: { methodId: 'pv-roof-set-grid'; methodVersion: '1.0.0';
    inputSha256: string; isNormative: false; limitations: string[] };
}
export interface PvRoofSetDraft {
  roofSetSource: string;
  planes: { id: number; name: string; layout: PvDraft }[];
}
const isRecord = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const close = (a: number, b: number): boolean =>
  Math.abs(a - b) <= 1e-7 * Math.max(1, Math.abs(a), Math.abs(b));
export function validPvRoofSetInput(value: unknown): PvRoofSetInput {
  if (!isRecord(value) || !Array.isArray(value['planes']) ||
    value['planes'].length < 1 || value['planes'].length > 8)
    throw new Error('Eine PV-Dachgruppe benötigt 1 bis 8 getrennte Dachflächen.');
  const source = value['roofSetSource'];
  if (typeof source !== 'string' || source.length > 500 || !source.trim() ||
      /[\u0000-\u001f\u007f-\u009f]/u.test(source))
    throw new Error('Eine gültige Quelle der gesamten Dachgruppe ist erforderlich.');
  const seen = new Set<string>();
  const planes = value['planes'].map((item: unknown) => {
    if (!isRecord(item)) throw new Error('Eine Dachfläche ist unvollständig.');
    const name = item['name'];
    if (typeof name !== 'string' || !name.trim() || name.length > 200 ||
        /[\u0000-\u001f\u007f-\u009f]/u.test(name))
      throw new Error('Jede Dachfläche benötigt eine eindeutige Bezeichnung.');
    const normalized = name.trim();
    if (seen.has(normalized.toLowerCase()))
      throw new Error('Dachflächenbezeichnungen müssen eindeutig sein.');
    seen.add(normalized.toLowerCase());
    return { name: normalized, layout: validPvInput(item['layout']) };
  });
  return { roofSetSource: source.trim(), planes };
}
function finiteInt(value: unknown, max: number, label: string): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > max)
    throw new Error(label + ' ist ungültig.');
  return value;
}
function finiteTotal(value: unknown, max: number, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > max)
    throw new Error(label + ' ist ungültig.');
  return value;
}
export function validPvRoofSetResult(raw: unknown, requested: PvRoofSetInput): PvRoofSetResult {
  if (!isRecord(raw) || !isRecord(raw['input']) || !isRecord(raw['provenance']) ||
      !Array.isArray(raw['planes']))
    throw new Error('Die Antwort der Dachgruppenberechnung ist unvollständig.');
  const input = validPvRoofSetInput(raw['input']);
  if (JSON.stringify(input) !== JSON.stringify(requested) ||
      raw['planes'].length !== requested.planes.length ||
      raw['structureOrElectricalDesignApproved'] !== false)
    throw new Error('Die Dachgruppe der Serverantwort entspricht nicht der bestätigten Eingabe.');
  const planes = raw['planes'].map((plane: unknown, i: number) => {
    if (!isRecord(plane) || plane['name'] !== requested.planes[i].name)
      throw new Error('Dachflächenzuordnung in der Serverantwort ist ungültig.');
    return validPvResult(plane['layout'], requested.planes[i].layout);
  });
  const count = finiteInt(raw['installedModules'], 10000, 'Gesamtmodulzahl');
  const candidates = finiteInt(raw['gridCandidateCount'], 10000, 'Rasterkandidaten');
  const rejected = finiteInt(raw['obstructionRejectedCount'], 10000, 'Verworfene Kandidaten');
  if (count + rejected !== candidates ||
      count !== planes.reduce((sum, p) => sum + p.installedModules, 0) ||
      candidates !== planes.reduce((sum, p) => sum + p.gridCandidateCount, 0) ||
      rejected !== planes.reduce((sum, p) => sum + p.obstructionRejectedCount, 0))
    throw new Error('Die Summen der Dachflächen widersprechen den Einzelergebnissen.');
  const totals: Array<[string, keyof PvResult, number]> = [
    ['nominalDcPowerWp', 'nominalDcPowerWp', 15000000],
    ['roofPlaneAreaM2', 'roofPlaneAreaM2', 80000],
    ['installedModuleAreaM2', 'installedModuleAreaM2', 80000]
  ];
  for (const [field, key, max] of totals) {
    const actual = finiteTotal(raw[field], max, field);
    if (!close(actual, planes.reduce((sum, p) => sum + (p[key] as number), 0)))
      throw new Error('Die Gesamtwerte widersprechen den Dachflächenwerten.');
  }
  const provenance = raw['provenance'];
  if (provenance['methodId'] !== 'pv-roof-set-grid' || provenance['methodVersion'] !== '1.0.0' ||
      provenance['isNormative'] !== false || typeof provenance['inputSha256'] !== 'string' ||
      !/^[a-f0-9]{64}$/.test(provenance['inputSha256']) ||
      !Array.isArray(provenance['limitations']) || provenance['limitations'].length < 3 ||
      provenance['limitations'].some(x => typeof x !== 'string' || !x.trim()))
    throw new Error('Dachgruppenprovenienz und fachliche Grenzen fehlen.');
  return raw as unknown as PvRoofSetResult;
}
