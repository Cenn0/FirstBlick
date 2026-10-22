import { Component, DestroyRef, ElementRef, ViewChild, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';
import { downloadBlob,sha256Blob } from '../core/integrations.models';
import { isLedgerOriginalKind, LedgerOriginalContext, validLedgerOriginalContext } from '../core/ledger-originals.models';
import { LedgerOriginalsComponent } from './ledger-originals.component';

interface DocumentEntry extends Entity {name:string;contentType:string;size:number;sha256:string;state:string;projectId?:string;sourceKind?:string;sourceId?:string;sourceDigest?:string;createdAt:string}
const maximumBytes=25*1024*1024;
const fileTypes:Record<string,string>={pdf:'application/pdf',png:'image/png',jpg:'image/jpeg',jpeg:'image/jpeg',txt:'text/plain',csv:'text/csv'};

@Component({imports:[FormsModule,RouterLink,LedgerOriginalsComponent],template:`
  <header class="page-heading"><div><p class="eyebrow">PROJEKTUNTERLAGEN</p><h1>Dokumente</h1><p>Geprüfte Dateiversionen, Belege und Baustellenfotos im richtigen Projekt.</p></div>@if(auth.can('documents.write')){<button class="button primary" (click)="openUpload()" [disabled]="busy()">+ Datei hochladen</button>}</header>
  @if(!auth.can('documents.read')){<div class="alert warning">Für diesen Bereich fehlt die Leseberechtigung.</div>}@else{
    @if(error()){<div class="alert error" role="alert">{{error()}} <button class="text-button" (click)="load()">Erneut laden</button></div>}
    @if(notice()){<div class="alert success" role="status">{{notice()}}</div>}
    <section class="panel collection-panel"><div class="collection-toolbar"><form class="search-form" (ngSubmit)="search()"><label for="document-search" class="sr-only">Dokumente durchsuchen</label><input id="document-search" name="search" [(ngModel)]="searchText" placeholder="Dokumente durchsuchen …" maxlength="200"><button class="text-button">Suchen</button></form><span class="small muted">{{page().total}} Dateien</span></div>
      @if(loading()){<div class="loading-state" role="status">Dokumente werden geladen …</div>}
      @else if(page().items.length){<div class="table-wrap"><table><thead><tr><th>Name</th><th>Projekt</th><th>Größe</th><th>Status</th><th>Aktion</th></tr></thead><tbody>@for(doc of page().items;track doc.id){<tr><td data-label="Name"><strong>{{doc.name}}</strong><span class="document-date">{{date(doc.createdAt)}}</span></td><td data-label="Projekt">{{projectName(doc.projectId)}}</td><td data-label="Größe">{{size(doc.size)}}</td><td data-label="Status"><span class="badge">{{state(doc.state)}}</span></td><td data-label="Aktion">@if(doc.state==='Ready'){<button class="text-button" (click)="download(doc)" [disabled]="busy()">Herunterladen</button>@if(canShare(doc)){<a class="text-button" routerLink="/portal" [queryParams]="{new:1,kind:'documents',sourceId:doc.id}">Im Kundenportal freigeben</a>}@if(canArchive(doc)){<button class="text-button danger-text" (click)="archive(doc)" [disabled]="busy()">Archivieren</button>}}@else if(doc.state==='Staged'&&doc.sourceKind==='equipment-inspections'){<a class="text-button" routerLink="/equipment-inspections" [queryParams]="{inspectionId:doc.sourceId}">In der Geräteprüfakte fortsetzen</a>}@else if(doc.state==='Staged'&&isLedgerOriginal(doc)){<button class="text-button" type="button" (click)="openOriginal(doc)" [disabled]="busy()">In der Originalakte fortsetzen</button>}@else if(doc.state==='Staged'&&auth.can('documents.write')){<button class="text-button" (click)="resume(doc)">Upload fortsetzen</button>}@if(doc.state==='Ready'&&isLedgerOriginal(doc)){<button class="text-button" type="button" (click)="openOriginal(doc)" [disabled]="busy()">Originalakte öffnen</button>}</td></tr>}</tbody></table></div>}
      @else{<div class="empty-state"><span class="empty-icon" aria-hidden="true">D</span><h2>{{searchText?'Keine passenden Dokumente.':'Noch keine Dokumente vorhanden.'}}</h2><p>Fügen Sie Projektunterlagen und Fotos hinzu. Angebots- und Rechnungs-PDFs erstellen Sie direkt am Beleg.</p></div>}
      @if(page().total>page().pageSize){<div class="pagination"><button class="button secondary" (click)="paginate(-1)" [disabled]="pageNumber===1||loading()">← Zurück</button><span>Seite {{pageNumber}} von {{totalPages()}}</span><button class="button secondary" (click)="paginate(1)" [disabled]="pageNumber>=totalPages()||loading()">Weiter →</button></div>}
    </section>
    @if(originalContext();as context){<section class="panel" aria-label="Verknüpfte Originalakte"><div class="dialog-heading"><h2>{{context.title}}</h2><button class="text-button" type="button" (click)="closeOriginal()">Originalakte schließen</button></div><p class="small muted">Dateien zum unveränderlichen Originaleintrag. Die Berechtigung wird in der jeweiligen Akte geprüft.</p><app-ledger-originals [kind]="context.sourceKind" [source]="context.source" [allowUpload]="true" (saved)="load()" /></section>}
  }
  <dialog #uploadDialog class="action-dialog" (cancel)="cancel($event)"><form #uploadForm="ngForm" (ngSubmit)="upload()"><header class="dialog-heading"><h2>{{staged()?'Upload fortsetzen':'Datei hochladen'}}</h2><button class="icon-button" type="button" aria-label="Upload schließen" [disabled]="busy()" (click)="close()">×</button></header><div class="dialog-body"><p class="small muted">PDF, PNG, JPEG, UTF-8-Text oder CSV bis 25 MiB. Neue Inhalte werden als eigenständige Dateiversion gespeichert.</p>
    @if(uploadError()){<div class="alert error" role="alert">{{uploadError()}}</div>}
    @if(staged();as doc){<div class="alert information">Wählen Sie erneut die ursprüngliche Datei <strong>{{doc.name}}</strong> ({{size(doc.size)}}). Inhalt und Prüfsumme müssen zum begonnenen Upload passen.</div>}
    <div class="form-field"><label for="upload-file">Datei auswählen *</label><input #fileInput id="upload-file" type="file" accept=".pdf,.png,.jpg,.jpeg,.txt,.csv" (change)="choose($event)" [disabled]="busy()"></div>
    @if(!staged()){<div class="form-field workflow-field"><label for="upload-project">Projekt{{auth.tenant()?.role==='Worker'?' *':''}}</label><input [disabled]="busy()" name="projectSearch" aria-label="Projekt für Dokument suchen" placeholder="Projekte durchsuchen" (input)="loadProjects($any($event.target).value)"><select [disabled]="busy()" id="upload-project" name="projectId" [(ngModel)]="projectId" [required]="auth.tenant()?.role==='Worker'"><option value="">Allgemeine Betriebsunterlage</option>@for(project of projects();track project.id){<option [value]="project.id">{{project['name']}}</option>}</select></div>}
    @if(progress()){<p role="status" class="small">{{progress()}}</p>}
  </div><footer class="dialog-footer"><button type="button" class="button secondary" (click)="close()" [disabled]="busy()">Schließen</button><button class="button primary" [disabled]="busy()||!file()||uploadForm.invalid">{{busy()?'Wird hochgeladen …':'Datei speichern'}}</button></footer></form></dialog>
`})
export class DocumentsComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);
  readonly page=signal<Page<DocumentEntry>>({items:[],total:0,page:1,pageSize:25});readonly projects=signal<Entity[]>([]);
  readonly busy=signal(false);readonly loading=signal(false);readonly error=signal('');readonly notice=signal('');readonly uploadError=signal('');readonly progress=signal('');
  readonly file=signal<File|null>(null);readonly staged=signal<DocumentEntry|null>(null);readonly originalContext=signal<LedgerOriginalContext|null>(null);private originalVersion=0;
  @ViewChild('uploadDialog')uploadDialog?:ElementRef<HTMLDialogElement>;@ViewChild('fileInput')fileInput?:ElementRef<HTMLInputElement>;
  searchText='';pageNumber=1;projectId='';private filterProject='';private selectedId='';private access='';private generation=0;private stageKey=crypto.randomUUID();private uploadKey=crypto.randomUUID();private loadVersion=0;private projectVersion=0;
  constructor(){
    inject(DestroyRef).onDestroy(()=>{this.generation++;this.loadVersion++;this.projectVersion++;this.originalVersion++;});
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(params=>{this.filterProject=params.get('projectId')||'';this.selectedId=params.get('selected')||'';void this.load();});
    effect(()=>{const access=this.auth.accessSignature();untracked(()=>{if(access!==this.access){this.access=access;this.generation++;this.loadVersion++;this.projectVersion++;this.page.set({items:[],total:0,page:1,pageSize:25});this.projects.set([]);this.originalVersion++;this.originalContext.set(null);this.staged.set(null);this.file.set(null);this.projectId='';this.uploadDialog?.nativeElement.close();if(this.fileInput)this.fileInput.nativeElement.value='';this.notice.set('');this.error.set('');this.uploadError.set('');this.progress.set('');this.loading.set(false);this.busy.set(false);void this.load();void this.loadProjects();}});});
  }
  private current(generation:number,access:string):boolean{return generation===this.generation&&access===this.auth.accessSignature();}
  state(state:string):string{return ({Ready:'Geprüft & verfügbar',Staged:'Upload unvollständig',Archived:'Archiviert'}as Record<string,string>)[state]||state;}
  date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium'}).format(new Date(value));}
  size(bytes:number):string{return bytes>=1024*1024?`${new Intl.NumberFormat('de-DE',{maximumFractionDigits:2}).format(bytes/(1024*1024))} MiB`:`${new Intl.NumberFormat('de-DE',{maximumFractionDigits:1}).format(bytes/1024)} KiB`;}
  projectName(id?:string):string{return id?String(this.projects().find(x=>x.id===id)?.['name']||'Projektunterlage'):'Betriebsunterlage';}
  isLedgerOriginal(doc:DocumentEntry):boolean{return isLedgerOriginalKind(doc.sourceKind);}
  canArchive(doc?:DocumentEntry):boolean{return this.auth.can('documents.write')&&['ManagingDirector','Office'].includes(this.auth.tenant()?.role||'')&&(!doc||!isLedgerOriginalKind(doc.sourceKind)||(this.auth.can(`${doc.sourceKind==='customer-communications'?'customers':doc.sourceKind}.write`)&&this.auth.hasModule(doc.sourceKind==='customer-communications'?'core':'purchasing')));}
  closeOriginal():void{this.originalVersion++;this.originalContext.set(null);}
  async openOriginal(doc:DocumentEntry):Promise<void>{
    if(this.busy()||!isLedgerOriginalKind(doc.sourceKind)||!doc.sourceId||!doc.sourceDigest)return;
    const generation=this.generation,access=this.auth.accessSignature(),version=++this.originalVersion,kind=doc.sourceKind,tenant=this.auth.activeTenantId();this.originalContext.set(null);this.busy.set(true);this.error.set('');
    try{const raw=await this.api.get<unknown>(`${kind}/${doc.sourceId}/document-context`);if(!this.current(generation,access)||version!==this.originalVersion)return;this.originalContext.set(validLedgerOriginalContext(raw,kind,doc.sourceId,doc.sourceDigest,tenant));}
    catch(error){if(this.current(generation,access)&&version===this.originalVersion)this.error.set(errorMessage(error));}
    finally{if(this.current(generation,access))this.busy.set(false);}
  }
  canShare(doc:DocumentEntry):boolean{return doc.state==='Ready'&&!!doc.projectId&&this.auth.hasModule('portal')&&this.auth.can('portal-shares.write');}
  totalPages():number{return Math.ceil(this.page().total/this.page().pageSize);}
  search():void{this.pageNumber=1;void this.load();}paginate(delta:number):void{this.pageNumber+=delta;void this.load();}
  async load():Promise<void>{
    if(!this.auth.scope()||!this.auth.can('documents.read'))return;const generation=this.generation,access=this.auth.accessSignature(),version=++this.loadVersion;this.loading.set(true);this.error.set('');
    try{const result=await this.api.get<Page<DocumentEntry>>(`documents?${new URLSearchParams({page:String(this.pageNumber),pageSize:'25',search:this.searchText,...(this.filterProject?{projectId:this.filterProject}:{})})}`);
      if(!this.current(generation,access)||version!==this.loadVersion)return;this.page.set(result);const selectedId=this.selectedId;
      if(selectedId){const doc=await this.api.get<DocumentEntry>(`documents/${encodeURIComponent(selectedId)}`);if(!this.current(generation,access)||version!==this.loadVersion)return;if(!result.items.some(x=>x.id===doc.id))this.page.update(page=>({...page,items:[doc,...page.items]}));if(this.selectedId===selectedId)this.selectedId='';}
    }catch(error){if(this.current(generation,access)&&version===this.loadVersion)this.error.set(errorMessage(error));}finally{if(this.current(generation,access)&&version===this.loadVersion)this.loading.set(false);}
  }
  async loadProjects(search=''):Promise<void>{if(!this.auth.can('projects.read'))return;const generation=this.generation,access=this.auth.accessSignature(),version=++this.projectVersion;try{const result=await this.api.get<Page>(`projects?${new URLSearchParams({search,pageSize:'100'})}`);if(this.current(generation,access)&&version===this.projectVersion)this.projects.set(result.items);}catch(error){if(this.current(generation,access)&&version===this.projectVersion)this.uploadError.set(errorMessage(error));}}
  openUpload():void{if(!this.auth.can('documents.write')||this.busy())return;this.staged.set(null);this.prepareUpload();this.projectId=this.filterProject;}
  resume(doc:DocumentEntry):void{if(!this.auth.can('documents.write')||this.busy()||doc.sourceKind==='equipment-inspections'||this.isLedgerOriginal(doc))return;this.staged.set(doc);this.prepareUpload();}
  private prepareUpload():void{this.file.set(null);this.stageKey=crypto.randomUUID();this.uploadKey=crypto.randomUUID();this.uploadError.set('');this.progress.set('');if(this.fileInput)this.fileInput.nativeElement.value='';this.uploadDialog?.nativeElement.showModal();}
  choose(event:Event):void{const file=(event.target as HTMLInputElement).files?.[0]||null;this.file.set(file);this.uploadError.set('');if(file&&(file.size===0||file.size>maximumBytes)){this.uploadError.set('Die Datei muss zwischen einem Byte und 25 MiB groß sein.');this.file.set(null);}}
  cancel(event:Event):void{if(this.busy())event.preventDefault();}
  close():void{if(!this.busy())this.uploadDialog?.nativeElement.close();}
  async upload():Promise<void>{
    const file=this.file();if(!file||this.busy()||!this.auth.can('documents.write'))return;const generation=this.generation,access=this.auth.accessSignature(),projectId=this.projectId,stageKey=this.stageKey,uploadKey=this.uploadKey;let staged=this.staged();this.busy.set(true);this.uploadError.set('');
    try{const type=fileTypes[file.name.split('.').pop()!.toLowerCase()];if(!type)throw new Error('Dieses Dateiformat wird nicht unterstützt.');
      this.progress.set('Datei wird geprüft …');const sha256=await sha256Blob(file);if(!this.current(generation,access))return;
      if(staged&&(staged.name!==file.name||staged.sha256!==sha256||staged.size!==file.size||staged.contentType!==type))throw new Error('Die gewählte Datei entspricht nicht dem begonnenen Upload. Wählen Sie die ursprüngliche Datei.');
      if(!staged){this.progress.set('Dokument wird angelegt …');staged=await this.api.command<DocumentEntry>('documents',{name:file.name,contentType:type,size:file.size,sha256,projectId:projectId||null},undefined,stageKey);if(!this.current(generation,access))return;this.staged.set(staged);}
      if(!this.current(generation,access))return;this.progress.set('Datei wird übertragen und im Speicher geprüft …');await this.api.upload<DocumentEntry>(`documents/${staged.id}/content`,file,type,staged.revision,uploadKey);
      if(this.current(generation,access)){this.uploadDialog?.nativeElement.close();this.notice.set('Datei vollständig übertragen und geprüft.');this.file.set(null);this.staged.set(null);await this.load();}
    }catch(error){if(this.current(generation,access))this.uploadError.set(errorMessage(error));}finally{if(this.current(generation,access)){this.busy.set(false);this.progress.set('');}}
  }
  async download(doc:DocumentEntry):Promise<void>{
    if(this.busy()||!this.auth.can('documents.read'))return;const generation=this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');
    try{const blob=await this.api.download(`documents/${doc.id}/download`);if(!this.current(generation,access))return;const sha=await sha256Blob(blob);if(!this.current(generation,access))return;if(sha!==doc.sha256)throw new Error('Die heruntergeladene Datei stimmt nicht mit der gespeicherten Prüfsumme überein.');downloadBlob(blob,doc.name);}catch(error){if(this.current(generation,access))this.error.set(errorMessage(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  async archive(doc:DocumentEntry):Promise<void>{
    if(this.busy()||!this.canArchive(doc)||!confirm(`„${doc.name}“ archivieren? Die gespeicherte Version bleibt erhalten.`))return;const generation=this.generation,access=this.auth.accessSignature();this.busy.set(true);
    try{await this.api.command(`documents/${doc.id}/archive`,{},doc.revision);if(this.current(generation,access)){this.notice.set('Dokument archiviert.');await this.load();}}catch(error){if(this.current(generation,access))this.error.set(errorMessage(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
}
