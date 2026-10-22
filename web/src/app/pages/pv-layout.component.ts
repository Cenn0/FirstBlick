import { Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { downloadBlob } from '../core/integrations.models';
import { PvDraft, PvResult, validPvInput, validPvResult } from '../core/pv-layout.models';
import { errorMessage } from '../core/transport';

const emptyDraft = (): PvDraft => ({
  roofPlaneWidthM: null, roofPlaneHeightM: null, moduleWidthM: null,
  moduleHeightM: null, moduleRatedPowerWp: null, edgeClearanceM: 0,
  moduleGapXM: 0, moduleGapYM: 0, exclusionClearanceM: 0, orientation: 'MostModules',
  exclusions: [], roofMeasurementSource: '', moduleDataSource: ''
});

@Component({
  imports: [FormsModule, RouterLink],
  template: String.raw`
    <header class="page-heading"><div><p class="eyebrow">DACH & BERECHNUNG · PV</p>
      <h1>Geometrische PV-Modulbelegung</h1>
      <p>Rechteckige Dachfläche, gemessene Moduldaten und explizite Hindernisse.</p></div>
      <a class="button secondary" routerLink="/technical">Zurück zu den Fachwerkzeugen</a></header>
    @if (!allowed()) {
      <section class="panel empty-state"><h2>Zugriff eingeschränkt</h2>
        <p>Technisches Modul und Berechtigungen für technische Ergebnisse erforderlich.</p></section>
    } @else {
      <div class="alert warning"><strong>Keine Montage- oder Elektrofreigabe.</strong>
        Die Maßangaben beziehen sich auf die reale geneigte Dachfläche (nicht auf die horizontale Projektion).
        Dachrand-, Hindernis- und Modulabstände sind eigene Vorgaben. Standsicherheit, Wind-/Schneelasten,
        Brandschutz, Ertrag, Verschattung und Elektroinstallation werden nicht geprüft.</div>
      @if (error()) {<div class="alert error" role="alert">{{error()}}</div>}
      <section class="panel">
        <form (ngSubmit)="calculate()">
          <fieldset [disabled]="busy()">
            <legend>Aufmaß und Module</legend>
            <div class="form-grid">
              <div class="form-field"><label for="pv-width">Dachflächenbreite x (m)</label>
                <input id="pv-width" name="pv-width" type="number" step="any" min=".1" max="100" required [(ngModel)]="draft.roofPlaneWidthM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-height">Dachflächenhöhe y (m)</label>
                <input id="pv-height" name="pv-height" type="number" step="any" min=".1" max="100" required [(ngModel)]="draft.roofPlaneHeightM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-module-width">Modulbreite (m)</label>
                <input id="pv-module-width" name="pv-module-width" type="number" step="any" min=".1" max="5" required [(ngModel)]="draft.moduleWidthM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-module-height">Modulhöhe (m)</label>
                <input id="pv-module-height" name="pv-module-height" type="number" step="any" min=".1" max="5" required [(ngModel)]="draft.moduleHeightM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-module-power">Nennleistung je Modul (Wp)</label>
                <input id="pv-module-power" name="pv-module-power" type="number" step="any" min="10" max="1500" required [(ngModel)]="draft.moduleRatedPowerWp" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-orientation">Modulausrichtung</label>
                <select id="pv-orientation" name="pv-orientation" [(ngModel)]="draft.orientation" (ngModelChange)="edited()">
                  <option value="MostModules">Mehr Module (Hoch/Quer vergleichen)</option>
                  <option value="Portrait">Hochformat</option><option value="Landscape">Querformat</option>
                </select></div>
              <div class="form-field"><label for="pv-edge">Allseitiger Dachrandabstand (m)</label>
                <input id="pv-edge" name="pv-edge" type="number" step="any" min="0" max="5" required [(ngModel)]="draft.edgeClearanceM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-gap-x">Modulabstand x (m)</label>
                <input id="pv-gap-x" name="pv-gap-x" type="number" step="any" min="0" max="1" required [(ngModel)]="draft.moduleGapXM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-gap-y">Modulabstand y (m)</label>
                <input id="pv-gap-y" name="pv-gap-y" type="number" step="any" min="0" max="1" required [(ngModel)]="draft.moduleGapYM" (ngModelChange)="edited()"></div>
              <div class="form-field"><label for="pv-obstacle-gap">Zusätzlicher Hindernisabstand (m)</label>
                <input id="pv-obstacle-gap" name="pv-obstacle-gap" type="number" step="any" min="0" max="5" required [(ngModel)]="draft.exclusionClearanceM" (ngModelChange)="edited()"></div>
              <div class="form-field full-width"><label for="pv-roof-source">Quelle und Stand des Dachaufmaßes</label>
                <input id="pv-roof-source" name="pv-roof-source" required maxlength="500" [(ngModel)]="draft.roofMeasurementSource" (ngModelChange)="edited()" placeholder="Aufmaß / Planrevision / Datum"></div>
              <div class="form-field full-width"><label for="pv-module-source">Modul-Datenblatt und Version</label>
                <input id="pv-module-source" name="pv-module-source" required maxlength="500" [(ngModel)]="draft.moduleDataSource" (ngModelChange)="edited()" placeholder="Hersteller / Typ / Datenstand"></div>
            </div>
            <div class="line-heading"><h2>Ausschlussrechtecke auf der Dachfläche</h2>
              <button type="button" class="button secondary" [disabled]="draft.exclusions.length >= 64" (click)="addExclusion()">Hindernis hinzufügen</button></div>
            <p>Koordinaten (x/y) beginnen links unten. Berührung ohne zusätzlichen Abstand ist geometrisch zulässig.</p>
            @for (item of draft.exclusions; track $index; let i = $index) {
              <section class="line-row">
                <div class="line-heading"><h3>Hindernis {{i + 1}}</h3><button type="button" class="text-button danger-text" (click)="removeExclusion(i)">Entfernen</button></div>
                <div class="form-grid">
                  <div class="form-field"><label [for]="'pv-name-'+i">Bezeichnung</label>
                    <input [id]="'pv-name-'+i" [name]="'pv-name-'+i" required maxlength="200" [(ngModel)]="item.name" (ngModelChange)="edited()"></div>
                  <div class="form-field"><label [for]="'pv-x-'+i">x (m)</label>
                    <input [id]="'pv-x-'+i" [name]="'pv-x-'+i" type="number" step="any" min="0" required [(ngModel)]="item.x" (ngModelChange)="edited()"></div>
                  <div class="form-field"><label [for]="'pv-y-'+i">y (m)</label>
                    <input [id]="'pv-y-'+i" [name]="'pv-y-'+i" type="number" step="any" min="0" required [(ngModel)]="item.y" (ngModelChange)="edited()"></div>
                  <div class="form-field"><label [for]="'pv-ow-'+i">Breite (m)</label>
                    <input [id]="'pv-ow-'+i" [name]="'pv-ow-'+i" type="number" step="any" min=".001" required [(ngModel)]="item.widthM" (ngModelChange)="edited()"></div>
                  <div class="form-field"><label [for]="'pv-oh-'+i">Höhe (m)</label>
                    <input [id]="'pv-oh-'+i" [name]="'pv-oh-'+i" type="number" step="any" min=".001" required [(ngModel)]="item.heightM" (ngModelChange)="edited()"></div>
                </div>
              </section>
            }
            <div class="actions"><button type="submit" class="button primary" [disabled]="busy()">{{busy() ? 'Berechnung läuft…' : 'Modulbelegung berechnen'}}</button></div>
          </fieldset>
        </form>
      </section>
      @if (result(); as plan) {
        <section class="panel" aria-label="PV-Berechnungsergebnis">
          <h2>Geometrischer Belegungsentwurf</h2>
          <div class="summary-grid">
            <div><strong>{{plan.installedModules}}</strong><span>Module</span></div>
            <div><strong>{{plan.nominalDcPowerWp}} Wp</strong><span>Nennleistung DC</span></div>
            <div><strong>{{plan.installedModuleAreaM2}} m²</strong><span>Modulfläche</span></div>
          </div>
          <p>Raster {{plan.gridCandidateCount}}, ausgeschlossene Plätze {{plan.obstructionRejectedCount}},
            Dachfläche {{plan.roofPlaneAreaM2}} m², Ausrichtung {{plan.selectedOrientation === 'Portrait' ? 'Hochformat' : 'Querformat'}}.</p>
          <p><strong>Kein geprüfter Ausführungsplan.</strong> Provenienz (SHA-256): <code>{{plan.provenance.inputSha256}}</code></p>
          <div class="actions"><button type="button" class="button secondary" (click)="exportJson()">Prüfbares Ergebnis als JSON</button></div>
          @if (plan.placements.length > 100) {<p>Anzeige der ersten 100 von {{plan.placements.length}} Positionen. Der JSON-Export enthält alle.</p>}
          <div class="table-wrap"><table><thead><tr><th>Reihe</th><th>Spalte</th><th>x (m)</th><th>y (m)</th><th>Breite (m)</th><th>Höhe (m)</th></tr></thead>
            <tbody>@for (p of displayed(); track p.row+'-'+p.column) {
              <tr><td>{{p.row}}</td><td>{{p.column}}</td><td>{{p.x}}</td><td>{{p.y}}</td><td>{{p.widthM}}</td><td>{{p.heightM}}</td></tr>
            }</tbody></table></div>
        </section>
      }
    }
  `,
  styles: [String.raw`
    .page-heading{display:flex;justify-content:space-between;align-items:center;gap:1rem;flex-wrap:wrap}
    .line-heading{display:flex;justify-content:space-between;gap:1rem;align-items:center;flex-wrap:wrap}
    .form-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:1rem}
    .form-field.full-width{grid-column:1/-1}
    .form-field{display:flex;flex-direction:column;gap:.35rem}
    .summary-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:1rem}
    .summary-grid>div{display:flex;flex-direction:column;gap:.35rem}
    .summary-grid strong{font-size:1.5rem}
    .table-wrap{max-width:100%;overflow-x:auto}
    table{width:100%;border-collapse:collapse}
    th,td{padding:.5rem;text-align:left;border-bottom:1px solid var(--border,#ddd)}
    fieldset{border:0;padding:0;margin:0;min-width:0}
    .actions{margin-top:1.25rem}
  `]
})
export class PvLayoutComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => !!this.auth.scope() && this.auth.hasModule('technical') && this.auth.can('technical-results.write'));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly result = signal<PvResult | null>(null);
  readonly displayed = computed(() => this.result()?.placements.slice(0, 100) ?? []);
  draft: PvDraft = emptyDraft();
  private generation = 0;
  constructor() {
    inject(DestroyRef).onDestroy(() => { this.generation++; });
    effect(() => { this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  private reset(): void {
    this.generation++;
    this.busy.set(false);
    this.error.set('');
    this.result.set(null);
    this.draft = emptyDraft();
  }
  edited(): void { this.generation++; this.result.set(null); this.error.set(''); }
  addExclusion(): void {
    if (this.draft.exclusions.length >= 64 || this.busy()) return;
    this.draft.exclusions.push({ name: '', x: 0, y: 0, widthM: null, heightM: null });
    this.edited();
  }
  removeExclusion(index: number): void {
    if (this.busy()) return;
    this.draft.exclusions.splice(index, 1);
    this.edited();
  }
  async calculate(): Promise<void> {
    if (!this.allowed() || this.busy()) return;
    const revision = ++this.generation, access = this.auth.accessSignature();
    this.error.set(''); this.result.set(null);
    let input;
    try { input = validPvInput(this.draft); }
    catch (error) { this.error.set(errorMessage(error)); return; }
    this.busy.set(true);
    try {
      const raw = await this.api.command<unknown>('technical/roof/pv-layout', input);
      if (revision === this.generation && access === this.auth.accessSignature() && this.allowed())
        this.result.set(validPvResult(raw, input));
    } catch (error) {
      if (revision === this.generation && access === this.auth.accessSignature())
        this.error.set(errorMessage(error));
    } finally {
      if (revision === this.generation && access === this.auth.accessSignature()) this.busy.set(false);
    }
  }
  exportJson(): void {
    const result = this.result();
    if (!this.allowed() || !result || this.busy()) return;
    downloadBlob(new Blob([JSON.stringify(result, null, 2)], { type: 'application/json' }),
      'FirstBlick-PV-Geometrie-' + result.provenance.inputSha256.slice(0, 12) + '.json');
  }
}
