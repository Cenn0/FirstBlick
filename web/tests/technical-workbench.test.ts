import { describe, expect, it } from 'vitest';
import { calculationInput, canonicalJson, emptyClimates, emptyInhomogeneousSection, emptyLayer, isDigest, technicalMethods, technicalModes, validateCalculation, validateSavedCalculation } from '../src/app/core/technical-workbench.models';

const source = 'Synthetic plan';
const roofInput = () => calculationInput('roof', { ridgeLengthM: 12, horizontalRunM: 4, riseM: 3 }, source, [], []);
const result = () => ({ input: roofInput(), surfaceAreaM2: 60, projectedAreaM2: 48, slopingLengthM: 5, pitchDegrees: 36.8699, perimeterM: 34, provenance: { methodId: 'rectangular-roof-plane', methodVersion: '1.0.0', inputSha256: 'a'.repeat(64), isNormative: false, limitations: ['Synthetic non-normative reference'] } });
const layer = () => ({ name: ' Synthetic ', thicknessM: .12, conductivityWmK: .04, vapourResistanceFactor: 10, valueSource: ' Test reference ' });

describe('technical workbench inputs', () => {
  it('defines six versioned server methods without inventing climate or material defaults', () => { expect(technicalModes).toHaveLength(6); expect(emptyLayer().conductivityWmK).toBeNull(); expect(emptyClimates()).toHaveLength(12); expect(emptyClimates().every(m => m.insideTemperatureC === null)).toBe(true); });
  it('builds only selected method fields and trims explicit sources', () => { expect(calculationInput('roof', { ridgeLengthM: 12, horizontalRunM: 4, riseM: 3, ignored: 99 }, ' Synthetic plan ', [], [])).toEqual(roofInput()); });
  it.each([null, undefined, NaN, Infinity, -1, 0, 100001])('rejects a missing or invalid roof length: %s', value => { expect(() => calculationInput('roof', { ridgeLengthM: value as number, horizontalRunM: 4, riseM: 3 }, source, [], [])).toThrow(); });
  it('accepts zero rise and zero surface resistance, not missing numbers', () => { expect(calculationInput('roof', { ridgeLengthM: 12, horizontalRunM: 4, riseM: 0 }, source, [], [])['riseM']).toBe(0); expect(calculationInput('thermal', { insideTemperatureC: 0, outsideTemperatureC: 0, insideSurfaceResistanceM2KW: 0, outsideSurfaceResistanceM2KW: 0 }, '', [layer()], [])['insideSurfaceResistanceM2KW']).toBe(0); });
  it('does not send a diffusion field to the strict thermal contract', () => { const input = calculationInput('thermal', { insideTemperatureC: 20, outsideTemperatureC: 0, insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04 }, '', [layer()], []); expect(input['layers']).toEqual([{ name: 'Synthetic', thicknessM: .12, conductivityWmK: .04, valueSource: 'Test reference' }]); });
  it('requires all 48 monthly climate values', () => { expect(() => calculationInput('monthly', { insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04 }, '', [layer()], emptyClimates())).toThrow(); });
  it('sorts twelve explicit climates and preserves a true zero humidity', () => { const months = emptyClimates().map(m => ({ ...m, insideTemperatureC: 20, outsideTemperatureC: 0, insideRelativeHumidityPercent: 0, outsideRelativeHumidityPercent: 50 })).reverse(); const input = calculationInput('monthly', { insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04 }, '', [layer()], months); expect((input['months'] as { month: number }[]).map(m => m.month)).toEqual(Array.from({ length: 12 }, (_, i) => i + 1)); });
  it('rejects duplicated calendar months', () => { const months = emptyClimates().map(m => ({ ...m, insideTemperatureC: 20, outsideTemperatureC: 0, insideRelativeHumidityPercent: 50, outsideRelativeHumidityPercent: 50 })); months[11].month = 11; expect(() => calculationInput('monthly', { insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04 }, '', [layer()], months)).toThrow('Kalendermonat'); });
  it('requires source and bounded solid layers', () => { expect(() => calculationInput('roof', { ridgeLengthM: 12, horizontalRunM: 4, riseM: 3 }, ' ', [], [])).toThrow(); expect(() => calculationInput('thermal', { insideTemperatureC: 20, outsideTemperatureC: 0, insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04 }, '', [{ ...layer(), thicknessM: 6 }], [])).toThrow(); });
  it('represents zero gradients without interpreting them as absent', () => { const input = calculationInput('tapered', { lengthM: 12, widthM: 10, minimumThicknessM: .08, lengthGradientPercent: 0, widthGradientPercent: 0 }, source, [], []); expect(input['lengthGradientPercent']).toBe(0); expect(input['widthGradientPercent']).toBe(0); });
});

