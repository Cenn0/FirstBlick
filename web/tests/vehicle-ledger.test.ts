import { describe, expect, it } from 'vitest';
import { VehicleCost, VehicleCostItem, VehicleLedger, VehicleTrip, VehicleTripItem, VehicleView, validCostChange, validCostItem, validTripChange, validTripItem, validVehicleLedger, validVehicleList } from '../src/app/core/vehicle-ledger.models';
import { decimalUnits } from '../src/app/core/ledger-contracts';

const vehicleId = '72000000-0000-4000-8000-000000000001', tenant = '72000000-0000-4000-8000-000000000002', actor = '72000000-0000-4000-8000-000000000003';
const tripId = '72000000-0000-4000-8000-000000000004', costId = '72000000-0000-4000-8000-000000000005', reverseId = '72000000-0000-4000-8000-000000000006';
const now = '2026-10-22T01:00:00Z';
const vehicle = (): VehicleView => ({ id: vehicleId, tenantId: tenant, revision: 2, name: 'Transporter Nord', inventoryNumber: 'FB-03', kind: 'Vehicle', registration: 'TEST-03', archived: false, available: true });
const common = () => ({ id: tripId, tenantId: tenant, revision: 1, createdAt: now, updatedAt: now, archived: false as const, vehicleId,
  vehicleSnapshot: { id: vehicleId, name: 'Transporter Nord', inventoryNumber: 'FB-03', kind: 'Vehicle' as const, registration: 'TEST-03' },
  projectId: null, projectTitle: null, recordedBy: actor, recordedAt: now, digest: 'a'.repeat(64) });
const trip = (): VehicleTrip => ({ ...common(), driverEmployeeId: actor, driverName: 'Testperson', start: '2026-10-21T06:00:00Z', end: '2026-10-21T07:00:00Z', odometerStartKm: '1000', odometerEndKm: '1020.125', purpose: 'Material abholen', origin: 'Betriebshof', destination: 'Lieferant', notes: '' });
const tripItem = (): VehicleTripItem => ({ entry: trip(), distanceKm: '20.125', reversal: null });
const cost = (): VehicleCost => ({ ...common(), id: costId, spentOn: '2026-10-21', category: 'Fuel', amount: '40.25', currency: 'EUR', reference: 'Tankbeleg-42', notes: '' });
const costItem = (): VehicleCostItem => ({ entry: cost(), reversal: null });
const book = (): VehicleLedger => ({ vehicle: vehicle(), businessDate: '2026-10-22', timeZone: 'Europe/Berlin', month: '2026-10', summary: { tripCount: 1, reversedTripCount: 0, distanceKm: '20.125', costCount: 1, reversedCostCount: 0, costAmount: '40.25', costPerKm: '2' }, trips: { items: [tripItem()], total: 1, page: 1, pageSize: 20 }, costs: { items: [costItem()], total: 1, page: 1, pageSize: 20 } });
const reverseTrip = (): VehicleTripItem => ({ ...tripItem(), reversal: { id: reverseId, tenantId: tenant, revision: 1, createdAt: now, updatedAt: now, archived: false, vehicleId, sourceKind: 'vehicle-trips', sourceId: tripId, sourceDigest: 'a'.repeat(64), reason: 'Falsche Strecke', recordedBy: actor, recordedAt: now, digest: 'b'.repeat(64) } });

