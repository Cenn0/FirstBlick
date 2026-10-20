import { Component, ElementRef, ViewChild, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';

interface DocumentEntry extends Entity {name:string;contentType:string;size:number;sha256:string;state:string;projectId?:string;sourceKind?:string;createdAt:string}
const maximumBytes=25*1024*1024;
const fileTypes:Record<string,string>={pdf:'application/pdf',png:'image/png',jpg:'image/jpeg',jpeg:'image/jpeg',txt:'text/plain',csv:'text/csv'};

@Component({imports:[FormsModule,RouterLink],template:`
  <header class="page-heading"><div><p class="eyebrow">PROJEKTUNTERLAGEN</p><h1>Dokumente</h1><p>Geprüfte Dateiversionen, Belege und Baustellenfotos im richtigen Projekt.</p></div>@if(auth.can('documents.write')){<button class="button primary" (click)="openUpload()">+ Datei hochladen</button>}</header>
  @if(!auth.can('documents.read')){<div class="alert warning">Für diesen Bereich fehlt die Leseberechtigung.</div>}@else{
    @if(error()){<div class="alert error" role="alert">{{error()}} <button class="text-button" (click)="load()">Erneut laden</button></div>}
    @if(notice()){<div class="alert success" role="status">{{notice()}}</div>}
    <section class="panel collection-panel"><div class="collection-toolbar"><form class="search-form" (ngSubmit)="search()"><label for="document-search" class="sr-only">Dokumente durchsuchen</label><input id="document-search" name="search" [(ngModel)]="searchText" placeholder="Dokumente durchsuchen …" maxlength="200"><button class="text-button">Suchen</button></form><span class="small muted">{{page().total}} Dateien</span></div>
      @if(loading()){<div class="loading-state" role="status">Dokumente werden geladen …</div>}
      @else if(page().items.length){<div class="table-wrap"><table><thead><tr><th>Name</th><th>Projekt</th><th>Größe</th><th>Status</th><th>Aktion</th></tr></thead><tbody>@for(doc of page().items;track doc.id){<tr><td data-label="Name"><strong>{{doc.name}}</strong><span class="document-date">{{date(doc.createdAt)}}</span></td><td data-label="Projekt">{{projectName(doc.projectId)}}</td><td data-label="Größe">{{size(doc.size)}}</td><td data-label="Status"><span class="badge">{{state(doc.state)}}</span></td><td data-label="Aktion">@if(doc.state==='Ready'){<button class="text-button" (click)="download(doc)" [disabled]="busy()">Herunterladen</button>@if(canShare(doc)){<a class="text-button" routerLink="/portal" [queryParams]="{new:1,kind:'documents',sourceId:doc.id}">Im Kundenportal freigeben</a>}@if(canArchive()){<button class="text-button danger-text" (click)="archive(doc)" [disabled]="busy()">Archivieren</button>}}@else if(doc.state==='Staged'&&auth.can('documents.write')){<button class="text-button" (click)="resume(doc)">Upload fortsetzen</button>}</td></tr>}</tbody></table></div>}
      @else{<div class="empty-state"><span class="empty-icon" aria-hidden="true">D</span><h2>{{searchText?'Keine passenden Dokumente.':'Noch keine Dokumente vorhanden.'}}</h2><p>Fügen Sie Projektunterlagen und Fotos hinzu. Angebots- und Rechnungs-PDFs erstellen Sie direkt am Beleg.</p></div>}
      @if(page().total>page().pageSize){<div class="pagination"><button class="button secondary" (click)="paginate(-1)" [disabled]="pageNumber===1||loading()">← Zurück</button><span>Seite {{pageNumber}} von {{totalPages()}}</span><button class="button secondary" (click)="paginate(1)" [disabled]="pageNumber>=totalPages()||loading()">Weiter →</button></div>}
    </section>
  }
  <dialog #uploadDialog class="action-dialog" (cancel)="cancel($event)"><form #uploadForm="ngForm" (ngSubmit)="upload()"><header class="dialog-heading"><h2>{{staged()?'Upload fortsetzen':'Datei hochladen'}}</h2><button class="icon-button" type="button" aria-label="Upload schließen" [disabled]="busy()" (click)="close()">×</button></header><div class="dialog-body"><p class="small muted">PDF, PNG, JPEG, UTF-8-Text oder CSV bis 25 MiB. Neue Inhalte werden als eigenständige Dateiversion gespeichert.</p>
    @if(uploadError()){<div class="alert error" role="alert">{{uploadError()}}</div>}
    @if(staged();as doc){<div class="alert information">Wählen Sie erneut die ursprüngliche Datei <strong>{{doc.name}}</strong> ({{size(doc.size)}}). Inhalt und Prüfsumme müssen zum begonnenen Upload passen.</div>}
    <div class="form-field"><label for="upload-file">Datei auswählen *</label><input #fileInput id="upload-file" type="file" accept=".pdf,.png,.jpg,.jpeg,.txt,.csv" (change)="choose($event)" [disabled]="busy()"></div>
    @if(!staged()){<div class="form-field workflow-field"><label for="upload-project">Projekt{{auth.tenant()?.role==='Worker'?' *':''}}</label><input name="projectSearch" aria-label="Projekt für Dokument suchen" placeholder="Projekte durchsuchen" (input)="loadProjects($any($event.target).value)"><select id="upload-project" name="projectId" [(ngModel)]="projectId" [required]="auth.tenant()?.role==='Worker'"><option value="">Allgemeine Betriebsunterlage</option>@for(project of projects();track project.id){<option [value]="project.id">{{project['name']}}</option>}</select></div>}
    @if(progress()){<p role="status" class="small">{{progress()}}</p>}
  </div><footer class="dialog-footer"><button type="button" class="button secondary" (click)="close()" [disabled]="busy()">Schließen</button><button class="button primary" [disabled]="busy()||!file()||uploadForm.invalid">{{busy()?'Wird hochgeladen …':'Datei speichern'}}</button></footer></form></dialog>
`})
export class DocumentsComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);
  readonly page=signal<Page<DocumentEntry>>({items:[],total:0,page:1,pageSize:25});readonly projects=signal<Entity[]>([]);
  readonly busy=signal(false);readonly loading=signal(false);readonly error=signal('');readonly notice=signal('');readonly uploadError=signal('');readonly progress=signal('');
  readonly file=signal<File|null>(null);readonly staged=signal<DocumentEntry|null>(null);
  @ViewChild('uploadDialog')uploadDialog?:ElementRef<HTMLDialogElement>;@ViewChild('fileInput')fileInput?:ElementRef<HTMLInputElement>;
  searchText='';pageNumber=1;projectId='';private filterProject='';private selectedId='';private scope='';private stageKey=crypto.randomUUID();private uploadKey=crypto.randomUUID();private loadVersion=0;private projectVersion=0;
  constructor(){this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(params=>{this.filterProject=params.get('projectId')||'';this.selectedId=params.get('selected')||'';void this.load();});effect(()=>{const scope=this.auth.scope();untracked(()=>{if(scope!==this.scope){this.scope=scope;this.staged.set(null);this.file.set(null);this.uploadDialog?.nativeElement.close();this.notice.set('');this.error.set('');void this.load();void this.loadProjects();}});});}
  state(state:string):string{return ({Ready:'Geprüft & verfügbar',Staged:'Upload unvollständig',Archived:'Archiviert'}as Record<string,string>)[state]||state;}
  date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium'}).format(new Date(value));}
  size(bytes:number):string{return bytes>=1024*1024?`${new Intl.NumberFormat('de-DE',{maximumFractionDigits:2}).format(bytes/(1024*1024))} MiB`:`${new Intl.NumberFormat('de-DE',{maximumFractionDigits:1}).format(bytes/1024)} KiB`;}
  projectName(id?:string):string{return id?String(this.projects().find(x=>x.id===id)?.['name']||'Projektunterlage'):'Betriebsunterlage';}
  canArchive():boolean{return this.auth.can('documents.write')&&['ManagingDirector','Office'].includes(this.auth.tenant()?.role||'');}
  canShare(doc:DocumentEntry):boolean{return doc.state==='Ready'&&!!doc.projectId&&this.auth.hasModule('portal')&&this.auth.can('portal-shares.write');}
  totalPages():number{return Math.ceil(this.page().total/this.page().pageSize);}
  search():void{this.pageNumber=1;void this.load();}paginate(delta:number):void{this.pageNumber+=delta;void this.load();}
  async load():Promise<void>{if(!this.auth.scope()||!this.auth.can('documents.read'))return;const scope=this.auth.scope(),version=++this.loadVersion;this.loading.set(true);this.error.set('');try{const result=await this.api.get<Page<DocumentEntry>>(`documents?${new URLSearchParams({page:String(this.pageNumber),pageSize:'25',search:this.searchText,...(this.filterProject?{projectId:this.filterProject}:{})})}`);if(scope===this.auth.scope()&&version===this.loadVersion){this.page.set(result);if(this.selectedId){const doc=await this.api.get<DocumentEntry>(`documents/${encodeURIComponent(this.selectedId)}`);if(scope===this.auth.scope()&&version===this.loadVersion&&!result.items.some(x=>x.id===doc.id))this.page.update(page=>({...page,items:[doc,...page.items]}));this.selectedId='';}}}catch(error){if(scope===this.auth.scope())this.error.set(errorMessage(error));}finally{if(version===this.loadVersion)this.loading.set(false);}}
  async loadProjects(search=''):Promise<void>{const scope=this.auth.scope(),version=++this.projectVersion;try{const result=await this.api.get<Page>(`projects?${new URLSearchParams({search,pageSize:'100'})}`);if(scope===this.auth.scope()&&version===this.projectVersion)this.projects.set(result.items);}catch(error){if(scope===this.auth.scope())this.uploadError.set(errorMessage(error));}}
  openUpload():void{this.staged.set(null);this.prepareUpload();this.projectId=this.filterProject;}
  resume(doc:DocumentEntry):void{this.staged.set(doc);this.prepareUpload();}
  private prepareUpload():void{this.file.set(null);this.stageKey=crypto.randomUUID();this.uploadKey=crypto.randomUUID();this.uploadError.set('');this.progress.set('');if(this.fileInput)this.fileInput.nativeElement.value='';this.uploadDialog?.nativeElement.showModal();}
  choose(event:Event):void{const file=(event.target as HTMLInputElement).files?.[0]||null;this.file.set(file);this.uploadError.set('');if(file&&(file.size===0||file.size>maximumBytes)){this.uploadError.set('Die Datei muss zwischen einem Byte und 25 MiB groß sein.');this.file.set(null);}}
  cancel(event:Event):void{if(this.busy())event.preventDefault();}
  close():void{if(!this.busy())this.uploadDialog?.nativeElement.close();}
  async upload():Promise<void>{
    const file=this.file();if(!file||this.busy())return;const scope=this.auth.scope();this.busy.set(true);this.uploadError.set('');
    try{const type=fileTypes[file.name.split('.').pop()!.toLowerCase()];if(!type)throw new Error('Dieses Dateiformat wird nicht unterstützt.');
      this.progress.set('Datei wird geprüft …');const bytes=await file.arrayBuffer();const sha256=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),value=>value.toString(16).padStart(2,'0')).join('');
      let staged=this.staged();if(staged&&(staged.name!==file.name||staged.sha256!==sha256||staged.size!==file.size||staged.contentType!==type))throw new Error('Die gewählte Datei entspricht nicht dem begonnenen Upload. Wählen Sie die ursprüngliche Datei.');
      if(scope!==this.auth.scope())throw new Error('Der Betrieb wurde gewechselt. Öffnen Sie den Upload erneut.');
      if(!staged){this.progress.set('Dokument wird angelegt …');staged=await this.api.command<DocumentEntry>('documents',{name:file.name,contentType:type,size:file.size,sha256,projectId:this.projectId||null},undefined,this.stageKey);this.staged.set(staged);}
      if(scope!==this.auth.scope())throw new Error('Der Betrieb wurde gewechselt. Öffnen Sie den Upload erneut.');
      this.progress.set('Datei wird übertragen und im Speicher geprüft …');await this.api.upload<DocumentEntry>(`documents/${staged.id}/content`,new Blob([bytes],{type}),type,staged.revision,this.uploadKey);
      if(scope===this.auth.scope()){this.uploadDialog?.nativeElement.close();this.notice.set('Datei vollständig übertragen und geprüft.');this.file.set(null);this.staged.set(null);await this.load();}
    }catch(error){this.uploadError.set(errorMessage(error));}finally{this.busy.set(false);this.progress.set('');}
  }
  async download(doc:DocumentEntry):Promise<void>{this.busy.set(true);this.error.set('');try{const blob=await this.api.download(`documents/${doc.id}/download`);const sha=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await blob.arrayBuffer())),value=>value.toString(16).padStart(2,'0')).join('');if(sha!==doc.sha256)throw new Error('Die heruntergeladene Datei stimmt nicht mit der gespeicherten Prüfsumme überein.');const url=URL.createObjectURL(blob);const link=document.createElement('a');link.href=url;link.download=doc.name;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}catch(error){this.error.set(errorMessage(error));}finally{this.busy.set(false);}}
  async archive(doc:DocumentEntry):Promise<void>{if(!confirm(`„${doc.name}“ archivieren? Die gespeicherte Version bleibt erhalten.`))return;this.busy.set(true);try{await this.api.command(`documents/${doc.id}/archive`,{},doc.revision);this.notice.set('Dokument archiviert.');await this.load();}catch(error){this.error.set(errorMessage(error));}finally{this.busy.set(false);}}
}
