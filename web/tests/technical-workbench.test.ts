import { describe, expect, it } from 'vitest';
import { calculationInput, canonicalJson, emptyClimates, emptyLayer, isDigest, technicalMethods, technicalModes, validateCalculation, validateSavedCalculation } from '../src/app/core/technical-workbench.models';

const source = 'Synthetic plan';
const roofInput = () => calculationInput('roof', { ridgeLengthM: 12, horizontalRunM: 4, riseM: 3 }, source, [], []);
const result = () => ({ input: roofInput(), surfaceAreaM2: 60, projectedAreaM2: 48, slopingLengthM: 5, pitchDegrees: 36.8699, perimeterM: 34, provenance: { methodId: 'rectangular-roof-plane', methodVersion: '1.0.0', inputSha256: 'a'.repeat(64), isNormative: false, limitations: ['Synthetic non-normative reference'] } });
const layer = () => ({ name: ' Synthetic ', thicknessM: .12, conductivityWmK: .04, vapourResistanceFactor: 10, valueSource: ' Test reference ' });

describe('technical workbench inputs', () => {
  it('defines five existing versioned server methods without inventing climate or material defaults', () => { expect(technicalModes).toHaveLength(5); expect(emptyLayer().conductivityWmK).toBeNull(); expect(emptyClimates()).toHaveLength(12); expect(emptyClimates().every(m => m.insideTemperatureC === null)).toBe(true); });
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
