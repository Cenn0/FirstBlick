import { JsonPipe } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { downloadBlob, sha256Blob } from '../core/integrations.models';

type Workflow = 'balance' | 'drying' | 'boards' | 'assembly';
type HeatFlow = 'Upward' | 'Horizontal' | 'Downward';
type Exterior = 'OutsideAir' | 'WellVentilatedCavity';
type ClimateTreatment = 'WallAir' | 'RoofMinusTwoKelvin' | 'SuppliedEquivalentRoofTemperature';
type LayerKind = 'HomogeneousLayer' | 'Membrane' | 'UnventilatedAirLayer';
type FlowAxis = 'AlongLength' | 'AlongWidth';
interface Layer { name: string; resistanceM2KW: number | null; sdM: number | null; kind: LayerKind; designValueSource: string }
interface Month { month: number; insideTemperatureC: number | null; outsideAirTemperatureC: number | null;
  insideRelativeHumidityPercent: number | null; outsideRelativeHumidityPercent: number | null; equivalentOutsideTemperatureC: number | null }
interface Board { sku: string; thinEdgeM: number | null; thickEdgeM: number | null; unitsPerPack: number | null; price: string }
interface RoofFieldDraft { fieldId: string; originX: number | null; originY: number | null;
  roofLengthM: number | null; roofWidthM: number | null; lowEdgeThicknessM: number | null;
  slopePercent: number | null; flowAxis: FlowAxis; lowEdgeAtCoordinateZero: boolean }
type Result = Record<string, unknown> & { input: Record<string, unknown>; provenance: { methodId: string; methodVersion: string; inputSha256: string; isNormative: false; limitations: string[] } };
type Saved = { id: string; revision: number; tenantId: string; projectId: string; method: string; methodVersion: string; input: unknown; result: Result; digest: string; isNormative: false };
const hex = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const object = (value: unknown): Record<string, unknown> => {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('Die Serverantwort enthält kein vollständiges Datenobjekt.');
  return value as Record<string, unknown>;
};
function num(value: number | null, description: string, low: number, high: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < low || value > high)
    throw new Error(`${description}: Wert zwischen ${low} und ${high} erforderlich.`);
  return value;
}
function integer(value: number | null, description: string, low: number, high: number): number {
  const n = num(value, description, low, high);
  if (!Number.isInteger(n)) throw new Error(`${description} muss eine ganze Zahl sein.`);
  return n;
}
function label(value: string, description: string, limit = 500): string {
  if (!value?.trim() || value.length > limit || /[\u0000-\u001f\u007f-\u009f]/u.test(value))
    throw new Error(`${description}: eine aussagekräftige Quellenangabe mit maximal ${limit} Zeichen eintragen.`);
  return value.trim();
}
const emptyLayer = (): Layer => ({ name: '', resistanceM2KW: null, sdM: null, kind: 'HomogeneousLayer', designValueSource: '' });
const emptyBoard = (): Board => ({ sku: '', thinEdgeM: null, thickEdgeM: null, unitsPerPack: null, price: '' });
const emptyRoofField = (): RoofFieldDraft => ({ fieldId: '', originX: null, originY: null,
  roofLengthM: null, roofWidthM: null, lowEdgeThicknessM: null, slopePercent: null,
  flowAxis: 'AlongLength', lowEdgeAtCoordinateZero: true });
const emptyMonths = (): Month[] => Array.from({ length: 12 }, (_, index) => ({
  month: index + 1, insideTemperatureC: null, outsideAirTemperatureC: null,
  insideRelativeHumidityPercent: null, outsideRelativeHumidityPercent: null, equivalentOutsideTemperatureC: null
}));
const methods: Record<Workflow, { name: string; method: string; endpoint: string; provenance: string }> = {
  balance: { name: 'Tauwasser- und Jahresbilanz (ISO 13788)', method: 'moisture.iso13788.balance',
    endpoint: 'technical/moisture/iso13788/balance', provenance: 'iso13788-monthly-diffusion-balance' },
  drying: { name: 'Austrocknung (ISO 13788, Abschnitt 7)', method: 'moisture.iso13788.drying',
    endpoint: 'technical/moisture/iso13788/drying', provenance: 'iso13788-excess-water-drying' },
  boards: { name: 'Gefälleplatten aus Herstellerkatalog', method: 'roof.tapered-board-layout',
    endpoint: 'technical/roof/tapered-board-layout', provenance: 'manufacturer-catalog-tapered-board-layout' },
  assembly: { name: 'Mehrfeld-Verlegeplanung', method: 'roof.tapered-assembly',
    endpoint: 'technical/roof/tapered-assembly', provenance: 'manufacturer-catalog-multifield-tapered-board-layout' }
};
const labels = ['Jan', 'Feb', 'Mär', 'Apr', 'Mai', 'Jun', 'Jul', 'Aug', 'Sep', 'Okt', 'Nov', 'Dez'];

