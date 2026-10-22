import { describe, expect, it } from 'vitest';
import fixtures from './fixtures/advanced-technical.json';
import { AdvancedMode, AdvancedResult, normalizeAdvancedInput, validateAdvancedResult, validateAdvancedSaved } from '../src/app/core/advanced-technical.models';

type Fixture = { mode: string; input: Record<string, unknown>; result: unknown };
const cases = Object.entries(fixtures) as [string, Fixture][];
const clone = (key = 'balance'): any => structuredClone((fixtures as Record<string, Fixture>)[key]);
const saved = (): any => {
  const f = clone();
  return { id: '20000000-0000-4000-8000-000000000001', tenantId: 'tenant', projectId: 'project', revision: 1,
    method: 'moisture.iso13788.balance', methodVersion: '1.0.0',
    input: f.result.input, result: f.result, digest: 'b'.repeat(64), isNormative: false };
};

describe('current core outputs at the advanced technical client boundary', () => {
  it.each(cases)('accepts the exact core fixture %s', (_key, f) => {
    expect(validateAdvancedResult(f.result, f.mode as AdvancedMode, f.input)).toEqual(f.result);
  });
  it('allows documented climate, field and SKU ordering plus price string serialization', () => {
    for (const key of ['balance', 'boards', 'assembly']) {
      const f = clone(key);
      if (key === 'balance') f.input.months.reverse();
      else if (key === 'assembly') f.input.fields.reverse();
      else { f.input.catalog.boards.reverse(); f.input.catalog.boards[0].pricePerBoardEur = '20.00'; }
      expect(() => validateAdvancedResult(f.result, f.mode, f.input)).not.toThrow();
    }
  });
  it.each(cases)('rejects changed source inputs for %s', (_key, f) => {
    const input = structuredClone(f.input); input['unexpected'] = 'changed';
    expect(() => validateAdvancedResult(f.result, f.mode as AdvancedMode, input)).toThrow('aktuellen Eingaben');
  });
  it.each(['methodId', 'methodVersion', 'inputSha256', 'isNormative', 'limitations'])('rejects corrupt provenance %s', field => {
    const f = clone(); f.result.provenance[field] = field === 'isNormative' ? true : '';
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each([
    ['totalCondensedKgM2', 999], ['totalEvaporatedKgM2', -1], ['residualCondensateKgM2', NaN],
    ['classification', 'Approved'], ['startingMonth', 0], ['months', []], ['interfaceMaxima', []]
  ])('rejects malformed annual result %s', (key, value) => {
    const f = clone(); f.result[key] = value;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['sequence', 'month', 'days', 'condensedKgM2', 'endStoredMassKgM2', 'massConservationErrorKgM2'])('rejects altered monthly %s', key => {
    const f = clone(); f.result.months[0][key] += 1;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['startMassKgM2', 'endMassKgM2', 'condensedKgM2', 'evaporatedKgM2', 'maximumMassKgM2'])('rejects altered interface %s', key => {
    const f = clone(); const wet = f.result.months.find((m: any) => m.wetInterfaces.length).wetInterfaces[0]; wet[key] += 1;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it('rejects forged approval and duplicate interfaces', () => {
    const f = clone(); f.result.standardProcedure.din4108Part3Assessment = true;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
    const d = clone(); const wet = d.result.months.find((m: any) => m.wetInterfaces.length).wetInterfaces; wet.push({ ...wet[0] });
    expect(() => validateAdvancedResult(d.result, d.mode, d.input)).toThrow();
  });
  it.each(['dryingTimeMonths', 'initialExcessMassKgM2', 'residualMassKgM2', 'initiallyWettedNode'])('rejects changed drying %s', key => {
    const f = clone('drying'); f.result[key] += 1;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['roofAreaM2', 'installedBoards', 'installedVolumeM3', 'cutWasteVolumeM3', 'totalEstimatedPackCostEur'])('rejects changed board total %s', key => {
    const f = clone('boards'); f.result[key] += 1;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['packsToOrder', 'boardsOrdered', 'usedBoards', 'unitsPerPack', 'additionalBoardsFromPacks', 'estimatedPackCostEur'])('rejects changed board order %s', key => {
    const f = clone('boards'); f.result.orders[0][key] += 1;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['sku', 'lengthM', 'thinEdgeM', 'rotationDegrees', 'requiresCut'])('rejects changed board placement %s', key => {
    const f = clone('boards'); f.result.placements[0][key] = 'invalid';
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it('rejects an unknown price becoming a zero', () => {
    const f = clone('unknown'); f.result.orders[0].estimatedPackCostEur = 0;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it.each(['fieldId', 'globalX', 'panel'])('rejects changed assembly placement %s', key => {
    const f = clone('assembly'); f.result.placements[0][key] = null;
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
  it('rejects catalogue substitution in consolidated orders', () => {
    const f = clone('assembly'); f.result.orders[0].catalogRevision = 'substituted';
    expect(() => validateAdvancedResult(f.result, f.mode, f.input)).toThrow();
  });
});

describe('immutable saved preview binding', () => {
  const preview = () => clone().result as AdvancedResult;
  it('accepts a matching saved result', () => { expect(validateAdvancedSaved(saved(), 'balance', preview(), 'tenant', 'project')).toEqual(saved()); });
  it.each(['tenantId', 'projectId', 'method', 'methodVersion', 'digest', 'id', 'revision', 'isNormative'])('rejects changed saved %s', key => {
    const s = saved(); s[key] = key === 'isNormative' ? true : 'invalid';
    expect(() => validateAdvancedSaved(s, 'balance', preview(), 'tenant', 'project')).toThrow();
  });
  it('rejects different outer input even with an unchanged nested preview hash', () => {
    const s = saved(); s.input = { ...s.input, trialMonth: 2 };
    expect(() => validateAdvancedSaved(s, 'balance', preview(), 'tenant', 'project')).toThrow();
  });
  it('rejects changed result content with a copied preview hash', () => {
    const s = saved(); s.result.provenance.limitations = ['Substituted evidence'];
    expect(() => validateAdvancedSaved(s, 'balance', preview(), 'tenant', 'project')).toThrow('Vorschau');
  });
  it('never mutates source collections while normalizing', () => {
    const f = clone('boards'); normalizeAdvancedInput(f.input, 'boards'); expect(f).toEqual(clone('boards'));
  });
});