describe('technical response boundaries', () => {
  it('accepts finite matching non-normative evidence', () => { expect(validateCalculation(result(), 'roof', roofInput())).toEqual(result()); });
  it('rejects a response for old input', () => { expect(() => validateCalculation(result(), 'roof', { ...roofInput(), riseM: 4 })).toThrow('aktuellen Eingaben'); });
  it.each(['isNormative', 'methodId', 'methodVersion', 'inputSha256'])('rejects invalid provenance field %s', key => { const value = result(); (value.provenance as Record<string, unknown>)[key] = key === 'isNormative' ? true : 'invalid'; expect(() => validateCalculation(value, 'roof', roofInput())).toThrow(); });
  it('rejects missing limitations and nonfinite metric values', () => { expect(() => validateCalculation({ ...result(), surfaceAreaM2: Infinity }, 'roof', roofInput())).toThrow(); expect(() => validateCalculation({ ...result(), provenance: { ...result().provenance, limitations: [] } }, 'roof', roofInput())).toThrow(); });
  it('accepts JSON key-order changes but not changed values', () => { expect(canonicalJson({ b: 2, a: 1 })).toBe(canonicalJson({ a: 1, b: 2 })); expect(canonicalJson([1, 2])).not.toBe(canonicalJson([2, 1])); expect(canonicalJson(-0)).toBe('0'); });
  it('accepts only complete lowercase SHA-256 identifiers', () => { expect(isDigest('a'.repeat(64))).toBe(true); expect(isDigest('a'.repeat(63))).toBe(false); expect(isDigest(null)).toBe(false); });
  it('requires the saved record to match the tenant, project and method', () => {
    const record = { id: '00000000-0000-0000-0000-000000000001', tenantId: 'alpha', projectId: 'p1', revision: 1, title: 'Reference', method: technicalMethods.roof.method, methodVersion: '1.0.0', input: roofInput(), result: result(), digest: 'b'.repeat(64), isNormative: false };
    expect(validateSavedCalculation(record, 'roof', roofInput(), 'alpha', 'p1')).toEqual(record);
    expect(() => validateSavedCalculation(record, 'roof', roofInput(), 'beta', 'p1')).toThrow();
    expect(() => validateSavedCalculation(record, 'roof', roofInput(), 'alpha', 'p2')).toThrow();
    expect(() => validateSavedCalculation({ ...record, isNormative: true }, 'roof', roofInput(), 'alpha', 'p1')).toThrow();
  });
});

const inhomogeneousValues = () => ({
  insideTemperatureC: 20, outsideTemperatureC: -20,
  insideSurfaceResistanceM2KW: .13, outsideSurfaceResistanceM2KW: .04
});
const rafterSections = () => [
  { name: 'Rafter', areaFraction: .15, layers: [
    { ...layer(), name: 'Inner', thicknessM: .02, conductivityWmK: .2 },
    { ...layer(), name: 'Timber', thicknessM: .2, conductivityWmK: .12 },
    { ...layer(), name: 'Outer', thicknessM: .02, conductivityWmK: .2 }
  ] },
  { name: 'Insulation', areaFraction: .85, layers: [
    { ...layer(), name: 'Inner', thicknessM: .02, conductivityWmK: .2 },
    { ...layer(), name: 'Insulation', thicknessM: .2, conductivityWmK: .04 },
    { ...layer(), name: 'Outer', thicknessM: .02, conductivityWmK: .2 }
  ] }
];
const rafterInput = () => calculationInput('inhomogeneous', inhomogeneousValues(), 'Synthetic drawing', [], [], rafterSections(), true);
const rafterResult = () => {
  const input = rafterInput();
  return { input, upperResistanceM2KW: 4.311524310118266, lowerResistanceM2KW: 4.216153846153846,
    estimatedResistanceM2KW: 4.263839078136056, uValueWm2K: .23453042708102192,
    heatFluxWm2: 9.381217083240877, upperToLowerRatio: 1.0226202605, halfSpreadPercent: 1.1183637822245285,
    metalBridgingModelled: false, constructionComplianceAssessed: false,
    sections: (input['sections'] as { name: string; areaFraction: number }[]).map(section => ({
      name: section.name, areaFraction: section.areaFraction, path: { totalResistanceM2KW: 4.3 }
    })),
    provenance: { methodId: 'inhomogeneous-resistance-bound-estimate', methodVersion: '1.0.0',
      inputSha256: 'a'.repeat(64), isNormative: false, limitations: ['Synthetic, not a certificate'] }
  };
};