@Component({
  selector: 'app-advanced-technical',
  imports: [FormsModule, JsonPipe, RouterLink],
  templateUrl: './advanced-technical.component.html',
  styleUrl: './technical.component.css'
})
export class AdvancedTechnicalComponent {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.can('technical-results.write'));
  readonly mode = signal<Workflow>('balance');
  readonly modes: Workflow[] = ['balance', 'drying', 'boards', 'assembly'];
  readonly methods = methods; readonly monthsLabels = labels;
  readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly result = signal<Result | null>(null); readonly saved = signal<Saved | null>(null);
  readonly projects = signal<Entity[]>([]); readonly totalProjects = signal(0);
  readonly monthOrder = emptyMonths;
  readonly applicabilityFlags: { key: keyof AdvancedTechnicalComponent['applicability']; title: string }[] = [
    { key: 'parallelHomogeneousLayers', title: 'Ausschließlich parallele homogene Schichten' },
    { key: 'noAirflowThroughAssembly', title: 'Keine Luftströmung durch den Aufbau' },
    { key: 'noRainAbsorption', title: 'Kein Feuchteeintrag durch Regen' },
    { key: 'negligibleCapillaryAndHygroscopicEffects', title: 'Kapillare und hygroskopische Effekte vernachlässigbar' },
    { key: 'constantDesignProperties', title: 'Konstante und fachlich überprüfte Materialkennwerte' },
    { key: 'climateSelectionReviewed', title: 'Klima- und Nutzungsrandbedingungen fachlich überprüft' }
  ];
  layers: Layer[] = [emptyLayer()]; months: Month[] = emptyMonths();
  climateCalendarYear: number | null = null; trialMonth: number | null = null;
  heatFlow: HeatFlow = 'Upward'; exteriorBoundary: Exterior = 'OutsideAir'; climateTreatment: ClimateTreatment = 'WallAir';
  subdivideThermalLayers = false; constructionReview = ''; climateSource = '';
  applicability = { parallelHomogeneousLayers: false, noAirflowThroughAssembly: false, noRainAbsorption: false,
    negligibleCapillaryAndHygroscopicEffects: false, constantDesignProperties: false, climateSelectionReviewed: false };
  wettedLayerIndex: number | null = null; startingMonth: number | null = null; initialAssessmentReview = '';
  roofLengthM: number | null = null; roofWidthM: number | null = null; lowEdgeThicknessM: number | null = null;
  slopePercent: number | null = null; flowAxis: FlowAxis = 'AlongLength'; lowEdgeAtCoordinateZero = true;
  measuredRoofSource = ''; drainageDesignReview = ''; planRevision = ''; roofFields: RoofFieldDraft[] = [emptyRoofField()];
  manufacturer = ''; productRange = ''; catalogRevision = ''; catalogSource = '';
  boardRunM: number | null = null; boardCrossM: number | null = null; boards: Board[] = [emptyBoard()];
  projectSearch = ''; projectPage = 1; projectId = ''; reportTitle = '';
  private revision = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.revision++; });
    effect(() => { this.auth.accessSignature(); untracked(() => this.clear()); });
  }
  private current(revision: number, access: string): boolean {
    return revision === this.revision && access === this.auth.accessSignature();
  }
  private clear(): void {
    this.edited();
    this.projects.set([]); this.totalProjects.set(0); this.projectId = ''; this.projectSearch = '';
    this.projectPage = 1; this.reportTitle = '';
  }
  changeMode(mode: Workflow): void {
    if (this.mode() === mode) return;
    this.mode.set(mode);
    this.clear();
  }
  tabKey(event: KeyboardEvent, mode: Workflow): void {
    const i = this.modes.indexOf(mode);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? this.modes.length - 1
      : event.key === 'ArrowRight' ? (i + 1) % this.modes.length
        : event.key === 'ArrowLeft' ? (i + this.modes.length - 1) % this.modes.length : -1;
    if (next < 0) return;
    event.preventDefault();
    const list = (event.currentTarget as HTMLElement).parentElement;
    this.changeMode(this.modes[next]);
    list?.querySelector<HTMLButtonElement>(`#advanced-tab-${this.modes[next]}`)?.focus();
  }
  edited(): void {
    this.revision++; this.busy.set(false); this.error.set(''); this.notice.set(''); this.result.set(null); this.saved.set(null);
  }
  attachmentEdited(): void { this.revision++; this.saved.set(null); this.notice.set(''); }
  addLayer(): void { if (this.layers.length < 60) { this.layers.push(emptyLayer()); this.edited(); } }
  removeLayer(index: number): void { if (this.layers.length > 1) { this.layers.splice(index, 1); this.edited(); } }
  addBoard(): void { if (this.boards.length < 500) { this.boards.push(emptyBoard()); this.edited(); } }
  removeBoard(index: number): void { if (this.boards.length > 1) { this.boards.splice(index, 1); this.edited(); } }
  addRoofField(): void { if (this.roofFields.length < 32) { this.roofFields.push(emptyRoofField()); this.edited(); } }
  removeRoofField(index: number): void { if (this.roofFields.length > 1) { this.roofFields.splice(index, 1); this.edited(); } }
  boardOrder(row: Record<string, unknown>): Record<string, unknown> { return this.mode() === 'assembly' ? object(row['quantity']) : row; }
  boardPanel(row: Record<string, unknown>): Record<string, unknown> { return this.mode() === 'assembly' ? object(row['panel']) : row; }
  needsEquivalent(): boolean { return this.climateTreatment === 'SuppliedEquivalentRoofTemperature' && this.exteriorBoundary === 'OutsideAir'; }
  monthLabel(value: unknown): string { return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= 12 ? labels[value - 1] : 'Ungültiger Monat'; }
  number(value: unknown, digits = 5): string {
    return typeof value === 'number' && Number.isFinite(value)
      ? new Intl.NumberFormat('de-DE', { maximumFractionDigits: digits }).format(value) : '–';
  }
  responseEntries(value: Result, key: string): Record<string, unknown>[] {
    const raw = value[key];
    return Array.isArray(raw) ? raw.map(object) : [];
  }
  private buildInput(): Record<string, unknown> {
    if (this.mode() === 'boards' || this.mode() === 'assembly') {
      if (this.boards.length < 1 || this.boards.length > 500) throw new Error('1 bis 500 Herstellerartikel angeben.');
      const seen = new Set<string>();
      const catalog = {
        manufacturer: label(this.manufacturer, 'Hersteller', 200),
        productRange: label(this.productRange, 'Produktreihe', 200),
        catalogRevision: label(this.catalogRevision, 'Katalogrevision', 200),
        sourceReference: label(this.catalogSource, 'Katalogquelle', 1000),
        boardRunM: num(this.boardRunM, 'Plattenlauflänge', .2, 3),
        boardCrossM: num(this.boardCrossM, 'Plattenquerbreite', .2, 3),
        boards: this.boards.map((board, i) => {
          const sku = label(board.sku, 'Artikelnummer', 100);
          if (seen.has(sku)) throw new Error('Artikelnummer ' + sku + ' mehrfach eingegeben.');
          seen.add(sku);
          const price = board.price.trim().replace(',', '.');
          if (price && (!/^(?:0|[1-9]\d{0,6})(?:\.\d{1,2})?$/.test(price) || Number(price) <= 0))
            throw new Error('Herstellerartikel ' + (i + 1) + ': positiven Preis in Euro mit höchstens zwei Nachkommastellen angeben oder leer lassen.');
          return { sku, thinEdgeM: num(board.thinEdgeM, 'Anfangsdicke', .01, 2),
            thickEdgeM: num(board.thickEdgeM, 'Enddicke', .01, 2),
            unitsPerPack: integer(board.unitsPerPack, 'Stück je Pack', 1, 200),
            pricePerBoardEur: price || null };
        })
      };
      const roofLengthM = num(this.roofLengthM, 'Dachlänge', .01, 1000);
      const roofWidthM = num(this.roofWidthM, 'Dachbreite', .01, 1000);
      const measuredRoofSource = label(this.measuredRoofSource, 'Aufmaßquelle', 500);
      const drainageDesignReview = label(this.drainageDesignReview, 'Entwässerungsvorgabe', 1000);
      if (this.mode() === 'assembly') {
        if (this.roofFields.length < 1 || this.roofFields.length > 32) throw new Error('1 bis 32 getrennte Dachfelder erfassen.');
        const ids = new Set<string>();
        const fields = this.roofFields.map((field, i) => {
          const fieldId = label(field.fieldId, 'Feld ' + (i + 1) + ': Kennung', 100);
          if (ids.has(fieldId)) throw new Error('Dachfeld ' + fieldId + ' wurde mehrfach angegeben.');
          ids.add(fieldId);
          return {
            fieldId, originX: num(field.originX, 'Feldursprung x', 0, roofLengthM),
            originY: num(field.originY, 'Feldursprung y', 0, roofWidthM),
            layout: {
              roofLengthM: num(field.roofLengthM, 'Feldlänge', .01, 1000),
              roofWidthM: num(field.roofWidthM, 'Feldbreite', .01, 1000),
              lowEdgeThicknessM: num(field.lowEdgeThicknessM, 'Feld-Tiefkantendicke', .01, 2),
              slopePercent: num(field.slopePercent, 'Feldgefälle', .01, 10),
              flowAxis: field.flowAxis, lowEdgeAtCoordinateZero: field.lowEdgeAtCoordinateZero,
              measuredRoofSource, drainageDesignReview, catalog
            }
          };
        });
        return { roofLengthM, roofWidthM, planRevision: label(this.planRevision, 'Dachplanrevision', 200),
          surveySource: measuredRoofSource, drainageReview: drainageDesignReview, fields };
      }
      return { roofLengthM, roofWidthM, lowEdgeThicknessM: num(this.lowEdgeThicknessM, 'Mindestdicke', .01, 2),
        slopePercent: num(this.slopePercent, 'Gefälle', .01, 10), flowAxis: this.flowAxis,
        lowEdgeAtCoordinateZero: this.lowEdgeAtCoordinateZero, measuredRoofSource, drainageDesignReview, catalog };
    }
    if (!Object.values(this.applicability).every(x => x))
      throw new Error('Das ISO-Verfahren ist erst zulässig, wenn alle sechs Anwendungsannahmen fachlich bestätigt sind.');
    if (this.layers.length < 1 || this.layers.length > 60 || this.months.length !== 12)
      throw new Error('Die ISO-Berechnung benötigt 1–60 Schichten und genau 12 Klimamonate.');
    const assembly = {
      climateCalendarYear: integer(this.climateCalendarYear, 'Klimakalenderjahr', 1900, 3000),
      trialMonth: integer(this.trialMonth, 'Versuchsmonat', 1, 12),
      heatFlow: this.heatFlow, exteriorBoundary: this.exteriorBoundary,
      climateTreatment: this.climateTreatment, subdivideThermalLayers: this.subdivideThermalLayers,
      layers: this.layers.map((layer, i) => ({
        name: label(layer.name, `Schicht ${i + 1}`, 200),
        resistanceM2KW: num(layer.resistanceM2KW, 'Schicht-Wärmewiderstand', 0, 50),
        sdM: num(layer.sdM, 'sd-Wert', .000001, 1000000),
        kind: layer.kind, designValueSource: label(layer.designValueSource, 'Materialwertquelle', 1000)
      })),
      months: this.months.map(month => ({
        month: month.month,
        insideTemperatureC: num(month.insideTemperatureC, 'Innenklima-Temperatur', -50, 80),
        outsideAirTemperatureC: num(month.outsideAirTemperatureC, 'Außentemperatur', -50, 80),
        insideRelativeHumidityPercent: num(month.insideRelativeHumidityPercent, 'Innen-RH', 0, 100),
        outsideRelativeHumidityPercent: num(month.outsideRelativeHumidityPercent, 'Außen-RH', 0, 100),
        equivalentOutsideTemperatureC: this.needsEquivalent()
          ? num(month.equivalentOutsideTemperatureC, 'Dachoberflächen-Ersatztemperatur', -50, 80) : null
      })),
      applicability: { ...this.applicability,
        constructionReview: label(this.constructionReview, 'Konstruktionsprüfung', 2000),
        climateSource: label(this.climateSource, 'Klimadatenquelle', 1000) }
    };
    if (this.exteriorBoundary === 'WellVentilatedCavity' && this.climateTreatment !== 'WallAir')
      throw new Error('Vor einer stark belüfteten Luftschicht sind ausschließlich Außenluft-Randbedingungen zulässig.');
    return this.mode() === 'balance' ? assembly : {
      assembly, wettedLayerIndex: integer(this.wettedLayerIndex, 'Durchfeuchtete Schicht (0-basiert)', 0, this.layers.length - 1),
      startingMonth: integer(this.startingMonth, 'Austrocknungs-Startmonat', 1, 12),
      initialAssessmentReview: label(this.initialAssessmentReview, 'Austrocknungsprüfung', 2000)
    };
  }
  private validatedResponse(raw: unknown, method: Workflow): Result {
    const item = object(raw);
    const provenance = object(item['provenance']);
    if (provenance['methodId'] !== methods[method].provenance ||
        provenance['methodVersion'] !== '1.0.0' || provenance['isNormative'] !== false ||
        !hex(provenance['inputSha256']) || !Array.isArray(provenance['limitations']) ||
        !(provenance['limitations'] as unknown[]).every(x => typeof x === 'string') ||
        (provenance['limitations'] as unknown[]).length < 1) throw new Error('Der Server hat keine passende, überprüfbar begrenzte Rechenantwort geliefert.');
    object(item['input']);
    if (method === 'boards' || method === 'assembly') {
      if (!Array.isArray(item['placements']) || !Array.isArray(item['orders']) ||
          item['catalogIndependentlyVerified'] !== false || item['drainageOrStructureApproved'] !== false ||
          typeof item['installedVolumeM3'] !== 'number' || !Number.isFinite(item['installedVolumeM3']))
        throw new Error('Der Plattennachweis enthält keine gültige Mengen- und Zulassungstrennung.');
      if (method === 'assembly' && (!Array.isArray(item['fields']) ||
          !(item['orders'] as unknown[]).every(x => typeof x === 'object' && x !== null && !Array.isArray(x) &&
            typeof (x as Record<string, unknown>)['quantity'] === 'object')))
        throw new Error('Die Feld-/Packmengen-Abstimmung des Gesamtplans ist nicht vorhanden.');
    } else {
      const standard = object(item['standardProcedure']);
      if (standard['constructionComplianceAssessed'] !== false ||
          standard['din4108Part3Assessment'] !== false ||
          standard['implementsSpecifiedProcedure'] !== true ||
          !Array.isArray(item['months'])) throw new Error('Der ISO-Verfahrensnachweis ist unvollständig.');
      if (method === 'balance' && item['months'].length !== 12) throw new Error('Die Jahresbilanz enthält nicht zwölf Monate.');
    }
    return item as Result;
  }
  private failed(cause: unknown, revision: number, access: string): void {
    if (!this.current(revision, access)) return;
    if (cause instanceof ApiError && [401, 403, 423].includes(cause.status)) {
      this.result.set(null); this.saved.set(null); this.projects.set([]); this.projectId = '';
    }
    this.error.set(errorMessage(cause));
  }
  async calculate(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const revision = ++this.revision, access = this.auth.accessSignature(), mode = this.mode();
    this.busy.set(true); this.error.set(''); this.notice.set(''); this.result.set(null); this.saved.set(null);
    try {
      const input = this.buildInput();
      const answer = await this.api.command<unknown>(methods[mode].endpoint, input);
      if (this.current(revision, access)) this.result.set(this.validatedResponse(answer, mode));
    } catch (error) { this.failed(error, revision, access); }
    finally { if (this.current(revision, access)) this.busy.set(false); }
  }
  async searchProjects(page = 1): Promise<void> {
    if (!this.allowed() || this.busy() || !this.auth.can('projects.read')) return;
    const revision = ++this.revision, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set(''); this.projects.set([]); this.projectId = '';
    try {
      const data = await this.api.list('projects', this.projectSearch, page);
      if (!this.current(revision, access)) return;
      this.projects.set(data.items.filter(item => !item.archived)); this.totalProjects.set(data.total); this.projectPage = page;
    } catch (error) { this.failed(error, revision, access); }
    finally { if (this.current(revision, access)) this.busy.set(false); }
  }
  async save(): Promise<void> {
    const result = this.result();
    if (!result || !this.allowed() || this.busy() || this.saved()) return;
    const revision = ++this.revision, access = this.auth.accessSignature(), mode = this.mode();
    const tenant = this.auth.activeTenantId(), project = this.projectId;
    this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      if (!this.projects().some(x => x.id === project)) throw new Error('Ein Projekt aus der aktuellen Suche auswählen.');
      const title = label(this.reportTitle, 'Berichtstitel', 240);
      const raw = object(await this.api.command<unknown>('technical/runs',
        { projectId: project, roofRevisionId: null, title, method: methods[mode].method, input: result.input }));
      if (!this.current(revision, access)) return;
      if (raw['tenantId'] !== tenant || raw['projectId'] !== project || raw['method'] !== methods[mode].method ||
          raw['methodVersion'] !== '1.0.0' || raw['isNormative'] !== false ||
          typeof raw['id'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(raw['id']) ||
          !Number.isInteger(raw['revision']) || Number(raw['revision']) < 1 || !hex(raw['digest']))
        throw new Error('Der gespeicherte Bericht gehört nicht zum ausgewählten Betrieb, Verfahren oder Projekt.');
      const restored = this.validatedResponse(raw['result'], mode);
      if (restored.provenance.inputSha256 !== result.provenance.inputSha256)
        throw new Error('Der gespeicherte Rechenbericht unterscheidet sich von der freigegebenen Vorschau.');
      this.saved.set(raw as unknown as Saved);
      this.notice.set('Berechnung mit Quelle, Fachgrenzen und Digest im Projekt gespeichert.');
    } catch (error) { this.failed(error, revision, access); }
    finally { if (this.current(revision, access)) this.busy.set(false); }
  }
  exportJson(): void {
    const result = this.result(); if (!result || !this.allowed() || this.busy()) return;
    downloadBlob(new Blob([JSON.stringify(this.saved() ?? result, null, 2)], { type: 'application/json' }),
      `FirstBlick-${methods[this.mode()].method}-${result.provenance.inputSha256.slice(0, 12)}.json`);
  }
  async pdf(): Promise<void> {
    const record = this.saved(); if (!record || !this.allowed() || this.busy()) return;
    const revision = ++this.revision, access = this.auth.accessSignature();
    this.busy.set(true); this.error.set('');
    try {
      const response = object(await this.api.command<unknown>(`technical-results/${record.id}/pdf`, {}, record.revision));
      if (!this.current(revision, access)) return;
      if (response['sourceId'] !== record.id || response['sourceDigest'] !== record.digest ||
          response['sourceKind'] !== 'technical-results' || response['projectId'] !== record.projectId ||
          response['state'] !== 'Ready' || response['contentType'] !== 'application/pdf' || !hex(response['sha256']) ||
          typeof response['id'] !== 'string' || !/^[a-f0-9-]{36}$/i.test(response['id']))
        throw new Error('PDF-Beleg und gespeicherte Berechnung sind nicht eindeutig verbunden.');
      const blob = await this.api.download(`documents/${response['id']}/download`);
      if (!this.current(revision, access)) return;
      if (blob.size !== response['size'] || blob.size <= 0 || blob.size > 25 * 1024 * 1024 ||
          await blob.slice(0, 5).text() !== '%PDF-' || await sha256Blob(blob) !== response['sha256'])
        throw new Error('Der heruntergeladene PDF-Bericht besteht die Byte-Prüfung nicht.');
      if (this.current(revision, access)) downloadBlob(blob, `FirstBlick-Fachberechnung-${record.id}.pdf`);
    } catch (error) { this.failed(error, revision, access); }
    finally { if (this.current(revision, access)) this.busy.set(false); }
  }
}
