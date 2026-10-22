export type RoofAction = 'drainage' | 'wind' | 'snow';
export type ActionInput = Record<string, unknown>;
export interface ActionField { key: string; label: string; min: number; max: number; positive?: boolean }
export interface ActionDefinition {
  title: string; sourceKey: string; sourceLabel: string; rowsKey: string;
  globals: ActionField[]; fields: ActionField[]; sources: { key: string; label: string }[];
  columns: { key: string; label: string }[]; methodId: string; approvalKey: string; explanation: string;
}
const area = (key: string, label: string): ActionField => ({ key, label, min: 0, max: 1000000, positive: true });
export const roofActions: Record<RoofAction, ActionDefinition> = {
  drainage: {
    title: 'Entwässerungsbilanz', sourceKey: 'rainfallSource', rowsKey: 'catchments',
    sourceLabel: 'Regenquelle mit Ort, Datenstand, Dauer und Wiederkehrzeiten beider Ereignisse',
    globals: [
      { key: 'designRainfallLsHa', label: 'Berechnungsregen (l/s·ha)', min: 0, max: 10000, positive: true },
      { key: 'stormRainfallLsHa', label: 'Starkregen (l/s·ha)', min: 0, max: 10000, positive: true }
    ],
    fields: [area('effectiveAreaM2', 'Wirksame Einzugsfläche (m²)'),
      { key: 'designRunoffCoefficient', label: 'Abflussbeiwert beim Berechnungsregen', min: 0, max: 1, positive: true },
      { key: 'primaryCapacityLs', label: 'Belegte Hauptabflusskapazität (l/s)', min: 0, max: 1000000 },
      { key: 'emergencyCapacityLs', label: 'Belegte unabhängige Notabflusskapazität (l/s)', min: 0, max: 1000000 }],
    sources: [{ key: 'areaSource', label: 'Quelle der wirksamen Fläche und des Abflussbeiwerts' },
      { key: 'capacitySource', label: 'Kapazitätsnachweis mit Wasserständen, Produkt-/Leitungsstand und Fließwegen' }],
    columns: [{ key: 'primaryDemandLs', label: 'Hauptbedarf (l/s)' }, { key: 'stormInflowLs', label: 'Starkregenzufluss (l/s)' },
      { key: 'creditedPrimaryFlowLs', label: 'Angerechneter Hauptabfluss (l/s)' }, { key: 'emergencyDemandLs', label: 'Notabflussbedarf (l/s)' },
      { key: 'primaryDeficitLs', label: 'Hauptdefizit (l/s)' }, { key: 'emergencyDeficitLs', label: 'Notabflussdefizit (l/s)' }],
    methodId: 'supplied-catchment-flow-balance', approvalKey: 'hydraulicDesignApproved',
    explanation: 'Getrennte Einzugsflächen, keine Kapazitätsübertragung. Starkregen ohne Rückhaltegutschrift. Keine Rohrdimensionierung, Fließweg-, Rückstau- oder Überstauprüfung; ein rechnerisches Nulldefizit ist keine Entwässerungsfreigabe.'
  },
  wind: {
    title: 'Lokaler Winddruck', sourceKey: 'caseSource', rowsKey: 'zones',
    sourceLabel: 'Quelle des einzelnen Windfalls mit Richtung, Standort, Regelwerks-/Datenstand', globals: [],
    fields: [area('loadedAreaM2', 'Belastete lokale Fläche (m²)'),
      { key: 'externalPeakPressureKNm2', label: 'Äußerer Böengeschwindigkeitsdruck (kN/m²)', min: 0, max: 100 },
      { key: 'externalCoefficient', label: 'Äußerer Druckbeiwert cpe', min: -10, max: 10 },
      { key: 'internalPeakPressureKNm2', label: 'Innerer Bezugsdruck (kN/m²)', min: 0, max: 100 },
      { key: 'internalCoefficient', label: 'Innerer Druckbeiwert cpi', min: -10, max: 10 }],
    sources: [{ key: 'valueSource', label: 'Quellen für Fläche, Drücke und Beiwerte einschließlich jeweiliger Bezugshöhe' }],
    columns: [{ key: 'externalPressureKNm2', label: 'Außendruck (kN/m²)' }, { key: 'internalPressureKNm2', label: 'Innendruck (kN/m²)' },
      { key: 'netInwardPressureKNm2', label: 'Netto nach innen (kN/m²)' }, { key: 'normalForceKN', label: 'Lokale Normalkraft (kN)' },
      { key: 'outwardSuctionKNm2', label: 'Sog nach außen (kN/m²)' }],
    methodId: 'supplied-local-wind-pressure', approvalKey: 'structuralDesignApproved',
    explanation: 'Ein vorgegebener Windfall: positiver Nettodruck nach innen, negativer nach außen. Keine automatische Zonen-/Beiwertwahl, Lastkombination, Befestigerauslegung oder Tragwerksprüfung. Lokale Normalkräfte verschiedener Flächen werden nicht addiert.'
  },
  snow: {
    title: 'Vorgegebener Schneelastfall', sourceKey: 'caseSource', rowsKey: 'zones',
    sourceLabel: 'Quelle eines gleichzeitigen Lastfalls mit Standort, Höhenlage, Datenstand, sk, Ce und Ct',
    globals: [{ key: 'groundSnowLoadKNm2', label: 'Bodenschneelast sk (kN/m²)', min: 0, max: 100, positive: true },
      { key: 'exposureCoefficient', label: 'Expositionsbeiwert Ce', min: 0, max: 10, positive: true },
      { key: 'thermalCoefficient', label: 'Temperaturbeiwert Ct', min: 0, max: 10, positive: true }],
    fields: [area('horizontalAreaM2', 'Horizontale Projektionsfläche (m²)'),
      { key: 'shapeCoefficient', label: 'Vorgegebener Formbeiwert μ', min: 0, max: 20 }],
    sources: [{ key: 'valueSource', label: 'Quelle der überschneidungsfreien Fläche und des Formbeiwerts dieses Lastfalls' }],
    columns: [{ key: 'verticalLoadKNm2', label: 'Vertikallast auf Projektion (kN/m²)' },
      { key: 'verticalForceKN', label: 'Vertikalkraft (kN)' }],
    methodId: 'supplied-roof-snow-case', approvalKey: 'structuralDesignApproved',
    explanation: 'Nur überschneidungsfreie horizontale Projektionsflächen desselben gleichzeitigen Lastfalls. Keine automatische Verwehungs-, Abrutsch-, Extremfall- oder Formbeiwertwahl; keine Kombinationen, Auflagerkräfte oder Standsicherheitsfreigabe.'
  }
};
export interface ActionDraft { source: string; globals: Record<string, number | null>; scenario: string;
  rows: Array<{ name: string; values: Record<string, number | null>; sources: Record<string, string> }> }
