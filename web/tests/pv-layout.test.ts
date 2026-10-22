import { describe, expect, it } from 'vitest';
import { PvInput, validPvInput, validPvResult } from '../src/app/core/pv-layout.models';

const input = (): PvInput => ({
  roofPlaneWidthM: 4, roofPlaneHeightM: 2, moduleWidthM: 1, moduleHeightM: 1,
  moduleRatedPowerWp: 420, edgeClearanceM: 0, moduleGapXM: 0, moduleGapYM: 0,
  exclusionClearanceM: 0, orientation: 'Portrait', exclusions: [],
  roofMeasurementSource: 'Synthetic measured roof', moduleDataSource: 'Synthetic module'
});
const result = () => ({
  input: input(), selectedOrientation: 'Portrait', roofPlaneAreaM2: 8,
  gridCandidateCount: 8, obstructionRejectedCount: 0, installedModules: 8,
  installedModuleAreaM2: 8, nominalDcPowerWp: 3360,
  placements: Array.from({ length: 8 }, (_, i) => ({ row: Math.floor(i / 4) + 1,
    column: i % 4 + 1, x: i % 4, y: Math.floor(i / 4), widthM: 1, heightM: 1 })),
  structureOrElectricalDesignApproved: false,
  provenance: { methodId: 'pv-roof-plane-grid', methodVersion: '1.0.0', inputSha256: 'a'.repeat(64),
    isNormative: false, limitations: ['Not structural', 'Not fire safety', 'Not electrical'] }
});

describe('PV layout input and non-normative response contract', () => {
  it('accepts a bounded complete geometry and normalizes sources and obstacles', () => {
    const checked = validPvInput({ ...input(), roofMeasurementSource: '  Synthetic measured roof  ',
      exclusions: [{ name: '  Rooflight ', x: 1, y: 0, widthM: 1, heightM: 1 }] });
    expect(checked.roofMeasurementSource).toBe('Synthetic measured roof');
    expect(checked.exclusions[0].name).toBe('Rooflight');
  });
  it.each([
    { moduleRatedPowerWp: 0 }, { moduleHeightM: Infinity }, { roofPlaneWidthM: null },
    { orientation: 'Unsupported' }, { moduleDataSource: '' }, { moduleGapXM: -.1 },
    { exclusions: [{ name: 'Outside', x: 3.5, y: 0, widthM: 1, heightM: 1 }] },
    { exclusions: [{ name: 'Missing', x: 0, y: 0, widthM: null, heightM: 1 }] }
  ])('rejects unsupported input %o', changed => {
    expect(() => validPvInput({ ...input(), ...changed })).toThrow();
  });
  it('accepts a complete result without implying installation approval', () => {
    expect(validPvResult(result(), input()).installedModules).toBe(8);
  });
  it.each([
    { selectedOrientation: 'Landscape' }, { installedModules: 9 },
    { gridCandidateCount: 7 }, { structureOrElectricalDesignApproved: true },
    { nominalDcPowerWp: 4000 }, { installedModuleAreaM2: 9 },
    { input: { ...input(), moduleWidthM: 1.1 } },
    { provenance: { ...result().provenance, isNormative: true } }
  ])('rejects a contradictory result %o', changed => {
    expect(() => validPvResult({ ...result(), ...changed }, input())).toThrow();
  });
  it('rejects a module position that escapes the roof', () => {
    const invalid = result();
    invalid.placements[0].x = 4;
    expect(() => validPvResult(invalid, input())).toThrow();
  });
});
