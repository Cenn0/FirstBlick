import { Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { validateOriginalSignature } from '../core/equipment-documents.models';
import { downloadBlob, sha256Blob } from '../core/integrations.models';
import { LedgerOriginal, LedgerOriginalKind, LedgerOriginalSource, LedgerOriginals, ledgerOriginalFileType,
  validLedgerOriginal, validLedgerOriginals, verifiedLedgerOriginalDownload } from '../core/ledger-originals.models';
import { ApiError, errorMessage } from '../core/transport';

@Component({
  selector: 'app-ledger-originals',
  template: `
    <section class="originals" [attr.aria-label]="heading()">
      <div class="original-heading"><h4>{{heading()}}</h4><button class="button secondary small" type="button" (click)="toggle()"
        [attr.aria-expanded]="opened()">{{opened()?'Originaldateien schließen':'Originaldateien öffnen'}}</button></div>
      @if(opened()){
        @if(error()){<p class="alert error" role="alert">{{error()}}</p>}
        @if(notice()){<p class="alert information" role="status">{{notice()}}</p>}
        @if(loading()){<p role="status">Originaldateien werden geladen …</p>}
        @if(documents();as list){
          @if(!list.canAttach){<p class="small muted">Originaldateien bleiben lesbar. Neue Übertragungen sind für diesen Eintrag oder Ihre aktuellen Rechte gesperrt.</p>}
          <ul class="original-list">@for(doc of list.items;track doc.id){
            <li><div><strong>{{doc.name}}</strong><span>{{size(doc.size)}} · {{state(doc.state)}}</span></div>
              @if(doc.state==='Staged'){
                @if(writable()){<button class="button small" type="button" (click)="resume(doc)" [disabled]="busy()||loading()">Übertragung fortsetzen</button>}
              }@else{<button class="button small" type="button" (click)="download(doc)" [disabled]="busy()||loading()">Original herunterladen</button>}
            </li>
          }@empty{<li>Noch keine Originaldatei zu diesem Eintrag hinterlegt.</li>}</ul>
          <div class="original-pager"><button class="button small" type="button" (click)="load(list.page-1)" [disabled]="list.page<=1||loading()||busy()">Vorherige Originale</button>
            <span>Seite {{list.page}} · {{list.total}} Dateien</span>
            <button class="button small" type="button" (click)="load(list.page+1)" [disabled]="list.page*list.pageSize>=list.total||loading()||busy()">Weitere Originale</button>
            <button class="text-button" type="button" (click)="load(list.page)" [disabled]="loading()||busy()">Originaldateien neu laden</button></div>
        }
        @if(writable()){
          <div class="original-upload">
            @if(staged();as doc){<p>Begonnene Übertragung: <strong>{{doc.name}}</strong>. Wählen Sie dieselbe Originaldatei erneut.</p>
              <button class="text-button" type="button" (click)="newUpload()" [disabled]="busy()">Andere Originaldatei ergänzen</button>}
            <label [for]="'ledger-original-'+source().id">Originaldatei auswählen</label>
            <input [id]="'ledger-original-'+source().id" type="file" accept=".pdf,.png,.jpg,.jpeg" (change)="choose($event)" [disabled]="busy()||loading()">
            @if(file();as selected){<p class="small">Ausgewählt: <strong>{{selected.name}}</strong> · {{size(selected.size)}}</p>}
            <p class="small muted">PDF, PNG oder JPEG bis 25 MiB. Die Datei bleibt mit diesem Originaleintrag verknüpft; Inhalt und Betrag des Eintrags bleiben erhalten.</p>
            <button class="button primary small" type="button" (click)="upload()" [disabled]="busy()||loading()||!file()">Originaldatei speichern</button>
            @if(progress()){<p role="status">{{progress()}}</p>}
          </div>
        }
      }
    </section>`,
  styles: [':host{display:block;min-width:0}.originals{margin-top:1rem;border-top:1px solid var(--line,#dce3df);padding-top:1rem}.original-heading,.original-pager{display:flex;align-items:center;justify-content:space-between;gap:.7rem;flex-wrap:wrap}.original-heading h4{margin:0}.original-list{list-style:none;padding:0;margin:1rem 0}.original-list li{display:flex;justify-content:space-between;align-items:center;gap:.75rem;padding:.75rem 0;border-bottom:1px solid var(--line,#dce3df);flex-wrap:wrap}.original-list strong,.original-list span{display:block;overflow-wrap:anywhere}.original-list span{font-size:.85rem;color:var(--muted,#54625b)}.original-upload{display:grid;gap:.6rem;margin-top:1rem}.original-upload p,.alert{overflow-wrap:anywhere}.original-upload input{width:100%;max-width:100%;box-sizing:border-box}.original-upload .button{justify-self:start}.original-pager{font-size:.85rem}']
})
export class LedgerOriginalsComponent {
  readonly kind = input.required<LedgerOriginalKind>(); readonly source = input.required<LedgerOriginalSource>(); readonly allowUpload = input(false);
  readonly saved = output<void>();
  private readonly api = inject(ApiService); private readonly auth = inject(AuthService);
  readonly heading = computed(() => this.kind() === 'customer-communications' ? 'Originaldateien zur Kommunikation' : 'Originalkostenbelege');
  readonly readable = computed(() => this.auth.can(`${this.kind() === 'customer-communications' ? 'customers' : 'vehicle-costs'}.read`));
  readonly writable = computed(() => this.readable() && this.allowUpload() && this.documents()?.canAttach === true
    && this.auth.can(`${this.kind() === 'customer-communications' ? 'customers' : 'vehicle-costs'}.write`)
    && (this.kind() === 'customer-communications' ? ['ManagingDirector', 'Office'] : ['ManagingDirector', 'Office', 'SiteManager']).includes(this.auth.tenant()?.role || '')
    && this.auth.hasModule(this.kind() === 'customer-communications' ? 'core' : 'purchasing') && !this.auth.offlineSession() && !this.auth.recoveryHold());
  readonly opened = signal(false); readonly documents = signal<LedgerOriginals | null>(null); readonly loading = signal(false); readonly busy = signal(false);
  readonly error = signal(''); readonly notice = signal(''); readonly file = signal<File | null>(null); readonly staged = signal<LedgerOriginal | null>(null); readonly progress = signal('');
  private generation = 0; private listVersion = 0; private stageKey = crypto.randomUUID(); private uploadKey = crypto.randomUUID(); private stageIdentity: string | null = null;
  constructor() {
    effect(() => { this.kind(); this.source(); this.auth.accessSignature(); untracked(() => this.reset()); });
    inject(DestroyRef).onDestroy(() => { this.generation++; this.listVersion++; });
  }
  private reset(): void {
    this.generation++; this.listVersion++; this.opened.set(false); this.documents.set(null); this.loading.set(false); this.busy.set(false);
    this.error.set(''); this.notice.set(''); this.progress.set(''); this.clearFile();
  }
  private clearFile(): void { this.file.set(null); this.staged.set(null); this.stageIdentity = null; this.stageKey = crypto.randomUUID(); this.uploadKey = crypto.randomUUID(); }
  private context(): () => boolean {
    const generation = this.generation, access = this.auth.accessSignature(), source = this.source(), kind = this.kind();
    return () => generation === this.generation && access === this.auth.accessSignature() && this.opened() && this.readable()
      && kind === this.kind() && source.id === this.source().id && source.digest === this.source().digest;
  }
  private path(): string { return `${this.kind()}/${this.source().id}/documents`; }
  toggle(): void { if (this.opened()) this.reset(); else if (this.readable()) { this.opened.set(true); void this.load(1); } }
  size(value: number): string { return value < 1024 * 1024 ? `${Math.ceil(value / 1024)} KiB` : `${(value / 1024 / 1024).toFixed(1)} MiB`; }
  state(value: string): string { return value === 'Ready' ? 'Gespeichert und geprüft' : value === 'Archived' ? 'Archiviert · Original erhalten' : 'Übertragung unvollständig'; }
  async load(page: number): Promise<void> {
    if (!this.opened() || !this.readable() || page < 1) return;
    const current = this.context(), version = ++this.listVersion, source = this.source(), kind = this.kind(), tenant = this.auth.activeTenantId();
    this.loading.set(true); this.error.set('');
    try {
      const raw = await this.api.get<unknown>(`${this.path()}?page=${page}`);
      if (!current() || version !== this.listVersion) return;
      this.documents.set(validLedgerOriginals(raw, kind, source, tenant, page));
      if (!this.documents()!.canAttach) this.clearFile();
    } catch (error) { if (current() && version === this.listVersion) { this.documents.set(null); this.error.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (current() && version === this.listVersion) this.loading.set(false); }
  }
  choose(event: Event): void {
    const target = event.target as HTMLInputElement;
    if (!this.opened() || !this.writable() || this.busy() || this.loading() || !target?.isConnected || target.id !== `ledger-original-${this.source().id}`) return;
    this.file.set(null); this.error.set(''); const file = target.files?.[0]; target.value = ''; if (!file) return;
    try { ledgerOriginalFileType(file.name, file.size); this.file.set(file); } catch (error) { this.error.set(errorMessage(error)); }
  }
  resume(document: LedgerOriginal): void {
    if (this.busy() || this.loading() || !this.writable() || document.state !== 'Staged') return;
    this.clearFile(); this.staged.set(document); this.error.set(''); this.notice.set('Wählen Sie die ursprüngliche Datei zum Fortsetzen aus.');
  }
  newUpload(): void { if (!this.busy()) { this.clearFile(); this.error.set(''); this.notice.set(''); } }
  async upload(): Promise<void> {
    const file = this.file(); if (!file || this.busy() || this.loading() || !this.writable() || !this.opened()) return;
    const current = this.context(), source = this.source(), kind = this.kind(), tenant = this.auth.activeTenantId(), path = this.path();
    let staged = this.staged(); this.busy.set(true); this.error.set(''); this.notice.set('');
    try {
      const type = ledgerOriginalFileType(file.name, file.size); this.progress.set('Originaldatei wird geprüft …');
      await validateOriginalSignature(file, type); const sha256 = await sha256Blob(file);
      if (!current() || !this.writable()) return;
      if (staged && (staged.name !== file.name || staged.size !== file.size || staged.contentType !== type || staged.sha256 !== sha256))
        throw new Error('Die gewählte Datei entspricht nicht der begonnenen Originalübertragung. Wählen Sie dieselbe Datei.');
      const metadata = { name: file.name, size: file.size, contentType: type, sha256, projectId: null };
      if (!staged) {
        const identity = JSON.stringify(metadata); if (this.stageIdentity !== null && this.stageIdentity !== identity) this.stageKey = crypto.randomUUID(); this.stageIdentity = identity;
        const raw = await this.api.command<unknown>(path, metadata, source.revision, this.stageKey);
        if (!current() || !this.writable()) return;
        staged = validLedgerOriginal(raw, kind, source, tenant);
        if (staged.state !== 'Staged' || staged.name !== file.name || staged.size !== file.size || staged.contentType !== type || staged.sha256 !== sha256)
          throw new Error('Die vorbereitete Übertragung bestätigt andere Dateiangaben. Laden Sie die Originaldateien neu.');
        this.staged.set(staged);
      }
      this.progress.set('Originaldatei wird übertragen und im Speicher geprüft …');
      const raw = await this.api.upload<unknown>(`${path}/${staged.id}/content`, file, type, staged.revision, this.uploadKey);
      if (!current() || !this.writable()) return;
      const ready = validLedgerOriginal(raw, kind, source, tenant);
      if (ready.id !== staged.id || ready.state !== 'Ready' || ready.revision !== staged.revision + 1 || ready.name !== staged.name
        || ready.sha256 !== staged.sha256 || ready.size !== staged.size || ready.contentType !== staged.contentType)
        throw new Error('Die Antwort bestätigt nicht die vollständig gespeicherte Originaldatei. Laden Sie die Originaldateien neu.');
      this.clearFile(); this.notice.set('Originaldatei vollständig gespeichert und geprüft.'); this.saved.emit(); await this.load(1);
    } catch (error) {
      if (current()) {
        if (error instanceof ApiError && error.problem.code === 'ledger_original_closed') {
          this.documents.update(value => value ? { ...value, canAttach: false } : value); this.clearFile();
          this.error.set('Der Eintrag wurde storniert oder seine Akte archiviert. Gespeicherte Originaldateien bleiben lesbar; neue Übertragungen sind gesperrt.');
        } else this.error.set(errorMessage(error));
        await this.refreshAccess(error);
      }
    }
    finally { if (current()) { this.busy.set(false); this.progress.set(''); } }
  }
  async download(document: LedgerOriginal): Promise<void> {
    if (this.busy() || this.loading() || document.state === 'Staged' || !this.opened() || !this.readable()) return;
    const current = this.context(); this.busy.set(true); this.error.set('');
    try {
      validLedgerOriginal(document, this.kind(), this.source(), this.auth.activeTenantId());
      const response = await this.api.downloadResponse(`${this.path()}/${document.id}/download`); if (!current()) return;
      const blob = await verifiedLedgerOriginalDownload(response, document); if (!current()) return;
      downloadBlob(blob, document.name);
    } catch (error) { if (current()) { this.error.set(errorMessage(error)); await this.refreshAccess(error); } }
    finally { if (current()) this.busy.set(false); }
  }
  private async refreshAccess(error: unknown): Promise<void> { if (error instanceof ApiError && [401, 403].includes(error.status)) await this.auth.refresh().catch(() => undefined); }
}
