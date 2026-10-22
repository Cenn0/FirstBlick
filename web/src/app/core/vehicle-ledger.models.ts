import { businessLocalTime } from './business-time';
import { decimalUnits, recordDate, recordDigest, recordEntity, recordId, recordInstant, recordObject, recordPage, recordRevision, recordText, recordZone, requireUnique, sameInstant, sameRecord } from './ledger-contracts';

export interface VehicleView { id: string; tenantId: string; revision: number; name: string; inventoryNumber: string; kind: 'Vehicle'; registration: string | null; archived: boolean; available: boolean }
export interface VehicleList { items: VehicleView[]; total: number; page: number; pageSize: number; businessDate: string; timeZone: string }
export interface VehicleSnapshot { id: string; name: string; inventoryNumber: string; kind: 'Vehicle'; registration: string }
interface VehicleEntity { id: string; tenantId: string; revision: number; createdAt: string; updatedAt: string; archived: false }
interface VehicleOriginal extends VehicleEntity {
  vehicleId: string; vehicleSnapshot: VehicleSnapshot; projectId: string | null; projectTitle: string | null; recordedBy: string; recordedAt: string; digest: string;
}
export interface VehicleTrip extends VehicleOriginal { driverEmployeeId: string; driverName: string; start: string; end: string; odometerStartKm: string; odometerEndKm: string; purpose: string; origin: string; destination: string; notes: string }
export type VehicleCostCategory = 'Fuel' | 'Maintenance' | 'Repair' | 'Insurance' | 'Tax' | 'Toll' | 'Parking' | 'Other';
export interface VehicleCost extends VehicleOriginal { spentOn: string; category: VehicleCostCategory; amount: string; currency: 'EUR'; reference: string; notes: string }
export interface VehicleReversal extends VehicleEntity { vehicleId: string; sourceKind: 'vehicle-trips' | 'vehicle-costs'; sourceId: string; sourceDigest: string; reason: string; recordedBy: string; recordedAt: string; digest: string }
export interface VehicleTripItem { entry: VehicleTrip; distanceKm: string; reversal: VehicleReversal | null }
export interface VehicleCostItem { entry: VehicleCost; reversal: VehicleReversal | null }
interface VehiclePage<T> { items: T[]; total: number; page: number; pageSize: number }
export interface VehicleSummary { tripCount: number; reversedTripCount: number; distanceKm: string; costCount: number; reversedCostCount: number; costAmount: string; costPerKm: string | null }
export interface VehicleLedger { vehicle: VehicleView; businessDate: string; timeZone: string; month: string; summary: VehicleSummary; trips: VehiclePage<VehicleTripItem>; costs: VehiclePage<VehicleCostItem> }
export interface VehicleTripRequest { driverEmployeeId: string; projectId: string | null; start: string; end: string; odometerStartKm: string; odometerEndKm: string; purpose: string; origin: string; destination: string; notes: string }
export interface VehicleCostRequest { projectId: string | null; spentOn: string; category: VehicleCostCategory; amount: string; currency: 'EUR'; reference: string; notes: string }
export interface VehicleReverseRequest { sourceDigest: string; reason: string }
export interface VehicleTripChange { vehicle: VehicleView; trip: VehicleTripItem }
export interface VehicleCostChange { vehicle: VehicleView; cost: VehicleCostItem }
export const vehicleCostCategories: Record<VehicleCostCategory, string> = { Fuel: 'Kraftstoff', Maintenance: 'Wartung', Repair: 'Reparatur', Insurance: 'Versicherung', Tax: 'Kfz-Steuer', Toll: 'Maut', Parking: 'Parken', Other: 'Sonstiges' };
export function validVehicleMonth(value: unknown): value is string { return typeof value === 'string' && /^\d{4}-(?:0[1-9]|1[0-2])$/.test(value) && Number(value.slice(0, 4)) >= 1900 && Number(value.slice(0, 4)) <= 9998; }

