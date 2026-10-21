/** UI contracts for the existing, explicitly non-normative server calculators. */
export type TechnicalMode = 'roof' | 'thermal' | 'moisture' | 'monthly' | 'tapered';
export interface NumericField { key: string; label: string; min: number; max: number }
export interface LayerDraft { name: string; thicknessM: number | null; conductivityWmK: number | null; vapourResistanceFactor: number | null; valueSource: string }
export interface ClimateDraft { month: number; insideTemperatureC: number | null; outsideTemperatureC: number | null; insideRelativeHumidityPercent: number | null; outsideRelativeHumidityPercent: number | null }
export interface CalculationProvenance { methodId: string; methodVersion: string; inputSha256: string; isNormative: false; limitations: string[] }
export interface CalculationResult { input: Record<string, unknown>; provenance: CalculationProvenance; [key: string]: unknown }
export interface SavedCalculation { id: string; tenantId: string; revision: number; projectId: string; title: string; method: string; methodVersion: string; input: Record<string, unknown>; result: CalculationResult; digest: string; isNormative: false }
export interface InterfacePoint { name: string; temperatureC: number; vapourPressurePa?: number; saturationPressurePa?: number; relativeHumidityPercent?: number; exceedsSaturation?: boolean }
export interface MonthlyPoint { month: number; screening: CalculationResult }
export interface Metric { label: string; value: number; unit: string }

