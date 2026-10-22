import { RoofAction } from '../src/app/core/roof-actions.models';

export const actionInputs = {
  drainage: { rainfallSource: 'Synthetic rainfall site pair', designRainfallLsHa: 300, stormRainfallLsHa: 600,
    emergencyScenario: 'PrimaryUnavailable', catchments: [{ name: 'West', effectiveAreaM2: 200,
      designRunoffCoefficient: .8, primaryCapacityLs: 4, emergencyCapacityLs: 6,
      areaSource: 'Synthetic area', capacitySource: 'Synthetic capacity at specified head' }] },
  wind: { caseSource: 'Synthetic wind case', zones: [{ name: 'West', loadedAreaM2: 12,
    externalPeakPressureKNm2: .8, externalCoefficient: -1.5, internalPeakPressureKNm2: .6,
    internalCoefficient: .2, valueSource: 'Synthetic coefficients' }] },
  snow: { caseSource: 'Synthetic snow case', groundSnowLoadKNm2: 1.8, exposureCoefficient: .8, thermalCoefficient: 1,
    zones: [{ name: 'West', horizontalAreaM2: 50, shapeCoefficient: .8, valueSource: 'Synthetic projection and shape' }] }
};
export function actionResponse(mode: RoofAction) {
  const provenance = (methodId: string) => ({ methodId, methodVersion: '1.0.0', inputSha256: 'a'.repeat(64), isNormative: false,
    limitations: ['Supplied case only', 'Independent zones', 'No rule selection', 'No design approval'] });
  if (mode === 'drainage') return { input: actionInputs.drainage,
    catchments: [{ name: 'West', primaryDemandLs: 4.8, stormInflowLs: 12, creditedPrimaryFlowLs: 0,
      emergencyDemandLs: 12, primaryDeficitLs: .8, emergencyDeficitLs: 6 }], hydraulicDesignApproved: false,
    provenance: provenance('supplied-catchment-flow-balance') };
  if (mode === 'wind') return { input: actionInputs.wind,
    zones: [{ name: 'West', externalPressureKNm2: -1.2, internalPressureKNm2: .12,
      netInwardPressureKNm2: -1.32, normalForceKN: -15.84, outwardSuctionKNm2: 1.32 }], structuralDesignApproved: false,
    provenance: provenance('supplied-local-wind-pressure') };
  return { input: actionInputs.snow, zones: [{ name: 'West', verticalLoadKNm2: 1.152, verticalForceKN: 57.6 }],
    totalVerticalForceKN: 57.6, structuralDesignApproved: false, provenance: provenance('supplied-roof-snow-case') };
}
