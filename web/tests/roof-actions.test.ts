import { describe, expect, it } from 'vitest';
import { ActionInput, RoofAction, actionDraftInput, roofActions, validActionInput, validActionResult } from '../src/app/core/roof-actions.models';
import { actionInputs, actionResponse } from './roof-actions.fixture';

for (const mode of ['drainage', 'wind', 'snow'] as RoofAction[]) describe(mode + ' supplied roof cases', () => {
  it('accepts the independently specified arithmetic fixture', () => {
    expect(validActionResult(mode, actionResponse(mode), validActionInput(mode, actionInputs[mode])).rows[0]['name']).toBe('West');
  });
  it('requires every numeric input including zero-capable coefficients and capacities', () => {
    const def = roofActions[mode];
    for (const field of def.fields) {
      const input: ActionInput = structuredClone(actionInputs[mode]);
      delete (input[def.rowsKey] as Record<string, unknown>[])[0][field.key];
      expect(() => validActionInput(mode, input)).toThrow();
    }
  });
  it('rejects duplicate case-insensitive names and nonfinite areas', () => {
    const def = roofActions[mode], input: ActionInput = structuredClone(actionInputs[mode]);
    const rows = input[def.rowsKey] as Record<string, unknown>[];
    rows.push({ ...rows[0], name: ' WEST ' });
    expect(() => validActionInput(mode, input)).toThrow('eindeutig');
    rows.pop(); rows[0][def.fields[0].key] = Infinity;
    expect(() => validActionInput(mode, input)).toThrow();
  });
  it('rejects a modified source, changed row order, unsupported approval and missing limitations', () => {
    const def = roofActions[mode], input = validActionInput(mode, actionInputs[mode]);
    const raw: Record<string, unknown> = structuredClone(actionResponse(mode));
    (raw['input'] as Record<string, unknown>)[def.sourceKey] = 'Another source';
    expect(() => validActionResult(mode, raw, input)).toThrow('Eingabe');
    raw['input'] = input;
    (raw[def.rowsKey] as Record<string, unknown>[])[0]['name'] = 'Other';
    expect(() => validActionResult(mode, raw, input)).toThrow('Bereichszuordnung');
    raw[def.approvalKey] = true;
    expect(() => validActionResult(mode, raw, input)).toThrow();
    const incomplete = actionResponse(mode); incomplete.provenance.limitations = [];
    expect(() => validActionResult(mode, incomplete, input)).toThrow();
  });
  it('independently checks every result quantity instead of trusting totals or labels', () => {
    const def = roofActions[mode], input = validActionInput(mode, actionInputs[mode]);
    for (const col of def.columns) {
      const raw: Record<string, unknown> = structuredClone(actionResponse(mode));
      const row = (raw[def.rowsKey] as Record<string, unknown>[])[0];
      row[col.key] = (row[col.key] as number) + 1;
      expect(() => validActionResult(mode, raw, input)).toThrow('Widersprüchliche');
    }
  });
});
it('rejects a missing emergency scenario and inverted rainfall pair', () => {
  expect(() => validActionInput('drainage', { ...actionInputs.drainage, emergencyScenario: undefined })).toThrow('auswählen');
  expect(() => validActionInput('drainage', { ...actionInputs.drainage, stormRainfallLsHa: 200 })).toThrow('kleiner');
});
it('never treats an empty numeric draft as an explicit zero', () => {
  expect(() => actionDraftInput('wind', { source: 'Source', globals: {}, scenario: '',
    rows: [{ name: 'West', values: { loadedAreaM2: null }, sources: { valueSource: 'Source' } }] })).toThrow();
});
it('rejects a snow total inconsistent with the independent zone force', () => {
  const result = actionResponse('snow');
  expect(() => validActionResult('snow', { ...result, totalVerticalForceKN: 100 }, validActionInput('snow', actionInputs.snow))).toThrow('Gesamtvertikalkraft');
});