export interface ActionResult { input: ActionInput; rows: Array<Record<string, string | number>>; raw: Record<string, unknown>;
  provenance: { methodId: string; methodVersion: string; inputSha256: string; isNormative: false; limitations: string[] } }
export const object = (x: unknown): x is Record<string, unknown> => !!x && typeof x === 'object' && !Array.isArray(x);
function label(value: unknown, max = 500): string {
  if (typeof value !== 'string' || !value.trim() || value.length > max || /[\u0000-\u001f\u007f-\u009f]/u.test(value))
    throw new Error('Bezeichnungen und nachvollziehbare Quellen vollständig ohne Steuerzeichen eingeben.');
  return value.trim();
}
function numeric(value: unknown, field: ActionField): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < field.min || value > field.max || field.positive && value === 0)
    throw new Error(field.label + ': gültigen Wert im angegebenen Bereich eingeben.');
  return value;
}
export function validActionInput(mode: RoofAction, raw: unknown): ActionInput {
  if (!object(raw)) throw new Error('Unvollständige Eingabe.');
  const def = roofActions[mode];
  const result: ActionInput = { [def.sourceKey]: label(raw[def.sourceKey]) };
  for (const field of def.globals) result[field.key] = numeric(raw[field.key], field);
  if (mode === 'drainage') {
    if (raw['emergencyScenario'] !== 'ResidualAfterPrimaryDesignFlow' && raw['emergencyScenario'] !== 'PrimaryUnavailable')
      throw new Error('Ein Notentwässerungsszenario ausdrücklich auswählen.');
    if ((result['stormRainfallLsHa'] as number) < (result['designRainfallLsHa'] as number))
      throw new Error('Starkregen darf nicht kleiner als der Berechnungsregen sein.');
    result['emergencyScenario'] = raw['emergencyScenario'];
  }
  const rows = raw[def.rowsKey];
  if (!Array.isArray(rows) || rows.length < 1 || rows.length > 32) throw new Error('1 bis 32 getrennte Bereiche eingeben.');
  const seen = new Set<string>();
  result[def.rowsKey] = rows.map((item: unknown) => {
    if (!object(item)) throw new Error('Unvollständiger Bereich.');
    const name = label(item['name'], 200);
    if (seen.has(name.toLowerCase())) throw new Error('Bereichsbezeichnungen müssen eindeutig sein.');
    seen.add(name.toLowerCase());
    const row: Record<string, string | number> = { name };
    for (const f of def.fields) row[f.key] = numeric(item[f.key], f);
    for (const s of def.sources) row[s.key] = label(item[s.key]);
    return row;
  });
  return result;
}
export function actionDraftInput(mode: RoofAction, draft: ActionDraft): ActionInput {
  const def = roofActions[mode];
  return validActionInput(mode, { [def.sourceKey]: draft.source, ...draft.globals,
    ...(mode === 'drainage' ? { emergencyScenario: draft.scenario } : {}),
    [def.rowsKey]: draft.rows.map(row => ({ name: row.name, ...row.values, ...row.sources })) });
}
function expectedRows(mode: RoofAction, input: ActionInput): Array<Record<string, number>> {
  const rows = input[roofActions[mode].rowsKey] as Array<Record<string, number>>;
  return rows.map((row): Record<string, number> => {
    if (mode === 'wind') {
      const external = row['externalPeakPressureKNm2'] * row['externalCoefficient'];
      const internal = row['internalPeakPressureKNm2'] * row['internalCoefficient'];
      const net = external - internal;
      return { externalPressureKNm2: external, internalPressureKNm2: internal,
        netInwardPressureKNm2: net, normalForceKN: net * row['loadedAreaM2'], outwardSuctionKNm2: Math.max(0, -net) };
    }
    if (mode === 'snow') {
      const load = row['shapeCoefficient'] * (input['groundSnowLoadKNm2'] as number) *
        (input['exposureCoefficient'] as number) * (input['thermalCoefficient'] as number);
      return { verticalLoadKNm2: load, verticalForceKN: load * row['horizontalAreaM2'] };
    }
    const primary = (input['designRainfallLsHa'] as number) * row['effectiveAreaM2'] * row['designRunoffCoefficient'] / 10000;
    const storm = (input['stormRainfallLsHa'] as number) * row['effectiveAreaM2'] / 10000;
    const credit = input['emergencyScenario'] === 'PrimaryUnavailable' ? 0 : Math.min(primary, row['primaryCapacityLs']);
    const emergency = Math.max(0, storm - credit);
    return { primaryDemandLs: primary, stormInflowLs: storm, creditedPrimaryFlowLs: credit, emergencyDemandLs: emergency,
      primaryDeficitLs: Math.max(0, primary - row['primaryCapacityLs']), emergencyDeficitLs: Math.max(0, emergency - row['emergencyCapacityLs']) };
  });
}
function equalNumber(actual: unknown, expected: number): boolean {
  return typeof actual === 'number' && Number.isFinite(actual) && Math.abs(actual - expected) <= 1e-9 * Math.max(1, Math.abs(expected));
}
export function validActionResult(mode: RoofAction, raw: unknown, requested: ActionInput): ActionResult {
  const def = roofActions[mode];
  if (!object(raw) || raw[def.approvalKey] !== false || !object(raw['provenance']))
    throw new Error('Unvollständige oder unzulässig als freigegeben markierte Serverantwort.');
  const input = validActionInput(mode, raw['input']);
  if (JSON.stringify(input) !== JSON.stringify(requested)) throw new Error('Die Antwort gehört nicht zur bestätigten Eingabe.');
  const p = raw['provenance'];
  if (p['methodId'] !== def.methodId || p['methodVersion'] !== '1.0.0' || p['isNormative'] !== false ||
      typeof p['inputSha256'] !== 'string' || !/^[a-f0-9]{64}$/.test(p['inputSha256']) ||
      !Array.isArray(p['limitations']) || p['limitations'].length < 4 || p['limitations'].some(x => typeof x !== 'string' || !x.trim() || x.length > 2000))
    throw new Error('Versionsgebundene Herkunft und Verfahrensgrenzen fehlen.');
  const expected = expectedRows(mode, input);
  const source = input[def.rowsKey] as Array<Record<string, unknown>>;
  const rows = raw[def.rowsKey];
  if (!Array.isArray(rows) || rows.length !== expected.length) throw new Error('Bereiche der Antwort stimmen nicht überein.');
  const checked = rows.map((row: unknown, index) => {
    if (!object(row) || row['name'] !== source[index]['name']) throw new Error('Falsche Bereichszuordnung.');
    const safe: Record<string, string | number> = { name: row['name'] as string };
    for (const col of def.columns) {
      if (!equalNumber(row[col.key], expected[index][col.key])) throw new Error('Widersprüchliche Rechenwerte in der Antwort.');
      safe[col.key] = row[col.key] as number;
    }
    return safe;
  });
  if (mode === 'snow' && !equalNumber(raw['totalVerticalForceKN'], expected.reduce((sum, z) => sum + z['verticalForceKN'], 0)))
    throw new Error('Widersprüchliche Gesamtvertikalkraft.');
  return { input, rows: checked, raw, provenance: p as unknown as ActionResult['provenance'] };
}
