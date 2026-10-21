import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { EquipmentInspection } from '../core/equipment-workflow.models';
import { InspectionDocument, InspectionDocuments, inspectionFileType, validateInspectionDocument,
  validateInspectionDocuments, validateOriginalSignature, verifiedInspectionDownload } from '../core/equipment-documents.models';
import { downloadBlob, sha256Blob } from '../core/integrations.models';

@Component({
  selector: 'app-equipment-documents',
  template: `
    <section class="originals" aria-label="Originalbelege zur Geräteprüfung">
      <div class="original-heading"><h4>Originalprüfberichte</h4>
        <button class="button secondary small" type="button" (click)="toggle()">{{opened()?'Originalbelege schließen':'Originalbelege öffnen'}}</button>
      </div>
      @if(opened()){
        @if(error()){<p class="alert error" role="alert">{{error()}}</p>}
        @if(notice()){<p class="alert information" role="status">{{notice()}}</p>}
        @if(loading()){<p role="status">Originalbelege werden geladen …</p>}
        @if(documents(); as list){
          <ul class="original-list">
            @for(doc of list.items; track doc.id){
              <li><div><strong>{{doc.name}}</strong><span>{{size(doc.size)}} · {{state(doc.state)}}</span></div>
                @if(doc.state==='Staged'){
                  @if(writable()){<button class="button small" type="button" (click)="resume(doc)" [disabled]="busy()">Übertragung fortsetzen</button>}
                }@else{<button class="button small" type="button" (click)="download(doc)" [disabled]="busy()">Original herunterladen</button>}
              </li>
            }@empty{<li>Noch keine Originaldatei zu diesem Prüfergebnis hinterlegt.</li>}
          </ul>
          <div class="original-pager"><button class="button small" type="button" (click)="load(list.page-1)" [disabled]="list.page<=1||loading()||busy()">Vorherige Originale</button>
            <span>Seite {{list.page}} · {{list.total}} Belege</span>
            <button class="button small" type="button" (click)="load(list.page+1)" [disabled]="list.page*list.pageSize>=list.total||loading()||busy()">Weitere Originale</button>
            <button class="text-button" type="button" (click)="load(list.page)" [disabled]="loading()||busy()">Belege neu laden</button></div>
        }
        @if(writable()){
          <div class="original-upload">
            @if(staged(); as doc){<p>Begonnene Übertragung: <strong>{{doc.name}}</strong>. Wählen Sie dieselbe Originaldatei erneut.</p>
              <button class="text-button" type="button" (click)="newUpload()" [disabled]="busy()">Andere Originaldatei ergänzen</button>}
            <label [for]="'inspection-file-'+inspection().id">Originalprüfbericht auswählen</label>
            <input [id]="'inspection-file-'+inspection().id" type="file" accept=".pdf,.png,.jpg,.jpeg" (change)="choose($event)" [disabled]="busy()">
            @if(file(); as selected){<p class="small">Ausgewählte Datei: <strong>{{selected.name}}</strong> · {{size(selected.size)}}</p>}
            <p class="small muted">PDF oder Bild bis 25 MiB. Die Datei wird dauerhaft diesem abgeschlossenen Prüfergebnis zugeordnet.</p>
            <button class="button primary small" type="button" (click)="upload()" [disabled]="busy()||!file()">Originalbeleg speichern</button>
            @if(progress()){<p role="status">{{progress()}}</p>}
          </div>
        }
      }
    </section>`,
  styles: [`:host{display:block;min-width:0}.originals{margin-top:1rem;border-top:1px solid var(--border,#dce3df);padding-top:1rem}.original-heading,.original-pager{display:flex;align-items:center;justify-content:space-between;gap:.7rem;flex-wrap:wrap}.original-heading h4{margin:0}.original-list{list-style:none;padding:0;margin:1rem 0}.original-list li{display:flex;justify-content:space-between;align-items:center;gap:.75rem;padding:.75rem 0;border-bottom:1px solid var(--border,#dce3df);flex-wrap:wrap}.original-list strong,.original-list span{display:block;overflow-wrap:anywhere}.original-list span{font-size:.85rem;color:var(--muted,#54625b)}.original-upload{display:grid;gap:.6rem;margin-top:1rem}.original-upload p{overflow-wrap:anywhere}.original-upload input{width:100%;max-width:100%;box-sizing:border-box}.original-upload .button{justify-self:start}.original-pager{font-size:.85rem}.alert{overflow-wrap:anywhere}`]
})
export class EquipmentDocumentsComponent implements OnDestroy {
  readonly inspection = input.required<EquipmentInspection>();
  private readonly api = inject(ApiService); private readonly auth = inject(AuthService);
  readonly writable = computed(() => this.auth.can('equipment-inspections.write') && this.auth.hasModule('purchasing')
    && ['ManagingDirector', 'Office', 'SiteManager'].includes(this.auth.tenant()?.role || '')
    && !this.auth.offlineSession() && !this.auth.recoveryHold());
  readonly opened = signal(false); readonly documents = signal<InspectionDocuments | null>(null);
  readonly loading = signal(false); readonly busy = signal(false); readonly error = signal(''); readonly notice = signal('');
  readonly file = signal<File | null>(null); readonly staged = signal<InspectionDocument | null>(null); readonly progress = signal('');
  private generation = 0; private listVersion = 0; private stageKey = crypto.randomUUID(); private uploadKey = crypto.randomUUID();
  private stageIdentity: string | null = null;