describe('vehicle ledger exact client contracts', () => {
  it('accepts the complete monthly snapshot and validates decimals without binary arithmetic', () => {
    expect(validVehicleLedger(book(), vehicleId, tenant, '2026-10', 1, 1).summary.costPerKm).toBe('2');
    expect(decimalUnits('9999999.999', 3)).toBe(9_999_999_999n);
    for (const value of [20.125, '2e1', '-1', '1.0001', 'NaN']) expect(decimalUnits(value, 3)).toBeNull();
  });
  it('binds vehicle lists and ledger snapshots to the current tenant and selected month', () => {
    const list = { items: [vehicle()], total: 1, page: 1, pageSize: 25, businessDate: '2026-10-22', timeZone: 'Europe/Berlin' };
    expect(validVehicleList(list, tenant, 1).items).toHaveLength(1);
    expect(() => validVehicleList(list, actor, 1)).toThrow('Betrieb');
    expect(() => validVehicleList({ ...list, items: [{ ...vehicle(), kind: 'Tool' }] }, tenant, 1)).toThrow('Fahrzeugdaten');
    expect(() => validVehicleLedger(book(), vehicleId, tenant, '2026-09', 1, 1)).toThrow('Monat');
    expect(() => validVehicleLedger(book(), vehicleId, tenant, '2026-10', 2, 1)).toThrow('Seiten');
  });
  it('rejects altered distance, odometer precision, chronology and invalid driver identities', () => {
    expect(() => validTripItem({ ...tripItem(), distanceKm: '20.124' }, vehicleId, tenant)).toThrow('Kilometerstände');
    for (const change of [{ odometerEndKm: '999' }, { odometerEndKm: '1020.1251' }, { end: '2026-10-21T05:00:00Z' }, { end: '2026-10-22T08:00:00Z' }, { start: '2026-10-21T06:00:00.01Z' }, { driverEmployeeId: 'unknown' }])
      expect(() => validTripItem({ ...tripItem(), entry: { ...trip(), ...change } }, vehicleId, tenant)).toThrow();
  });
  it('rejects malformed money, cost classification, date and required original reference', () => {
    for (const change of [{ amount: '0' }, { amount: '-5.00' }, { amount: '40.251' }, { amount: 40.25 }, { currency: 'USD' }, { category: 'Unknown' }, { spentOn: '2026-02-30' }, { reference: '' }])
      expect(() => validCostItem({ ...costItem(), entry: { ...cost(), ...change } }, vehicleId, tenant)).toThrow('ungültige');
  });
  it('does not admit reversed originals into active totals and binds the reversal source', () => {
    const reversed = { ...book(), trips: { ...book().trips, items: [reverseTrip()] }, summary: { ...book().summary, tripCount: 0, reversedTripCount: 1, distanceKm: '0', costPerKm: null } };
    expect(validVehicleLedger(reversed, vehicleId, tenant, '2026-10', 1, 1).summary.distanceKm).toBe('0');
    expect(() => validVehicleLedger({ ...reversed, summary: book().summary }, vehicleId, tenant, '2026-10', 1, 1)).toThrow('Kilometer-Monatssumme');
    expect(() => validTripItem({ ...reverseTrip(), reversal: { ...reverseTrip().reversal, sourceId: costId } }, vehicleId, tenant)).toThrow('Stornierung');
  });
  it('independently checks full-page totals, counts and rounded per-kilometre cost', () => {
    for (const summary of [{ ...book().summary, costPerKm: '2.0001' }, { ...book().summary, costAmount: '40.24' }, { ...book().summary, tripCount: 0 }])
      expect(() => validVehicleLedger({ ...book(), summary }, vehicleId, tenant, '2026-10', 1, 1)).toThrow();
    const rounding = { ...book(), costs: { ...book().costs, items: [{ entry: { ...cost(), amount: '1' }, reversal: null }] }, summary: { ...book().summary, costAmount: '1', costPerKm: '0.0497' } };
    expect(validVehicleLedger(rounding, vehicleId, tenant, '2026-10', 1, 1).summary.costPerKm).toBe('0.0497');
  });
  it('allocates a UTC month-boundary journey by the business-zone start month', () => {
    const entry = { ...trip(), start: '2026-09-30T22:30:00Z', end: '2026-09-30T23:00:00Z' };
    const value = { ...book(), trips: { ...book().trips, items: [{ ...tripItem(), entry }] } };
    expect(validVehicleLedger(value, vehicleId, tenant, '2026-10', 1, 1).trips.items).toHaveLength(1);
    expect(() => validVehicleLedger({ ...value, timeZone: 'UTC' }, vehicleId, tenant, '2026-10', 1, 1)).toThrow('außerhalb');
  });
  it('confirms exactly the submitted trip and next vehicle revision', () => {
    const before = { ...book(), trips: { ...book().trips, items: [], total: 0 } }, source = trip();
    const request = { driverEmployeeId: source.driverEmployeeId, projectId: source.projectId, start: source.start, end: source.end, odometerStartKm: source.odometerStartKm, odometerEndKm: source.odometerEndKm, purpose: source.purpose, origin: source.origin, destination: source.destination, notes: source.notes };
    const change = { vehicle: { ...vehicle(), revision: 3 }, trip: tripItem() };
    expect(validTripChange(change, before, request).trip.entry.id).toBe(tripId);
    expect(() => validTripChange({ ...change, trip: { ...tripItem(), entry: { ...source, purpose: 'Andere Fahrt' } } }, before, request)).toThrow('Eingabe');
    expect(() => validTripChange({ ...change, vehicle: { ...vehicle(), revision: 4 } }, before, request)).toThrow('Fahrzeugstand');
  });
  it('requires an unchanged original for reversal and exact cost request values', () => {
    const changed = { vehicle: { ...vehicle(), revision: 3 }, trip: reverseTrip() }, request = { sourceDigest: trip().digest, reason: 'Falsche Strecke' };
    expect(validTripChange(changed, book(), request, tripItem()).trip.reversal).not.toBeNull();
    expect(() => validTripChange({ ...changed, trip: { ...reverseTrip(), entry: { ...trip(), notes: 'Überschrieben' } } }, book(), request, tripItem())).toThrow('Original');
    const input = { projectId: null, spentOn: cost().spentOn, category: cost().category, amount: cost().amount, currency: 'EUR' as const, reference: cost().reference, notes: '' };
    const before = { ...book(), costs: { ...book().costs, items: [], total: 0 } }, change = { vehicle: { ...vehicle(), revision: 3 }, cost: costItem() };
    expect(validCostChange(change, before, input).cost.entry.reference).toBe('Tankbeleg-42');
    expect(() => validCostChange(change, before, { ...input, amount: '41.00' })).toThrow('Eingabe');
  });
});
