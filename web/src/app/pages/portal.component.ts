import {Component,ElementRef,ViewChild,effect,inject,signal,untracked} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {ActivatedRoute,Router,RouterLink} from '@angular/router';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {ApiService,Entity,Page} from '../core/api.service';
import {AuthService} from '../core/auth.service';
import {ApiError,errorMessage} from '../core/transport';
import {PortalDetail,PortalDocument,PortalFeedback,PortalQuote,PortalRecipient,PortalShare,portalQuotePreview} from '../core/portal.models';

@Component({imports:[FormsModule,RouterLink],templateUrl:'./portal.component.html'})
export class PortalComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);private readonly router=inject(Router);
  readonly page=signal<Page<PortalShare>>({items:[],total:0,page:1,pageSize:25});readonly selected=signal<PortalDetail|null>(null);
  readonly recipients=signal<PortalRecipient[]>([]);readonly sources=signal<Entity[]>([]);readonly source=signal<Entity|null>(null);readonly grantQuote=signal<PortalQuote|null>(null);
  readonly loading=signal(false);readonly detailLoading=signal(false);readonly busy=signal(false);readonly error=signal('');readonly detailNotice=signal('');readonly notice=signal('');readonly formError=signal('');
  @ViewChild('grantDialog')grantDialog?:ElementRef<HTMLDialogElement>;@ViewChild('acceptDialog')acceptDialog?:ElementRef<HTMLDialogElement>;
  searchText='';pageNumber=1;message='';recipientUserId='';sourceKind:'quotes'|'documents'='quotes';sourceId='';grantTitle='';expiresAt='';allowAcceptance=false;consent=false;
  private selectedId='';private newRequested=false;private incomingSourceId='';private incomingKind:'quotes'|'documents'='quotes';
  private loadVersion=0;private detailVersion=0;private sourceVersion=0;private optionVersion=0;private sourceScope='';private acceptance?:PortalDetail;

  constructor(){
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(query=>{
      this.selectedId=query.get('selected')||'';this.newRequested=query.get('new')==='1';this.incomingSourceId=query.get('sourceId')||'';this.incomingKind=query.get('kind')==='documents'?'documents':'quotes';void this.load();
    });
    let previousScope='',previousAccess='';
    effect(()=>{
      const scope=this.auth.scope(),tenant=this.auth.tenant(),access=JSON.stringify([scope,tenant?.role,tenant?.modules,tenant?.capabilities]);
      if(access===previousAccess)return;const changedScope=!!previousScope&&scope!==previousScope;previousScope=scope;previousAccess=access;
      untracked(()=>{
        this.loadVersion++;this.detailVersion++;this.sourceVersion++;this.optionVersion++;this.selected.set(null);this.page.set({items:[],total:0,page:1,pageSize:25});this.recipients.set([]);this.sources.set([]);this.source.set(null);this.grantQuote.set(null);this.message='';this.error.set('');this.notice.set('');this.detailNotice.set('');this.formError.set('');this.busy.set(false);this.grantDialog?.nativeElement.close();this.acceptDialog?.nativeElement.close();this.acceptance=undefined;
        if(changedScope){this.selectedId='';this.newRequested=false;this.incomingSourceId='';}
        void this.load();if(this.canManage())void this.loadRecipients();
      });
    });
  }
  partner():boolean{return this.auth.tenant()?.role==='Partner';}
  allowed():boolean{return this.auth.hasModule('portal')&&this.auth.can(this.partner()?'portal-access.read':'portal-shares.read');}
  canManage():boolean{return this.allowed()&&['ManagingDirector','Office'].includes(this.auth.tenant()?.role||'')&&this.auth.can('portal-shares.write');}
  canRespond():boolean{const detail=this.selected();return !!detail&&this.active(detail.share)&&this.auth.can(this.partner()?'portal-access.write':'portal-shares.write');}
  canAccept(detail:PortalDetail):boolean{return this.partner()&&this.canRespond()&&detail.share.allowAcceptance&&detail.quote?.status==='Issued'&&detail.quote.issuedDigest===detail.share.sourceDigest;}
  active(share:PortalShare):boolean{return !share.revokedAt&&Date.parse(share.expiresAt)>Date.now();}
  status(share:PortalShare):string{return share.revokedAt?'Widerrufen':this.active(share)?'Freigegeben':'Abgelaufen';}
  sourceLabel(kind:string):string{return kind==='quotes'?'Angebot':'Dokument';}
  name(entity:Entity):string{return String(entity['name']||entity['title']||entity['number']||entity.id);}
  recipientName(id?:string):string{const recipient=this.recipients().find(person=>person.userId===id);return recipient?`${recipient.displayName} · ${recipient.email}`:'Persönlicher Portalzugang';}
  money(value:unknown):string{return new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR'}).format(Number(value));}
  quantity(value:unknown):string{return new Intl.NumberFormat('de-DE',{maximumFractionDigits:6}).format(Number(value));}
  date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short'}).format(new Date(value));}
  dateOnly(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium'}).format(new Date(value+'T12:00:00'));}
  size(value:number):string{return value>=1048576?`${(value/1048576).toLocaleString('de-DE',{maximumFractionDigits:1})} MiB`:`${(value/1024).toLocaleString('de-DE',{maximumFractionDigits:1})} KiB`;}
  localDate(value:Date):string{return new Date(value.getTime()-value.getTimezoneOffset()*60000).toISOString().slice(0,16);}
  minExpiry():string{return this.localDate(new Date());}maxExpiry():string{return this.localDate(new Date(Date.now()+90*86400000));}
  totalPages():number{return Math.ceil(this.page().total/this.page().pageSize);}
  feedbackPages():number{const feedback=this.selected()?.feedback;return feedback?Math.ceil(feedback.total/feedback.pageSize):0;}
  feedbackItems():PortalFeedback[]{return [...(this.selected()?.feedback.items||[])].reverse();}
  search():void{this.pageNumber=1;this.selectedId='';this.selected.set(null);void this.load();}
  paginate(delta:number):void{this.pageNumber+=delta;this.selectedId='';this.selected.set(null);void this.load();}
  private describeError(error:unknown):string{if(error instanceof ApiError){if(error.problem.code==='not_found'||error.status===404)return 'Diese Freigabe ist nicht mehr verfügbar. Sie wurde möglicherweise beendet oder die Unterlage ist nicht mehr freigegeben.';if(error.problem.code==='module_disabled')return 'Das Kundenportal ist für diesen Betrieb derzeit deaktiviert.';}return errorMessage(error);}
  async load():Promise<void>{
    if(!this.auth.scope()||!this.allowed())return;const scope=this.auth.scope(),version=++this.loadVersion;this.loading.set(true);this.error.set('');
    try{
      const query=new URLSearchParams({page:String(this.pageNumber),pageSize:'25'});if(!this.partner()&&this.searchText)query.set('search',this.searchText);
      const result=await this.api.get<Page<PortalShare>>(`${this.partner()?'portal/shares':'portal-shares'}?${query}`);
      if(scope!==this.auth.scope()||version!==this.loadVersion)return;this.page.set(result);
      if(this.selectedId)void this.viewId(this.selectedId);
      if(this.newRequested&&this.canManage()){this.newRequested=false;void this.prepareGrant(this.incomingKind,this.incomingSourceId);}
    }catch(error){if(scope===this.auth.scope()&&version===this.loadVersion)this.error.set(this.describeError(error));}
    finally{if(scope===this.auth.scope()&&version===this.loadVersion)this.loading.set(false);}
  }
  async viewId(id:string):Promise<void>{
    const scope=this.auth.scope(),version=++this.detailVersion;this.selectedId=id;this.selected.set(null);this.detailLoading.set(true);this.detailNotice.set('');this.message='';this.error.set('');
    try{
      let detail:PortalDetail;
      if(this.partner())detail=await this.api.get<PortalDetail>(`portal/shares/${id}`);
      else{
        const share=await this.api.get<PortalShare>(`portal-shares/${id}`);
        const results=await Promise.allSettled([this.api.get<Entity>(`${share.sourceKind}/${share.sourceId}`),this.api.get<Page<PortalFeedback>>(`portal/grants/${id}/feedback?page=1&pageSize=30`)]);
        let quote:PortalQuote|null=null,document:PortalDocument|null=null;
        if(results[0].status==='fulfilled'){if(share.sourceKind==='quotes')quote=portalQuotePreview(results[0].value);else document=results[0].value as unknown as PortalDocument;}
        else if(scope===this.auth.scope()&&version===this.detailVersion)this.detailNotice.set('Die Quellunterlage ist derzeit nicht verfügbar. Freigabe und Rückmeldungen bleiben in dieser Ansicht nachvollziehbar.');
        if(results[1].status==='rejected')throw results[1].reason;
        detail={share,quote,document,feedback:results[1].value};
      }
      if(scope!==this.auth.scope()||version!==this.detailVersion)return;this.selected.set(detail);this.focusDetail();
    }catch(error){if(scope===this.auth.scope()&&version===this.detailVersion){this.selectedId='';this.error.set(this.describeError(error));}}
    finally{if(scope===this.auth.scope()&&version===this.detailVersion)this.detailLoading.set(false);}
  }
  closeDetails():void{this.detailVersion++;this.selectedId='';this.selected.set(null);this.message='';void this.router.navigate([],{relativeTo:this.route,queryParams:{selected:null},queryParamsHandling:'merge',replaceUrl:true});}
  private focusDetail():void{setTimeout(()=>{const heading=document.getElementById('portal-detail-title');heading?.focus({preventScroll:true});heading?.scrollIntoView({block:'start',behavior:'smooth'});});}
  async loadFeedback(page:number):Promise<void>{
    const detail=this.selected();if(!detail)return;const scope=this.auth.scope(),shareId=detail.share.id;
    try{const result=await this.api.get<Page<PortalFeedback>>(`portal/${this.partner()?'shares':'grants'}/${shareId}/feedback?${new URLSearchParams({page:String(page),pageSize:String(detail.feedback.pageSize)})}`);if(scope===this.auth.scope()&&this.selected()?.share.id===shareId)this.selected.update(current=>current?{...current,feedback:result}:current);}
    catch(error){if(scope===this.auth.scope())this.error.set(this.describeError(error));}
  }
  async sendFeedback():Promise<void>{
    const detail=this.selected();if(!detail||!this.canRespond()||!this.message.trim()||this.busy())return;const scope=this.auth.scope();this.busy.set(true);this.error.set('');
    try{await this.api.command(`portal/${this.partner()?'shares':'grants'}/${detail.share.id}/${this.partner()?'feedback':'reply'}`,{message:this.message.trim()},detail.share.revision);if(scope!==this.auth.scope()||this.selected()?.share.id!==detail.share.id)return;this.message='';this.notice.set(this.partner()?'Ihre Rückmeldung ist im Portal gespeichert.':'Ihre Antwort ist für den freigegebenen Portalzugang gespeichert.');await this.loadFeedback(1);}
    catch(error){if(scope===this.auth.scope())this.error.set(this.describeError(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
  prepareAcceptance(detail:PortalDetail):void{if(!this.canAccept(detail))return;this.acceptance=structuredClone(detail);this.sourceScope=this.auth.scope();this.consent=false;this.formError.set('');this.acceptDialog?.nativeElement.showModal();}
  acceptanceQuote():PortalQuote|null{return this.acceptance?.quote||null;}
  async accept():Promise<void>{
    const detail=this.acceptance;if(!detail?.quote||!this.consent||this.busy()||this.sourceScope!==this.auth.scope())return;const scope=this.auth.scope();this.busy.set(true);this.formError.set('');
    try{const result=await this.api.command<{shareId:string;quote:PortalQuote}>(`portal/shares/${detail.share.id}/accept`,{digest:detail.quote.issuedDigest},detail.quote.revision);if(scope!==this.auth.scope())return;this.selected.update(current=>current?.share.id===result.shareId?{...current,quote:result.quote}:current);this.acceptDialog?.nativeElement.close();this.acceptance=undefined;this.notice.set('Ihre verbindliche Annahme dieser Angebotsversion ist dokumentiert.');this.focusDetail();}
    catch(error){if(scope===this.auth.scope())this.formError.set(this.describeError(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
  async download():Promise<void>{
    const detail=this.selected();if(!detail?.document||this.busy())return;const scope=this.auth.scope();this.busy.set(true);this.error.set('');
    try{const blob=await this.api.download(this.partner()?`portal/shares/${detail.share.id}/download`:`documents/${detail.document.id}/download`);const digest=await crypto.subtle.digest('SHA-256',await blob.arrayBuffer());const sha=Array.from(new Uint8Array(digest),byte=>byte.toString(16).padStart(2,'0')).join('');if(sha!==detail.document.sha256.toLowerCase())throw new Error('Die heruntergeladene Datei stimmt nicht mit der freigegebenen Version überein. Der Download wurde verworfen.');if(scope!==this.auth.scope())return;const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=detail.document.name;link.click();setTimeout(()=>URL.revokeObjectURL(url),30000);}
    catch(error){if(scope===this.auth.scope())this.error.set(this.describeError(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
  async loadRecipients():Promise<void>{const scope=this.auth.scope();try{const recipients=await this.api.get<PortalRecipient[]>('portal/recipients');if(scope===this.auth.scope())this.recipients.set(recipients);}catch(error){if(scope===this.auth.scope())this.error.set(this.describeError(error));}}
  async prepareGrant(kind:'quotes'|'documents'='quotes',id=''):Promise<void>{
    if(!this.canManage())return;this.sourceScope=this.auth.scope();this.sourceKind=kind;this.sourceId=id;this.recipientUserId='';this.grantTitle='';this.expiresAt=this.localDate(new Date(Date.now()+14*86400000));this.allowAcceptance=false;this.formError.set('');this.source.set(null);this.grantQuote.set(null);this.sources.set([]);this.grantDialog?.nativeElement.showModal();void this.loadSources();if(id)await this.selectSource(id);
  }
  changeSourceKind():void{this.sourceId='';this.source.set(null);this.grantQuote.set(null);this.allowAcceptance=false;void this.loadSources();}
  async loadSources(search=''):Promise<void>{
    if(!this.canManage())return;const scope=this.auth.scope(),kind=this.sourceKind,version=++this.optionVersion;
    try{const page=await this.api.get<Page>(`${kind}?${new URLSearchParams({search,pageSize:'100'})}`);if(scope!==this.auth.scope()||kind!==this.sourceKind||version!==this.optionVersion)return;this.sources.set(page.items.filter(entity=>kind==='quotes'?['Issued','Accepted'].includes(String(entity['status']))&&!!entity['issuedDigest']:entity['state']==='Ready'&&!!entity['projectId']));const selected=this.source();if(selected&&!this.sources().some(entity=>entity.id===selected.id))this.sources.update(items=>[selected,...items]);}
    catch(error){if(scope===this.auth.scope())this.formError.set(this.describeError(error));}
  }
  async selectSource(id:string):Promise<void>{
    this.source.set(null);this.grantQuote.set(null);this.allowAcceptance=false;if(!id)return;const scope=this.auth.scope(),kind=this.sourceKind,version=++this.sourceVersion;
    try{const entity=await this.api.get<Entity>(`${kind}/${id}`);if(scope!==this.auth.scope()||id!==this.sourceId||kind!==this.sourceKind||version!==this.sourceVersion)return;if(kind==='quotes')this.grantQuote.set(portalQuotePreview(entity));else if(entity['state']!=='Ready'||!entity['projectId'])throw new Error('Wählen Sie eine geprüfte Datei mit Projektzuordnung.');this.source.set(entity);if(!this.grantTitle)this.grantTitle=this.name(entity);if(!this.sources().some(option=>option.id===entity.id))this.sources.update(options=>[entity,...options]);}
    catch(error){if(scope===this.auth.scope())this.formError.set(this.describeError(error));}
  }
  async grant():Promise<void>{
    if(!this.canManage()||this.busy()||!this.source()||this.sourceScope!==this.auth.scope())return;const scope=this.auth.scope();this.busy.set(true);this.formError.set('');
    try{const share=await this.api.command<PortalShare>('portal/grants',{recipientUserId:this.recipientUserId,sourceKind:this.sourceKind,sourceId:this.sourceId,expiresAt:new Date(this.expiresAt).toISOString(),title:this.grantTitle.trim(),allowAcceptance:this.sourceKind==='quotes'&&this.allowAcceptance});if(scope!==this.auth.scope())return;this.grantDialog?.nativeElement.close();this.notice.set('Die ausgewählte Person kann diese Unterlage bis zum angegebenen Zeitpunkt im Portal öffnen.');this.selectedId=share.id;this.selected.set(null);await this.load();}
    catch(error){if(scope===this.auth.scope())this.formError.set(this.describeError(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
  async revoke(share:PortalShare):Promise<void>{
    if(!this.canManage()||this.busy()||!this.active(share)||!confirm(`Zugriff auf „${share.title}“ für ${this.recipientName(share.recipientUserId)} jetzt beenden?`))return;const scope=this.auth.scope();this.busy.set(true);this.error.set('');
    try{const result=await this.api.command<PortalShare>(`portal/grants/${share.id}/revoke`,{},share.revision);if(scope!==this.auth.scope())return;this.selected.update(detail=>detail?.share.id===share.id?{...detail,share:result}:detail);this.notice.set('Der Portalzugriff auf diese Unterlage ist beendet.');await this.load();}
    catch(error){if(scope===this.auth.scope())this.error.set(this.describeError(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
  cancel(event:Event):void{if(this.busy())event.preventDefault();}
  closeGrant():void{if(!this.busy())this.grantDialog?.nativeElement.close();}
  closeAcceptance():void{if(!this.busy()){this.acceptDialog?.nativeElement.close();this.acceptance=undefined;}}
}