  constructor() {
    effect(() => { this.inspection(); this.auth.accessSignature(); untracked(() => this.reset()); });
  }
  ngOnDestroy(): void { this.generation++; this.listVersion++; }
  private reset(): void {
    this.generation++; this.listVersion++; this.opened.set(false); this.documents.set(null); this.loading.set(false);
    this.busy.set(false); this.error.set(''); this.notice.set(''); this.progress.set(''); this.clearFile();
  }
  private clearFile(): void {
    this.file.set(null); this.staged.set(null); this.stageIdentity = null;
    this.stageKey = crypto.randomUUID(); this.uploadKey = crypto.randomUUID();
  }
  private context(): () => boolean {
    const generation = this.generation, access = this.auth.accessSignature(), inspection = this.inspection();
    return () => generation === this.generation && access === this.auth.accessSignature() && this.opened()
      && this.auth.can('equipment-inspections.read') && inspection.id === this.inspection().id
      && inspection.completion?.digest === this.inspection().completion?.digest;
  }
  toggle(): void {
    if (this.opened()) this.reset();
    else { this.opened.set(true); void this.load(1); }
  }
  size(value: number): string { return value < 1024 * 1024 ? `${Math.ceil(value / 1024)} KiB` : `${(value / 1024 / 1024).toFixed(1)} MiB`; }
  state(value: string): string { return value === 'Ready' ? 'Gespeichert und geprüft' : value === 'Archived' ? 'Archiviert · Original erhalten' : 'Übertragung unvollständig'; }
  async load(page: number): Promise<void> {
    if (!this.opened() || !this.auth.can('equipment-inspections.read') || page < 1) return;
    const current = this.context(), version = ++this.listVersion, inspection = this.inspection(), tenant = this.auth.activeTenantId();
    this.loading.set(true); this.error.set('');
    try {
      const raw = await this.api.get<InspectionDocuments>(`equipment-inspections/${inspection.id}/documents?page=${page}`);
      if (!current() || version !== this.listVersion) return;
      this.documents.set(validateInspectionDocuments(raw, inspection, tenant, page));
    } catch (error) { if (current() && version === this.listVersion) { this.documents.set(null); this.error.set(errorMessage(error)); } }
    finally { if (current() && version === this.listVersion) this.loading.set(false); }
  }
  choose(event: Event): void {
    const target = event.target as HTMLInputElement;
    if (!this.opened() || !this.writable() || this.busy() || !target?.isConnected
      || target.id !== `inspection-file-${this.inspection().id}`) return;
    this.file.set(null); this.error.set('');
    const file = target.files?.[0]; target.value = ''; if (!file) return;
    try { inspectionFileType(file.name, file.size); this.file.set(file); }
    catch (error) { this.error.set(errorMessage(error)); }
  }
  resume(document: InspectionDocument): void {
    if (this.busy() || !this.writable() || document.state !== 'Staged') return;
    this.clearFile(); this.staged.set(document); this.error.set(''); this.notice.set('Wählen Sie die ursprüngliche Datei zum Fortsetzen aus.');
  }
  newUpload(): void { if (!this.busy()) { this.clearFile(); this.error.set(''); this.notice.set(''); } }
  async upload(): Promise<void> {
    const file = this.file(); if (!file || this.busy() || !this.writable() || !this.opened()) return;
    const current = this.context(), inspection = this.inspection(), tenant = this.auth.activeTenantId();
    let staged = this.staged(); this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const type = inspectionFileType(file.name, file.size); this.progress.set('Originaldatei wird geprüft …');
      await validateOriginalSignature(file, type); const sha256 = await sha256Blob(file);
      if (!current() || !this.writable()) return;
      if (staged && (staged.name !== file.name || staged.size !== file.size || staged.contentType !== type || staged.sha256 !== sha256))
        throw new Error('Die gewählte Datei entspricht nicht der begonnenen Originalübertragung. Wählen Sie dieselbe Datei.');
      const metadata = { name: file.name, size: file.size, contentType: type, sha256, projectId: null };
      if (!staged) {
        // Identical metadata retries retain their key; another original gets a fresh preferred key.
        // ApiService can still recover an earlier retained key for the exact request fingerprint.
        const identity = JSON.stringify(metadata);
        if (this.stageIdentity !== null && this.stageIdentity !== identity) this.stageKey = crypto.randomUUID();
        this.stageIdentity = identity;
        const raw = await this.api.command<InspectionDocument>(`equipment-inspections/${inspection.id}/documents`, metadata, inspection.revision, this.stageKey);
        if (!current() || !this.writable()) return;
        staged = validateInspectionDocument(raw, inspection, tenant);
        if (staged.state !== 'Staged' || staged.name !== file.name || staged.size !== file.size || staged.contentType !== type || staged.sha256 !== sha256)
          throw new Error('Die vorbereitete Originalübertragung bestätigt andere Dateiangaben. Laden Sie die Belege neu.');
        this.staged.set(staged);
      }
      this.progress.set('Originaldatei wird übertragen und im Speicher geprüft …');
      const raw = await this.api.upload<InspectionDocument>(`equipment-inspections/${inspection.id}/documents/${staged.id}/content`,
        file, type, staged.revision, this.uploadKey);
      if (!current() || !this.writable()) return;
      const ready = validateInspectionDocument(raw, inspection, tenant);
      if (ready.id !== staged.id || ready.state !== 'Ready' || ready.revision !== staged.revision + 1
        || ready.name !== staged.name || ready.sha256 !== staged.sha256 || ready.size !== staged.size || ready.contentType !== staged.contentType)
        throw new Error('Die Antwort bestätigt nicht die vollständig gespeicherte Originaldatei. Laden Sie die Belege neu.');
      this.clearFile(); this.notice.set('Originalbeleg vollständig gespeichert und geprüft.'); await this.load(1);
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) { this.busy.set(false); this.progress.set(''); } }
  }
  async download(document: InspectionDocument): Promise<void> {
    if (this.busy() || document.state === 'Staged' || !this.opened()) return;
    const current = this.context(), inspection = this.inspection(); this.busy.set(true); this.error.set('');
    try {
      validateInspectionDocument(document, inspection, this.auth.activeTenantId());
      const response = await this.api.downloadResponse(`equipment-inspections/${inspection.id}/documents/${document.id}/download`);
      if (!current()) return;
      const blob = await verifiedInspectionDownload(response, document);
      if (!current()) return;
      downloadBlob(blob, document.name);
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) this.busy.set(false); }
  }
}