const field = (key: string, label: string, min: number, max: number): NumericField => ({ key, label, min, max });
const temperature = [field('insideTemperatureC', 'Temperatur innen (°C)', -80, 120), field('outsideTemperatureC', 'Temperatur außen (°C)', -80, 120)];
const resistance = [field('insideSurfaceResistanceM2KW', 'Innerer Oberflächenwiderstand (m²K/W)', 0, 1), field('outsideSurfaceResistanceM2KW', 'Äußerer Oberflächenwiderstand (m²K/W)', 0, 1)];
export const climateFields: NumericField[] = [...temperature.map(f => ({ ...f, min: -50, max: 80 })), field('insideRelativeHumidityPercent', 'Relative Feuchte innen (%)', 0, 100), field('outsideRelativeHumidityPercent', 'Relative Feuchte außen (%)', 0, 100)];
export const technicalMethods: Record<TechnicalMode, { title: string; method: string; endpoint: string; provenance: string; fields: NumericField[] }> = {
  roof: { title: 'Dachgeometrie', method: 'roof.rectangle', endpoint: 'technical/roof/rectangle', provenance: 'rectangular-roof-plane', fields: [field('ridgeLengthM', 'Länge parallel zum First (m)', .000001, 100000), field('horizontalRunM', 'Waagerechte Breite (m)', .000001, 100000), field('riseM', 'Höhenunterschied (m)', 0, 100000)] },
  thermal: { title: 'Schichtaufbau & Wärme', method: 'thermal', endpoint: 'technical/thermal', provenance: 'solid-plane-resistance', fields: [...temperature, ...resistance] },
  moisture: { title: 'Feuchte – Einzelklima', method: 'moisture.screening', endpoint: 'technical/moisture/screening', provenance: 'steady-vapour-screening', fields: [...climateFields, ...resistance] },
  monthly: { title: 'Feuchte – 12 Monate', method: 'moisture.monthly-screening', endpoint: 'technical/moisture/monthly-screening', provenance: 'monthly-independent-vapour-screening', fields: [...resistance] },
  tapered: { title: 'Gefälledämmung', method: 'roof.tapered-insulation', endpoint: 'technical/roof/tapered-insulation', provenance: 'rectangular-linear-tapered-insulation', fields: [field('lengthM', 'Grundrisslänge (m)', .01, 1000), field('widthM', 'Grundrissbreite (m)', .01, 1000), field('minimumThicknessM', 'Mindestdicke am Ursprung (m)', .01, 2), field('lengthGradientPercent', 'Gefälle in Längsrichtung (%)', 0, 10), field('widthGradientPercent', 'Gefälle in Querrichtung (%)', 0, 10)] }
};
export const technicalModes = Object.keys(technicalMethods) as TechnicalMode[];
export const monthNames = ['Januar', 'Februar', 'März', 'April', 'Mai', 'Juni', 'Juli', 'August', 'September', 'Oktober', 'November', 'Dezember'];
export function emptyLayer(): LayerDraft { return { name: '', thicknessM: null, conductivityWmK: null, vapourResistanceFactor: null, valueSource: '' }; }
export function emptyClimates(): ClimateDraft[] { return Array.from({ length: 12 }, (_, i) => ({ month: i + 1, insideTemperatureC: null, outsideTemperatureC: null, insideRelativeHumidityPercent: null, outsideRelativeHumidityPercent: null })); }
export function hasLayers(mode: TechnicalMode): boolean { return mode === 'thermal' || mode === 'moisture' || mode === 'monthly'; }
export function hasDiffusion(mode: TechnicalMode): boolean { return mode === 'moisture' || mode === 'monthly'; }
function number(value: unknown, f: NumericField): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < f.min || value > f.max) throw new Error(`${f.label}: einen Wert zwischen ${f.min} und ${f.max} eingeben.`);
  return value === 0 ? 0 : value;
}
function label(value: string, name: string, max: number): string {
  if (!value?.trim() || value.length > max || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) throw new Error(`${name}: eine sichtbare Quellenangabe mit höchstens ${max} Zeichen eingeben.`);
  return value.trim();
}
export function calculationInput(mode: TechnicalMode, values: Record<string, number | null>, source: string, layers: LayerDraft[], months: ClimateDraft[]): Record<string, unknown> {
  const result: Record<string, unknown> = Object.fromEntries(technicalMethods[mode].fields.map(f => [f.key, number(values[f.key], f)]));
  if (!hasLayers(mode)) result['measurementSource'] = label(source, 'Quelle der Maße', 500);
  else {
    if (layers.length < 1 || layers.length > 60) throw new Error('Ein bis 60 Schichten sind erforderlich.');
    result['layers'] = layers.map(layer => ({
      name: label(layer.name, 'Material', 200),
      thicknessM: number(layer.thicknessM, field('thickness', 'Dicke (m)', .000001, 5)),
      conductivityWmK: number(layer.conductivityWmK, field('conductivity', 'Wärmeleitfähigkeit (W/mK)', .001, 500)),
      ...(hasDiffusion(mode) ? { vapourResistanceFactor: number(layer.vapourResistanceFactor, field('mu', 'Diffusionswiderstandszahl μ', 1, 1000000)) } : {}),
      valueSource: label(layer.valueSource, 'Quelle des Kennwerts', 500)
    }));
  }
  if (mode === 'monthly') {
    if (months.length !== 12 || new Set(months.map(m => m.month)).size !== 12 || months.some(m => !Number.isInteger(m.month) || m.month < 1 || m.month > 12)) throw new Error('Genau ein Klimadatensatz je Kalendermonat ist erforderlich.');
    result['months'] = [...months].sort((a, b) => a.month - b.month).map(month => ({ month: month.month, ...Object.fromEntries(climateFields.map(f => [f.key, number(month[f.key as keyof ClimateDraft], f)])) }));
  }
  return result;
}
/** Equality of finite JSON values, not a substitute for the server's SHA-256 evidence. */
export function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value !== null && typeof value === 'object') return `{${Object.entries(value).sort(([a], [b]) => a.localeCompare(b, 'en')).map(([key, child]) => `${JSON.stringify(key)}:${canonicalJson(child)}`).join(',')}}`;
  return JSON.stringify(value);
}
export function isDigest(value: unknown): value is string { return typeof value === 'string' && /^[a-f0-9]{64}$/.test(value); }
export function asObject(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Der Server hat unvollständige Berechnungsdaten geliefert.');
  return value as Record<string, unknown>;
}
function finite(value: unknown): asserts value is number { if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error('Der Server hat einen ungültigen Zahlenwert geliefert.'); }
function strings(value: unknown): asserts value is string[] { if (!Array.isArray(value) || value.some(x => typeof x !== 'string')) throw new Error('Die Verfahrensgrenzen fehlen in der Serverantwort.'); }
const metrics: Record<TechnicalMode, [string, string, string][]> = {
  roof: [['surfaceAreaM2', 'Tatsächliche geneigte Dachfläche', 'm²'], ['projectedAreaM2', 'Horizontale Projektionsfläche', 'm²'], ['slopingLengthM', 'Geneigte Länge', 'm'], ['pitchDegrees', 'Dachneigung', '°'], ['perimeterM', 'Umfang', 'm']],
  thermal: [['uValueWm2K', 'Stationärer Wärmedurchgangskoeffizient', 'W/(m²K)'], ['totalResistanceM2KW', 'Gesamtwiderstand', 'm²K/W'], ['heatFluxWm2', 'Wärmestromdichte', 'W/m²']],
  moisture: [['uValueWm2K', 'Stationärer Wärmedurchgangskoeffizient', 'W/(m²K)'], ['totalSdM', 'Diffusionsäquivalente Luftschichtdicke', 'm'], ['insideSurfaceTemperatureC', 'Temperatur der Innenoberfläche', '°C'], ['insideSurfaceRelativeHumidityPercent', 'Relative Feuchte der Innenoberfläche', '%']],
  monthly: [],
  tapered: [['projectedAreaM2', 'Horizontale Grundfläche', 'm²'], ['minimumThicknessM', 'Mindestdicke', 'm'], ['maximumThicknessM', 'Maximaldicke', 'm'], ['averageThicknessM', 'Mittlere Dicke', 'm'], ['calculatedVolumeM3', 'Dämmstoffvolumen ohne Verschnitt', 'm³']]
};
export function resultMetrics(mode: TechnicalMode, value: CalculationResult): Metric[] { return metrics[mode].map(([key, label, unit]) => ({ label, value: value[key] as number, unit })); }
export function resultInterfaces(value: CalculationResult): InterfacePoint[] { return Array.isArray(value['interfaces']) ? value['interfaces'] as InterfacePoint[] : []; }
export function resultMonths(value: CalculationResult): MonthlyPoint[] { return Array.isArray(value['months']) ? value['months'] as MonthlyPoint[] : []; }
export function validateCalculation(value: unknown, mode: TechnicalMode, expectedInput: Record<string, unknown>): CalculationResult {
  const result = asObject(value), provenance = asObject(result['provenance']);
  if (provenance['methodId'] !== technicalMethods[mode].provenance || provenance['methodVersion'] !== '1.0.0' || provenance['isNormative'] !== false || !isDigest(provenance['inputSha256'])) throw new Error('Methode, Version oder Nichtnormativ-Kennzeichnung der Serverantwort stimmt nicht.');
  strings(provenance['limitations']);
  if (provenance['limitations'].length === 0 || canonicalJson(result['input']) !== canonicalJson(expectedInput)) throw new Error('Die Serverantwort gehört nicht zu den aktuellen Eingaben.');
  for (const [key] of metrics[mode]) finite(result[key]);
  if (mode === 'thermal' || mode === 'moisture') {
    if (!Array.isArray(result['interfaces']) || result['interfaces'].length === 0) throw new Error('Die Grenzflächen fehlen in der Serverantwort.');
    for (const raw of result['interfaces']) {
      const point = asObject(raw); if (typeof point['name'] !== 'string') throw new Error('Die Bezeichnung einer Grenzfläche fehlt.'); finite(point['temperatureC']);
      if (mode === 'moisture') { for (const key of ['vapourPressurePa', 'saturationPressurePa', 'relativeHumidityPercent']) finite(point[key]); if (typeof point['exceedsSaturation'] !== 'boolean') throw new Error('Der Feuchtehinweis ist unvollständig.'); }
    }
  }
  if (hasDiffusion(mode) && result['requiresDetailedAssessment'] !== true) throw new Error('Das Feuchtescreening muss eine weitere fachliche Prüfung verlangen.');
  if (mode === 'moisture' && (typeof result['anyInterfaceSupersaturated'] !== 'boolean' || typeof result['insideSurfaceAboveEightyPercentRh'] !== 'boolean')) throw new Error('Die Feuchtehinweise fehlen.');
  if (mode === 'monthly') {
    const months = result['months'];
    if (!Array.isArray(months) || months.length !== 12) throw new Error('Die zwölf Monatsergebnisse sind unvollständig.');
    const expectedMonths = expectedInput['months'] as Record<string, unknown>[];
    months.forEach((raw, i) => {
      const month = asObject(raw); if (month['month'] !== i + 1) throw new Error('Die Monatsfolge ist unvollständig oder doppelt.');
      const { month: _month, ...climate } = expectedMonths[i];
      validateCalculation(month['screening'], 'moisture', { ...climate, insideSurfaceResistanceM2KW: expectedInput['insideSurfaceResistanceM2KW'], outsideSurfaceResistanceM2KW: expectedInput['outsideSurfaceResistanceM2KW'], layers: expectedInput['layers'] });
    });
    for (const [key, flag] of [['supersaturatedMonths', 'anyInterfaceSupersaturated'], ['highInteriorSurfaceHumidityMonths', 'insideSurfaceAboveEightyPercentRh']]) {
      const expected = months.filter(m => asObject(asObject(m)['screening'])[flag] === true).map(m => asObject(m)['month']);
      if (canonicalJson(result[key]) !== canonicalJson(expected)) throw new Error('Die Monatszusammenfassung widerspricht den Einzelwerten.');
    }
  }
  if (mode === 'tapered') {
    if (!Array.isArray(result['corners']) || result['corners'].length !== 4) throw new Error('Die vier Eckdicken fehlen.');
    for (const raw of result['corners']) { const corner = asObject(raw); for (const key of ['x', 'y', 'thicknessM']) finite(corner[key]); }
  }
  return value as CalculationResult;
}
export function validateSavedCalculation(value: unknown, mode: TechnicalMode, input: Record<string, unknown>, tenantId: string, projectId: string): SavedCalculation {
  const record = asObject(value);
  if (typeof record['id'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(record['id']) || !Number.isInteger(record['revision']) || Number(record['revision']) < 1 || record['tenantId'] !== tenantId || record['projectId'] !== projectId || record['method'] !== technicalMethods[mode].method || record['methodVersion'] !== '1.0.0' || record['isNormative'] !== false || !isDigest(record['digest']) || canonicalJson(record['input']) !== canonicalJson(input)) throw new Error('Der Speicherbeleg passt nicht zu Betrieb, Projekt oder Berechnung. Bitte den Verlauf prüfen.');
  validateCalculation(record['result'], mode, input);
  return value as SavedCalculation;
}
