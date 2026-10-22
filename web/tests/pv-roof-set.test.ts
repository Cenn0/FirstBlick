import { describe, expect, it } from 'vitest';
import { PvInput } from '../src/app/core/pv-layout.models';
import { validPvRoofSetInput, validPvRoofSetResult } from '../src/app/core/pv-roof-set.models';

const layout = (): PvInput => ({
  roofPlaneWidthM: 2, roofPlaneHeightM: 2, moduleWidthM: 1, moduleHeightM: 1,
  moduleRatedPowerWp: 420, edgeClearanceM: 0, moduleGapXM: 0, moduleGapYM: 0,
  exclusionClearanceM: 0, orientation: 'Portrait', exclusions: [],
  roofMeasurementSource: 'Synthetic plane', moduleDataSource: 'Synthetic module'
});
const input = () => ({ roofSetSource: 'Survey', planes: [
  { name: 'South', layout: layout() }, { name: 'North', layout: layout() }
] });
const planeResult = () => ({
  input: layout(), selectedOrientation: 'Portrait', roofPlaneAreaM2: 4,
  gridCandidateCount: 4, obstructionRejectedCount: 0, installedModules: 4,
  installedModuleAreaM2: 4, nominalDcPowerWp: 1680,
  placements: Array.from({ length: 4 }, (_, i) => ({
    row: Math.floor(i / 2) + 1, column: i % 2 + 1,
    x: i % 2, y: Math.floor(i / 2), widthM: 1, heightM: 1
  })),
  structureOrElectricalDesignApproved: false,
  provenance: { methodId: 'pv-roof-plane-grid', methodVersion: '1.0.0',
    inputSha256: 'a'.repeat(64), isNormative: false, limitations: ['Geometry', 'No mounting', 'No electricity'] }
});
const result = () => ({
  input: input(), planes: [{ name: 'South', layout: planeResult() }, { name: 'North', layout: planeResult() }],
  installedModules: 8, nominalDcPowerWp: 3360, roofPlaneAreaM2: 8,
  installedModuleAreaM2: 8, gridCandidateCount: 8, obstructionRejectedCount: 0,
  structureOrElectricalDesignApproved: false,
  provenance: { methodId: 'pv-roof-set-grid', methodVersion: '1.0.0',
    inputSha256: 'b'.repeat(64), isNormative: false, limitations: ['Separate planes', 'No overlap check', 'No approval'] }
});
describe('bounded and versioned multi-plane PV layout', () => {
  it('accepts multiple source-linked planes and the sum of independent calculations', () => {
    expect(validPvRoofSetResult(result(), validPvRoofSetInput(input())).installedModules).toBe(8);
  });
  it('trims names and preserves the explicit plane order', () => {
    const accepted = validPvRoofSetInput({ ...input(),
      planes: [{ name: '  South  ', layout: layout() }, { name: 'North', layout: layout() }] });
    expect(accepted.planes.map(p => p.name)).toEqual(['South', 'North']);
  });
  it.each([
    { roofSetSource: '' }, { planes: [] },
    { planes: [{ name: 'South', layout: layout() }, { name: ' south ', layout: layout() }] },
    { planes: [{ name: 'South', layout: { ...layout(), moduleWidthM: Infinity } }] },
    { planes: Array.from({ length: 9 }, (_, i) => ({ name: String(i), layout: layout() })) }
  ])('rejects invalid roof groups %o', change => {
    expect(() => validPvRoofSetInput({ ...input(), ...change })).toThrow();
  });
  it.each([
    { installedModules: 9 }, { nominalDcPowerWp: 5000 }, { gridCandidateCount: 7 },
    { structureOrElectricalDesignApproved: true },
    { planes: [{ name: 'North', layout: planeResult() }, { name: 'South', layout: planeResult() }] },
    { provenance: { ...result().provenance, isNormative: true } }
  ])('rejects contradictory results %o', change => {
    expect(() => validPvRoofSetResult({ ...result(), ...change }, validPvRoofSetInput(input()))).toThrow();
  });
});