export function validVehicle(value: unknown, tenant: string, id?: string): VehicleView {
  if (!recordObject(value) || !recordId(value['id']) || id !== undefined && value['id'] !== id || value['tenantId'] !== tenant
    || !recordRevision(value['revision']) || !recordText(value['name'], 240, true) || !recordText(value['inventoryNumber'], 100, true)
    || value['kind'] !== 'Vehicle' || value['registration'] !== null && !recordText(value['registration'], 80)
    || typeof value['archived'] !== 'boolean' || typeof value['available'] !== 'boolean')
    throw new Error('Die Fahrzeugdaten gehören nicht eindeutig zum gewählten Fahrzeug und Betrieb.');
  return value as unknown as VehicleView;
}
export function validVehicleList(value: unknown, tenant: string, page: number): VehicleList {
  if (!recordPage(value, page, 25) || !recordDate(value['businessDate']) || !recordZone(value['timeZone'])) throw new Error('Die Fahrzeugauswahl ist unvollständig.');
  const items = value.items.map(item => validVehicle(item, tenant));
  if (items.some(item => item.archived)) throw new Error('Die Fahrzeugauswahl enthält archivierte Fahrzeuge.');
  requireUnique(items.map(item => item.id), 'Die Fahrzeugauswahl enthält doppelte Fahrzeuge.');
  return value as unknown as VehicleList;
}
function validOriginal(value: unknown, vehicleId: string, tenant: string): asserts value is Record<string, unknown> {
  if (!recordEntity(value, tenant) || value['vehicleId'] !== vehicleId || value['revision'] !== 1 || !recordId(value['recordedBy'])
    || !recordInstant(value['recordedAt']) || !sameInstant(value['createdAt'] as string, value['recordedAt']) || !recordDigest(value['digest']))
    throw new Error('Der Fahrzeugbeleg enthält ungültige Original- oder Betriebsdaten.');
  const snapshot = value['vehicleSnapshot'];
  if (!recordObject(snapshot) || snapshot['id'] !== vehicleId || snapshot['kind'] !== 'Vehicle' || !recordText(snapshot['name'], 240, true)
    || !recordText(snapshot['inventoryNumber'], 100, true) || !recordText(snapshot['registration'], 80)) throw new Error('Die damalige Fahrzeugidentität ist unvollständig.');
  if (value['projectId'] === null ? value['projectTitle'] !== null : !recordId(value['projectId']) || !recordText(value['projectTitle'], 240, true))
    throw new Error('Die Projektzuordnung des Fahrzeugbelegs ist unvollständig.');
}
function validReversal(value: unknown, entry: VehicleTrip | VehicleCost, kind: 'vehicle-trips' | 'vehicle-costs'): void {
  if (value === null) return;
  if (!recordEntity(value, entry.tenantId) || value['revision'] !== 1 || value['vehicleId'] !== entry.vehicleId || value['sourceId'] !== entry.id
    || value['sourceKind'] !== kind || value['sourceDigest'] !== entry.digest || value['id'] === entry.id || !recordText(value['reason'], 2000, true)
    || !recordId(value['recordedBy']) || !recordInstant(value['recordedAt']) || Date.parse(value['recordedAt']) < Date.parse(entry.recordedAt)
    || !sameInstant(value['createdAt'] as string, value['recordedAt']) || !recordDigest(value['digest']))
    throw new Error('Die Stornierung gehört nicht zum ursprünglichen Fahrzeugbeleg.');
}
export function validTripItem(value: unknown, vehicleId: string, tenant: string): VehicleTripItem {
  if (!recordObject(value)) throw new Error('Der Fahrteintrag fehlt.');
  const entry = value['entry']; validOriginal(entry, vehicleId, tenant);
  const startKm = decimalUnits(entry['odometerStartKm'], 3), endKm = decimalUnits(entry['odometerEndKm'], 3), distance = decimalUnits(value['distanceKm'], 3);
  if (!recordId(entry['driverEmployeeId']) || !recordText(entry['driverName'], 240, true) || !recordInstant(entry['start'], true) || !recordInstant(entry['end'], true)
    || entry['start'].slice(0, 4) < '1900' || Date.parse(entry['end']) <= Date.parse(entry['start']) || Date.parse(entry['end']) - Date.parse(entry['start']) > 86_400_000
    || Date.parse(entry['end']) > Date.parse(entry['recordedAt'] as string) || startKm === null || endKm === null || endKm > 9_999_999_999n || endKm <= startKm || distance !== endKm - startKm
    || !recordText(entry['purpose'], 1000, true) || !recordText(entry['origin'], 240, true) || !recordText(entry['destination'], 240, true) || !recordText(entry['notes'], 4000))
    throw new Error('Die Fahrt enthält widersprüchliche Zeiten, Kilometerstände oder Angaben.');
  validReversal(value['reversal'], entry as unknown as VehicleTrip, 'vehicle-trips');
  return value as unknown as VehicleTripItem;
}
export function validCostItem(value: unknown, vehicleId: string, tenant: string): VehicleCostItem {
  if (!recordObject(value)) throw new Error('Der Kostenbeleg fehlt.');
  const entry = value['entry']; validOriginal(entry, vehicleId, tenant);
  const amount = decimalUnits(entry['amount'], 2);
  if (!recordDate(entry['spentOn']) || entry['spentOn'].slice(0, 4) < '1900' || entry['spentOn'].slice(0, 4) > '9998'
    || !Object.hasOwn(vehicleCostCategories, String(entry['category'])) || entry['currency'] !== 'EUR' || amount === null || amount <= 0n || amount > 100_000_000_000_000n
    || !recordText(entry['reference'], 240, true) || !recordText(entry['notes'], 4000)) throw new Error('Der Fahrzeugkostenbeleg enthält ungültige Beträge oder Angaben.');
  validReversal(value['reversal'], entry as unknown as VehicleCost, 'vehicle-costs');
  return value as unknown as VehicleCostItem;
}
export function validVehicleLedger(value: unknown, vehicleId: string, tenant: string, month: string, tripPage: number, costPage: number): VehicleLedger {
  if (!recordObject(value) || !recordDate(value['businessDate']) || !recordZone(value['timeZone']) || !validVehicleMonth(value['month']) || value['month'] !== month
    || !recordPage(value['trips'], tripPage, 20) || !recordPage(value['costs'], costPage, 20) || !recordObject(value['summary']))
    throw new Error('Die Fahrzeugakte bestätigt nicht den gewählten Monat oder die angeforderten Seiten.');
  validVehicle(value['vehicle'], tenant, vehicleId);
  const trips = value['trips'].items.map(item => validTripItem(item, vehicleId, tenant)), costs = value['costs'].items.map(item => validCostItem(item, vehicleId, tenant));
  requireUnique(trips.map(item => item.entry.id), 'Die Fahrzeugakte enthält doppelte Fahrten.'); requireUnique(costs.map(item => item.entry.id), 'Die Fahrzeugakte enthält doppelte Kostenbelege.');
  requireUnique([...trips, ...costs].flatMap(item => item.reversal ? [item.reversal.id] : []), 'Die Fahrzeugakte enthält doppelte Stornierungen.');
  if (trips.some(item => businessLocalTime(item.entry.start, value['timeZone'] as string).slice(0, 7) !== month)
    || costs.some(item => item.entry.spentOn.slice(0, 7) !== month || item.entry.spentOn > String(value['businessDate']))) throw new Error('Die Fahrzeugakte enthält Belege außerhalb des gewählten Monats.');
  if (trips.some((item, index) => index > 0 && Date.parse(trips[index - 1].entry.start) < Date.parse(item.entry.start))
    || costs.some((item, index) => index > 0 && costs[index - 1].entry.spentOn < item.entry.spentOn)) throw new Error('Die Fahrzeugakte enthält falsch sortierte Belege.');
  const summary = value['summary'];
  if (['tripCount', 'reversedTripCount', 'costCount', 'reversedCostCount'].some(key => !Number.isSafeInteger(summary[key]) || Number(summary[key]) < 0)
    || Number(summary['tripCount']) + Number(summary['reversedTripCount']) !== value['trips'].total
    || Number(summary['costCount']) + Number(summary['reversedCostCount']) !== value['costs'].total) throw new Error('Die Monatsübersicht passt nicht zu den aufbewahrten Belegen.');
  const distance = decimalUnits(summary['distanceKm'], 3), amount = decimalUnits(summary['costAmount'], 2), rate = decimalUnits(summary['costPerKm'], 4);
  if (distance === null || amount === null || (distance === 0n ? summary['costPerKm'] !== null : rate === null)
    || (Number(summary['tripCount']) === 0) !== (distance === 0n) || (Number(summary['costCount']) === 0) !== (amount === 0n)) throw new Error('Die Monatssummen oder der Kilometerkostensatz sind ungültig.');
  // Where the entire month fits on one page, independently bind the totals to its active originals.
  if (value['trips'].total <= 20 && tripPage === 1 && (trips.filter(item => !item.reversal).length !== summary['tripCount']
    || trips.filter(item => !item.reversal).reduce((sum, item) => sum + decimalUnits(item.distanceKm, 3)!, 0n) !== distance)) throw new Error('Die Kilometer-Monatssumme widerspricht den aktiven Fahrten.');
  if (value['costs'].total <= 20 && costPage === 1 && (costs.filter(item => !item.reversal).length !== summary['costCount']
    || costs.filter(item => !item.reversal).reduce((sum, item) => sum + decimalUnits(item.entry.amount, 2)!, 0n) !== amount)) throw new Error('Die Kosten-Monatssumme widerspricht den aktiven Belegen.');
  // EUR cents / km thousandths -> EUR/km at four decimals, rounded away from zero.
  if (distance > 0n && rate !== (amount * 100_000n * 2n + distance) / (distance * 2n)) throw new Error('Der Kilometerkostensatz passt nicht zu den Monatssummen.');
  return value as unknown as VehicleLedger;
}
function changedVehicle(value: unknown, before: VehicleLedger): VehicleView {
  const vehicle = validVehicle(value, before.vehicle.tenantId, before.vehicle.id);
  if (!sameRecord({ ...vehicle, revision: before.vehicle.revision }, before.vehicle) || vehicle.revision !== before.vehicle.revision + 1)
    throw new Error('Das Ergebnis bestätigt nicht den angeforderten Fahrzeugstand. Laden Sie die Akte neu.');
  return vehicle;
}
export function validTripChange(value: unknown, before: VehicleLedger, request: VehicleTripRequest | VehicleReverseRequest, original?: VehicleTripItem): VehicleTripChange {
  if (!recordObject(value)) throw new Error('Das Ergebnis der Fahrtbuchung fehlt.');
  changedVehicle(value['vehicle'], before); const trip = validTripItem(value['trip'], before.vehicle.id, before.vehicle.tenantId);
  if ('sourceDigest' in request) {
    if (!original || original.reversal || !trip.reversal || !sameRecord(trip.entry, original.entry) || trip.distanceKm !== original.distanceKm
      || trip.reversal.sourceDigest !== request.sourceDigest || trip.reversal.reason !== request.reason) throw new Error('Die Fahrtstornierung verändert das Original oder bestätigt einen anderen Grund.');
  } else if (trip.reversal || before.trips.items.some(item => item.entry.id === trip.entry.id) || trip.entry.driverEmployeeId !== request.driverEmployeeId
    || trip.entry.projectId !== request.projectId || !sameInstant(trip.entry.start, request.start) || !sameInstant(trip.entry.end, request.end)
    || decimalUnits(trip.entry.odometerStartKm, 3) !== decimalUnits(request.odometerStartKm, 3) || decimalUnits(trip.entry.odometerEndKm, 3) !== decimalUnits(request.odometerEndKm, 3)
    || trip.entry.purpose !== request.purpose || trip.entry.origin !== request.origin || trip.entry.destination !== request.destination || trip.entry.notes !== request.notes)
    throw new Error('Die gespeicherte Fahrt bestätigt andere Angaben als die gesendete Eingabe.');
  return value as unknown as VehicleTripChange;
}
export function validCostChange(value: unknown, before: VehicleLedger, request: VehicleCostRequest | VehicleReverseRequest, original?: VehicleCostItem): VehicleCostChange {
  if (!recordObject(value)) throw new Error('Das Ergebnis der Kostenbuchung fehlt.');
  changedVehicle(value['vehicle'], before); const cost = validCostItem(value['cost'], before.vehicle.id, before.vehicle.tenantId);
  if ('sourceDigest' in request) {
    if (!original || original.reversal || !cost.reversal || !sameRecord(cost.entry, original.entry) || cost.reversal.sourceDigest !== request.sourceDigest || cost.reversal.reason !== request.reason)
      throw new Error('Die Kostenstornierung verändert das Original oder bestätigt einen anderen Grund.');
  } else if (cost.reversal || before.costs.items.some(item => item.entry.id === cost.entry.id) || cost.entry.projectId !== request.projectId || cost.entry.spentOn !== request.spentOn
    || cost.entry.category !== request.category || cost.entry.currency !== request.currency || decimalUnits(cost.entry.amount, 2) !== decimalUnits(request.amount, 2)
    || cost.entry.reference !== request.reference || cost.entry.notes !== request.notes) throw new Error('Der Kostenbeleg bestätigt andere Angaben als die gesendete Eingabe.');
  return value as unknown as VehicleCostChange;
}