describe('inhomogeneous roof workbench', () => {
  it('registers the actual server endpoint without material defaults', () => {
    expect(technicalModes).toHaveLength(6);
    expect(technicalMethods.inhomogeneous.endpoint).toBe('technical/thermal/inhomogeneous');
    expect(emptyInhomogeneousSection().layers[0].conductivityWmK).toBeNull();
    expect(emptyInhomogeneousSection().areaFraction).toBeNull();
  });
  it('serializes, trims, and orders area paths deterministically for saved-response comparison', () => {
    const input = rafterInput();
    expect(input['noMetalBridgingConfirmed']).toBe(true);
    expect(input['materialDataSource']).toBe('Synthetic drawing');
    const sections = input['sections'] as { name: string; areaFraction: number; layers: { name: string; valueSource: string }[] }[];
    expect(sections.map(section => section.name)).toEqual(['Insulation', 'Rafter']);
    expect(sections.reduce((sum, section) => sum + section.areaFraction, 0)).toBeCloseTo(1, 12);
    expect(sections[0].layers[0].valueSource).toBe('Test reference');
    expect(calculationInput('inhomogeneous', inhomogeneousValues(), 'Synthetic drawing', [], [], rafterSections().reverse(), true)).toEqual(input);
  });
  it('fails closed for confirmation, fractions, names, layer interfaces and sources', () => {
    const sections = rafterSections();
    const build = (items = sections, confirmed = true, materialSource = 'Synthetic drawing') =>
      calculationInput('inhomogeneous', inhomogeneousValues(), materialSource, [], [], items, confirmed);
    expect(() => build(sections, false)).toThrow('Metallische');
    expect(() => build(sections, true, ' ')).toThrow('Quelle');
    expect(() => build(sections.map(s => ({ ...s, areaFraction: .5 })))).not.toThrow();
    expect(() => build([{ ...sections[0], areaFraction: .16 }, sections[1]])).toThrow('Flächenanteile');
    expect(() => build([{ ...sections[0], name: 'Insulation' }, sections[1]])).toThrow('eindeutige');
    expect(() => build([{ ...sections[0], layers: sections[0].layers.slice(1) }, sections[1]])).toThrow('Schichtgrenzen');
    expect(() => build([{ ...sections[0], layers: sections[0].layers.map((l, i) => i === 0 ? { ...l, thicknessM: .021 } : l) }, sections[1]])).toThrow('Schichtgrenzen');
    expect(() => build([{ ...sections[0], layers: [{ ...sections[0].layers[0], valueSource: '' }, ...sections[0].layers.slice(1)] }, sections[1]])).toThrow('Quelle');
  });
  it('rejects incompatible server claims, stale inputs and malformed path results', () => {
    const result = rafterResult();
    expect(validateCalculation(result, 'inhomogeneous', rafterInput())).toEqual(result);
    expect(() => validateCalculation({ ...result, constructionComplianceAssessed: true }, 'inhomogeneous', rafterInput())).toThrow();
    expect(() => validateCalculation({ ...result, metalBridgingModelled: true }, 'inhomogeneous', rafterInput())).toThrow();
    expect(() => validateCalculation({ ...result, upperToLowerRatio: 1.6 }, 'inhomogeneous', rafterInput())).toThrow();
    expect(() => validateCalculation({ ...result, sections: [] }, 'inhomogeneous', rafterInput())).toThrow();
    expect(() => validateCalculation(result, 'inhomogeneous', { ...rafterInput(), outsideTemperatureC: -10 })).toThrow();
  });
  it('accepts a persisted non-normative calculation with matching project binding', () => {
    const input = rafterInput(), result = rafterResult();
    const record = { id: '00000000-0000-0000-0000-000000000001', revision: 1, tenantId: 'alpha',
      projectId: 'p1', title: 'Sparrendach', method: 'thermal.inhomogeneous', methodVersion: '1.0.0',
      input, result, digest: 'b'.repeat(64), isNormative: false };
    expect(validateSavedCalculation(record, 'inhomogeneous', input, 'alpha', 'p1')).toEqual(record);
    expect(() => validateSavedCalculation(record, 'inhomogeneous', input, 'beta', 'p1')).toThrow();
  });
});
