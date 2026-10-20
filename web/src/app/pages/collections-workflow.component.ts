import {Component,DestroyRef,computed,effect,inject,signal,untracked} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {ActivatedRoute,Router,RouterLink} from '@angular/router';
import {toSignal} from '@angular/core/rxjs-interop';
import {ApiService,Entity,Page} from '../core/api.service';
import {AuthService} from '../core/auth.service';
import {ApiError,errorMessage} from '../core/transport';
import {decimalInput} from '../core/decimal-input';
import {localTimeChoices,resolveLocalTime} from '../core/local-time';
import {
 CollectionAssignee,CollectionBalanceResponse,CollectionCase,CollectionPreview,CollectionStep,CollectionStepInput,
 CollectionWorklist,CollectionStage,ContactChannel,ClosureKind,CollectionStatus,
 checkCollectionCase,checkCollectionLedger,checkCollectionPage,checkCollectionPreview,collectionId,collectionMoney,
 collectionDate,collectionState,collectionStageName,collectionContactName,collectionStages,collectionStatuses,
 collectionChannels,collectionEventNames,
} from '../core/collections.models';

interface Context{generation:number;access:string;tenantId:string}
interface PendingCommand{path:string;body:unknown;revision?:number;key:string;label:string;caseId?:string}
interface InvoiceChoice extends Entity{tenantId:string;number:string;buyerName:string;dueDate:string;status:string;kind:string;issuedDigest:string}
type Panel='reminder'|'contact'|'dispute'|'resolve'|'review'|'close'|'reopen';

@Component({
 selector:'app-collections-workflow',imports:[FormsModule,RouterLink],
 templateUrl:'./collections-workflow.component.html',
 styles:[`
  :host{display:block}.collections{display:grid;gap:1.25rem}.collections h1,.collections h2,.collections h3{margin:0 0 .6rem}
  .collection-columns{display:grid;grid-template-columns:minmax(16rem,1fr) minmax(0,2fr);gap:1.25rem;align-items:start}
  .collection-card{border:1px solid var(--line,#d9e0e8);border-radius:1rem;padding:1.25rem;background:var(--surface,#fff);min-width:0}
  .collection-stack{display:grid;gap:1rem}.collection-fields{display:grid;gap:.8rem;grid-template-columns:repeat(2,minmax(0,1fr))}
  .collection-fields .wide{grid-column:1/-1}.collection-fields label{display:grid;gap:.35rem}
  .collection-fields input,.collection-fields select,.collection-fields textarea{min-width:0;width:100%;box-sizing:border-box}
  .collection-actions,.collection-pager{display:flex;flex-wrap:wrap;align-items:center;gap:.6rem;margin:.8rem 0}
  .collections button:not(.collection-list-button){display:inline-flex;align-items:center;justify-content:center;min-height:44px;max-width:100%;border:1px solid #cdd7d4;border-radius:7px;padding:10px 14px;background:#fff;color:#314950;font-size:.84rem;font-weight:650;line-height:1.4;white-space:normal}
  .collections button:not(.collection-list-button):hover:not(:disabled){background:#f1f5f2}.collections button[aria-pressed=true]{background:var(--accent-light,#fff0e5);border-color:var(--accent,#bb5d21)}
  .collections button[type=submit],.collections button.collection-primary{background:#ad531c;border-color:#ad531c;color:white}.collections button[type=submit]:hover:not(:disabled),.collections button.collection-primary:hover:not(:disabled){background:#8e4214}
  .collection-pager{justify-content:space-between}.collection-muted{color:var(--muted,#596777);font-size:.93rem}
  .collection-error,.collection-notice,.collection-warning{padding:.8rem 1rem;border-radius:.6rem;overflow-wrap:anywhere}
  .collection-error{background:#fff1f0;color:#8c2424}.collection-notice{background:#edf8f0;color:#225a36}.collection-warning{background:#fff8e7;color:#6e5011}
  .collection-summary{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:.75rem;margin:.8rem 0}
  .collection-summary>div{padding:.8rem;border:1px solid var(--line,#d9e0e8);border-radius:.6rem}.collection-summary strong{display:block;font-size:1.2rem}
  .collection-definition{display:grid;grid-template-columns:minmax(9rem,1fr) minmax(0,2fr);gap:.45rem 1rem}.collection-definition dt{font-weight:600}.collection-definition dd{margin:0;overflow-wrap:anywhere}
  .collection-list{list-style:none;margin:0;padding:0;display:grid;gap:.6rem}.collection-list>li{border-top:1px solid var(--line,#d9e0e8);padding:.8rem 0}
  .collection-list-button{display:block;text-align:left;width:100%;padding:.8rem;border:1px solid var(--line,#d9e0e8);border-radius:.65rem;background:transparent;white-space:normal}
  .collection-list-button[aria-current=true]{border-color:var(--accent,#145d72);box-shadow:inset 3px 0 var(--accent,#145d72)}
  .collection-list-button span,.collection-list-button strong{display:block;overflow-wrap:anywhere}.collection-preview{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.55}
  .collection-checkbox{display:flex!important;align-items:flex-start;gap:.6rem!important}.collection-checkbox input{width:auto!important;margin-top:.25rem}
  .collection-code{font-family:ui-monospace,monospace;font-size:.82rem;overflow-wrap:anywhere}details>summary{cursor:pointer;font-weight:600;margin:.5rem 0}
  .collection-history-note{white-space:pre-wrap;overflow-wrap:anywhere}.collection-table{width:100%;border-collapse:collapse}.collection-table th,.collection-table td{text-align:left;vertical-align:top;padding:.45rem;border-bottom:1px solid var(--line,#d9e0e8);overflow-wrap:anywhere}
  @media(max-width:1200px){.collection-columns{grid-template-columns:1fr}.collection-summary{grid-template-columns:repeat(3,minmax(0,1fr))}}
  @media(max-width:560px){.collection-fields,.collection-summary{grid-template-columns:1fr}.collection-definition{grid-template-columns:1fr}.collection-definition dd{margin-bottom:.5rem}.collection-card{padding:.9rem}.collection-table{font-size:.85rem}}
 `],
})
export class CollectionsWorkflowComponent{
 readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);private readonly router=inject(Router);
 private readonly query=toSignal(this.route.queryParamMap,{initialValue:this.route.snapshot.queryParamMap});
 readonly allowed=computed(()=>['ManagingDirector','Office','Accounting'].includes(this.auth.tenant()?.role||'')&&this.auth.can('collection-cases.read')&&!this.auth.recoveryHold());
 readonly writable=computed(()=>this.allowed()&&this.auth.can('collection-cases.write')&&this.auth.hasModule('Finance')&&!this.auth.offlineSession());
 readonly worklist=signal<CollectionWorklist|null>(null);readonly invoices=signal<Page<InvoiceChoice>|null>(null);readonly assignees=signal<CollectionAssignee[]>([]);
 readonly selected=signal<CollectionCase|null>(null);readonly invoice=signal<InvoiceChoice|null>(null);readonly balance=signal<CollectionBalanceResponse|null>(null);readonly preview=signal<CollectionPreview|null>(null);
 readonly loading=signal(false);readonly listLoading=signal(false);readonly invoiceLoading=signal(false);readonly busy=signal(false);readonly refreshRequired=signal(false);
 readonly error=signal('');readonly listError=signal('');readonly notice=signal('');readonly pending=signal<PendingCommand|null>(null);readonly panel=signal<Panel>('reminder');
 readonly page=signal(1);readonly invoicePage=signal(1);readonly sourcePage=signal(1);readonly eventPage=signal(1);readonly stepPage=signal(1);
 readonly stages=collectionStages;readonly statuses=collectionStatuses;readonly channels=collectionChannels;
 readonly money=collectionMoney;readonly date=collectionDate;readonly state=collectionState;readonly stage=collectionStageName;readonly channel=collectionContactName;
 readonly zone=Intl.DateTimeFormat().resolvedOptions().timeZone;
 search='';statusFilter:CollectionStatus|''='';invoiceSearch='';invoiceId='';showInvoicePicker=false;
 openForm={assignedToUserId:'',nextReviewOn:'',reason:''};stepForm:{stage:CollectionStage|'';respondBy:string;reason:string;additionalMessage:string}={stage:'',respondBy:'',reason:'',additionalMessage:''};previewAcknowledged=false;
 contactForm:{stepId:string;channel:ContactChannel|'';localTime:string;offset:string;evidenceReference:string;acknowledged:boolean}={stepId:'',channel:'',localTime:'',offset:'',evidenceReference:'',acknowledged:false};
 withdrawReason='';disputeForm={amount:'',reason:'',evidenceReference:'',nextReviewOn:''};resolveForm={reason:'',evidenceReference:'',nextReviewOn:''};reviewForm={assignedToUserId:'',nextReviewOn:'',reason:''};
 closeForm:{kind:ClosureKind|'';reason:string;evidenceReference:string;acknowledged:boolean}={kind:'',reason:'',evidenceReference:'',acknowledged:false};reopenForm={assignedToUserId:'',nextReviewOn:'',reason:'',evidenceReference:''};
 private generation=0;private listRequest=0;private invoiceRequest=0;
 constructor(){
  inject(DestroyRef).onDestroy(()=>{this.generation++;});
  effect(()=>{this.auth.accessSignature();const query=this.query();untracked(()=>{
   this.generation++;this.reset();if(!this.allowed())return;
   const caseId=query.get('caseId'),selected=query.get('selected'),invoiceId=query.get('invoiceId');
   if(['caseId','selected','invoiceId'].some(key=>query.getAll(key).length>1)||[caseId,selected,invoiceId].some(id=>id!==null&&!collectionId(id))||(caseId&&selected&&caseId!==selected)||((caseId||selected)&&invoiceId)){
    this.error.set('Bitte genau einen gültigen Rechnungs- oder Fallverweis öffnen. Es wurde kein Beleg abgerufen.');return;
   }
   void this.loadAssignees();void this.loadWorklist(1);
   if(caseId||selected)void this.loadCase((caseId||selected)!);else if(invoiceId)void this.chooseInvoice(invoiceId);
  });});
 }
 private context():Context{return{generation:this.generation,access:this.auth.accessSignature(),tenantId:this.auth.tenant()?.id||''};}
 private current(context:Context):boolean{return context.generation===this.generation&&context.access===this.auth.accessSignature()&&context.tenantId===this.auth.tenant()?.id&&this.allowed();}
 private reset():void{
  this.worklist.set(null);this.invoices.set(null);this.assignees.set([]);this.selected.set(null);this.invoice.set(null);this.balance.set(null);this.preview.set(null);this.pending.set(null);
  this.loading.set(false);this.listLoading.set(false);this.invoiceLoading.set(false);this.busy.set(false);this.refreshRequired.set(false);this.error.set('');this.listError.set('');this.notice.set('');
  this.page.set(1);this.invoicePage.set(1);this.sourcePage.set(1);this.eventPage.set(1);this.stepPage.set(1);this.search='';this.statusFilter='';this.invoiceSearch='';this.invoiceId='';this.showInvoicePicker=false;
  this.resetForms();
 }
 private resetForms():void{
  this.panel.set('reminder');this.openForm={assignedToUserId:'',nextReviewOn:'',reason:''};this.stepForm={stage:'',respondBy:'',reason:'',additionalMessage:''};this.previewAcknowledged=false;
  this.contactForm={stepId:'',channel:'',localTime:'',offset:'',evidenceReference:'',acknowledged:false};this.withdrawReason='';this.disputeForm={amount:'',reason:'',evidenceReference:'',nextReviewOn:''};this.resolveForm={reason:'',evidenceReference:'',nextReviewOn:''};
  this.reviewForm={assignedToUserId:this.selected()?.assignedToUserId||'',nextReviewOn:this.selected()?.nextReviewOn||'',reason:''};
  this.closeForm={kind:'',reason:'',evidenceReference:'',acknowledged:false};this.reopenForm={assignedToUserId:'',nextReviewOn:'',reason:'',evidenceReference:''};
 }
 pages():number{return Math.max(1,Math.ceil((this.worklist()?.total||0)/25));}
 invoicePages():number{return Math.max(1,Math.ceil((this.invoices()?.total||0)/25));}
 sourcePages():number{return Math.max(1,Math.ceil((this.balance()?.ledger.sources.length||0)/25));}
 eventPages():number{return Math.max(1,Math.ceil((this.selected()?.events.length||0)/25));}
 stepPages():number{return Math.max(1,Math.ceil((this.selected()?.steps.length||0)/10));}
 sources(){return this.balance()?.ledger.sources.slice((this.sourcePage()-1)*25,this.sourcePage()*25)||[];}
 events(){return [...(this.selected()?.events||[])].reverse().slice((this.eventPage()-1)*25,this.eventPage()*25);}
 steps(){return [...(this.selected()?.steps||[])].reverse().slice((this.stepPage()-1)*10,this.stepPage()*10);}
 pendingStep():CollectionStep|undefined{return this.selected()?.steps.find(step=>!step.contact&&!step.withdrawal);}
 unresolvedDispute(){return this.selected()?.disputes.find(dispute=>!dispute.resolvedAt);}
 assignedName(id:string):string{return this.assignees().find(value=>value.id===id)?.displayName||'Bisherige Zuordnung nicht mehr verfügbar';}
 eventName(action:string):string{return collectionEventNames[action]||'Dokumentierter Vorgang';}
 instant(value:string):string{const date=new Date(value);return Number.isFinite(date.getTime())?new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short'}).format(date):'Zeitpunkt nicht verfügbar';}
 contactChoices(){return this.contactForm.localTime?localTimeChoices(this.contactForm.localTime):[];}
 contactTimeChanged():void{this.contactForm.offset='';this.contactForm.acknowledged=false;}
 canAct():boolean{return this.writable()&&!this.busy()&&!this.loading()&&!this.pending()&&!this.refreshRequired()&&!!this.selected()&&!!this.balance();}
 private describe(error:unknown):string{
  if(error instanceof ApiError){const code=error.problem.code;const messages:Record<string,string>={
   collection_preview_changed:'Seit der Vorschau haben sich der Fall, die Rechnungsquellen oder das Betriebsdatum geändert. Bitte den aktuellen Stand laden und eine neue Vorschau prüfen.',
   collection_owner_unavailable:'Die bisherige verantwortliche Person hat aktuell keinen passenden Zugang. Bitte den Fall einer verfügbaren Person zuordnen und erneut prüfen.',
   collection_exists:'Für diese Rechnung gibt es bereits einen Fall, auch wenn er geschlossen ist. Bitte den vorhandenen Fall öffnen.',
   collection_source_budget:'Dieser Fall überschreitet den Umfang von 1.000 Finanzquellen. Es wurde keine unvollständige Saldenvorschau erzeugt.',
   collection_source_metadata:'Eine gespeicherte Finanzquelle passt nicht zum erwarteten Belegstand. Bitte die Daten mit der Betriebsverwaltung prüfen.',
   invalid_collection_search:'Bitte mindestens zwei Zeichen für die Suche eingeben.',
  };if(code&&messages[code])return messages[code];if(error.status===403)return 'Der aktuelle Zugang erlaubt diesen Vorgang nicht mehr. Bitte die Berechtigung oder das Finanzmodul mit der Betriebsverwaltung prüfen.';}
  return errorMessage(error);
 }
 private clearForbidden(error:unknown):void{if(error instanceof ApiError&&[401,403,404].includes(error.status)){this.generation++;this.selected.set(null);this.balance.set(null);this.invoice.set(null);this.preview.set(null);this.worklist.set(null);this.invoices.set(null);this.assignees.set([]);this.pending.set(null);this.loading.set(false);this.listLoading.set(false);this.invoiceLoading.set(false);this.busy.set(false);}}
 private text(value:string,label:string,maximum=2000):string{const trimmed=value.trim();if(!trimmed||trimmed.length>maximum)throw new Error(`Bitte ${label} mit höchstens ${maximum} Zeichen angeben.`);return trimmed;}
 private day(value:string,label:string):string{if(!/^\d{4}-\d{2}-\d{2}$/.test(value))throw new Error(`Bitte ${label} ausdrücklich als Datum angeben.`);return value;}
 private owner(value:string):string{if(!collectionId(value)||!this.assignees().some(item=>item.id===value))throw new Error('Bitte eine aktuell verfügbare verantwortliche Person auswählen.');return value;}
 async loadAssignees():Promise<void>{
  if(!this.allowed())return;const context=this.context();
  try{const value=await this.api.get<CollectionAssignee[]>('collection-cases/assignees');if(!this.current(context))return;
   if(!Array.isArray(value)||value.length>1000||value.some(item=>!collectionId(item.id)||typeof item.displayName!=='string'))throw new Error('Die Liste der verfügbaren Personen konnte nicht geprüft werden.');this.assignees.set(value);
  }catch(error){if(this.current(context)){this.clearForbidden(error);this.error.set(this.describe(error));}}
 }
 async loadWorklist(page=1):Promise<void>{
  if(!this.allowed()||this.pending())return;const context=this.context(),request=++this.listRequest;this.listLoading.set(true);this.listError.set('');
  try{const search=this.search.trim();if(search.length===1)throw new Error('Bitte mindestens zwei Zeichen für die Suche eingeben.');
   const query=new URLSearchParams({page:String(page),pageSize:'25',status:this.statusFilter,q:search});const value=await this.api.get<CollectionWorklist>('collection-cases/worklist?'+query);
   if(!this.current(context)||request!==this.listRequest)return;checkCollectionPage(value,page);if(value.schema!=='firstblick.collection-worklist/1.0.0'||value.items.some(item=>!collectionId(item.id)||!collectionId(item.invoiceId)||typeof item.invoiceNumber!=='string'||typeof item.buyerName!=='string'||!collectionStatuses.some(status=>status.value===item.status)))throw new Error('Die Fallübersicht konnte nicht zum angefragten Stand geprüft werden.');
   this.worklist.set(value);this.page.set(page);
  }catch(error){if(this.current(context)&&request===this.listRequest){this.worklist.set(null);this.clearForbidden(error);this.listError.set(this.describe(error));}}finally{if(this.current(context)&&request===this.listRequest)this.listLoading.set(false);}
 }
 chooseCase(id:string):void{if(!collectionId(id)||this.busy()||this.pending())return;void this.router.navigate([],{relativeTo:this.route,queryParams:{caseId:id}});}
 async loadCase(id:string):Promise<void>{
  if(!this.allowed()||!collectionId(id))return;const context=this.context();this.loading.set(true);this.error.set('');this.preview.set(null);this.previewAcknowledged=false;this.balance.set(null);
  try{const [entity,balance]=await Promise.all([this.api.get<CollectionCase>(`collection-cases/${id}`),this.api.get<CollectionBalanceResponse>(`collection-cases/${id}/balance`)]);
   if(!this.current(context))return;checkCollectionCase(entity,context.tenantId,id);this.acceptBalance(balance,entity,context);this.selected.set(entity);this.invoice.set(null);this.refreshRequired.set(false);this.resetForms();
  }catch(error){if(this.current(context)){this.refreshRequired.set(true);this.clearForbidden(error);this.error.set(this.describe(error));}}finally{if(this.current(context))this.loading.set(false);}
 }
 private acceptBalance(value:CollectionBalanceResponse,entity:CollectionCase,context:Context):void{
  if(value.schema!=='firstblick.collection-balance/1.0.0'||value.caseId!==entity.id||value.caseRevision!==entity.revision||value.status!==entity.status)throw new Error('Der Fall wurde während des Einlesens geändert. Bitte den aktuellen Stand erneut laden.');
  checkCollectionLedger(value.ledger,context.tenantId,entity.invoiceId,entity.invoiceDigest);this.balance.set(value);this.sourcePage.set(1);
 }
 refresh():void{if(this.busy())return;this.preview.set(null);this.previewAcknowledged=false;const id=this.selected()?.id||this.query().get('caseId')||this.query().get('selected');if(id&&collectionId(id))void this.loadCase(id);else if(this.invoiceId)void this.chooseInvoice(this.invoiceId);void this.loadAssignees();if(!this.pending())void this.loadWorklist(this.page());}
 async openInvoicePicker():Promise<void>{if(!this.writable()||this.busy()||this.pending())return;this.showInvoicePicker=true;await this.loadInvoices(1);}
 async loadInvoices(page=1):Promise<void>{
  if(!this.allowed())return;const context=this.context(),request=++this.invoiceRequest;this.invoiceLoading.set(true);
  try{const query=new URLSearchParams({status:'Issued',openOnly:'true',search:this.invoiceSearch.trim(),page:String(page),pageSize:'25'});const value=await this.api.get<Page<InvoiceChoice>>('invoices?'+query);
   if(!this.current(context)||request!==this.invoiceRequest)return;checkCollectionPage(value,page);if(value.items.some(item=>!collectionId(item.id)||item.tenantId!==context.tenantId||item.status!=='Issued'||item.kind==='CreditNote'))throw new Error('Die Rechnungsauswahl enthält keinen passenden offenen Belegstand.');this.invoices.set(value);this.invoicePage.set(page);
  }catch(error){if(this.current(context)&&request===this.invoiceRequest){this.invoices.set(null);this.clearForbidden(error);this.error.set(this.describe(error));}}finally{if(this.current(context)&&request===this.invoiceRequest)this.invoiceLoading.set(false);}
 }
 async chooseInvoice(id:string):Promise<void>{
  if(!this.allowed()||this.busy()||this.pending())return;this.generation++;const context=this.context();this.invoiceId=id;this.selected.set(null);this.balance.set(null);this.invoice.set(null);this.preview.set(null);this.resetForms();this.error.set('');this.notice.set('');this.refreshRequired.set(false);
  if(!collectionId(id)){this.error.set('Bitte eine gültige ausgestellte Rechnung auswählen.');return;}this.loading.set(true);this.listLoading.set(false);this.invoiceLoading.set(false);void this.loadWorklist(this.page());void this.loadAssignees();
  try{const [invoice,existing]=await Promise.all([this.api.get<InvoiceChoice>(`invoices/${id}`),this.api.get<CollectionWorklist>(`collection-cases/worklist?${new URLSearchParams({invoiceId:id,page:'1',pageSize:'25'})}`)]);
   if(!this.current(context))return;if(invoice.id!==id||invoice.tenantId!==context.tenantId||invoice.status!=='Issued'||invoice.kind==='CreditNote')throw new Error('Für den gewählten Beleg kann kein neuer Rechnungsfall angelegt werden.');checkCollectionPage(existing,1);
   if(existing.schema!=='firstblick.collection-worklist/1.0.0'||existing.total>1||existing.items.some(item=>item.invoiceId!==id))throw new Error('Der bestehende Rechnungsfall konnte nicht eindeutig bestimmt werden.');
   if(existing.items.length){await this.loadCase(existing.items[0].id);if(this.current(context))this.notice.set('Der bereits bestehende Rechnungsfall wurde geöffnet.');}
   else this.invoice.set(invoice);
   if(this.current(context))void this.loadAssignees();
  }catch(error){if(this.current(context)){this.clearForbidden(error);this.error.set(this.describe(error));}}finally{if(this.current(context))this.loading.set(false);}
 }
 showPanel(panel:Panel):void{if(this.busy()||this.pending())return;this.panel.set(panel);this.preview.set(null);this.previewAcknowledged=false;this.error.set('');this.contactForm.stepId=this.pendingStep()?.id||'';}
 discardPreview():void{if(this.busy()||this.pending())return;this.preview.set(null);this.previewAcknowledged=false;}
 async makePreview():Promise<void>{
  if(!this.canAct()||this.pendingStep()||this.selected()?.status!=='Active')return;const entity=this.selected()!,context=this.context();this.busy.set(true);this.error.set('');this.preview.set(null);this.previewAcknowledged=false;
  try{if(!this.stepForm.stage)throw new Error('Bitte die gewünschte Stufe ausdrücklich auswählen.');
   const input:CollectionStepInput={stage:this.stepForm.stage,respondBy:this.day(this.stepForm.respondBy,'das gewünschte Antwortdatum'),reason:this.text(this.stepForm.reason,'die Begründung'),additionalMessage:this.stepForm.additionalMessage.trim()};if(input.additionalMessage.length>4000)throw new Error('Die ergänzende Nachricht darf höchstens 4.000 Zeichen enthalten.');
   const value=await this.api.command<CollectionPreview>(`collection-cases/${entity.id}/step-preview`,{input},entity.revision);
   if(!this.current(context))return;checkCollectionPreview(value,entity,context.tenantId,input);this.balance.set({schema:'firstblick.collection-balance/1.0.0',caseId:entity.id,caseRevision:entity.revision,status:entity.status,ledger:value.ledger});this.sourcePage.set(1);this.preview.set(value);
  }catch(error){if(this.current(context)){this.clearForbidden(error);if(error instanceof ApiError&&error.status>=400&&error.status<500){this.refreshRequired.set(true);this.balance.set(null);}this.error.set(this.describe(error));}}finally{if(this.current(context))this.busy.set(false);}
 }
 async approve():Promise<void>{const preview=this.preview(),entity=this.selected();if(!preview||!entity||!this.previewAcknowledged||!this.canAct())return;await this.send('approve-step',{input:preview.input,previewDigest:preview.digest},'Der Text wurde intern freigegeben. Es wurde keine Nachricht versandt.');}
 async create():Promise<void>{
  if(!this.writable()||this.busy()||this.pending()||!this.invoice())return;
  try{await this.execute({path:'collection-cases/from-invoice',body:{invoiceId:this.invoice()!.id,assignedToUserId:this.owner(this.openForm.assignedToUserId),nextReviewOn:this.day(this.openForm.nextReviewOn,'den nächsten Prüftermin'),reason:this.text(this.openForm.reason,'den Anlass des Falls')},key:crypto.randomUUID(),label:'Der Rechnungsfall wurde angelegt.'});}catch(error){this.error.set(this.describe(error));}
 }
 async withdraw():Promise<void>{const step=this.pendingStep();if(!step)return;try{await this.send('withdraw-step',{stepId:step.id,reason:this.text(this.withdrawReason,'die Begründung für die Rücknahme')},'Der unkontaktierte interne Entwurf wurde zurückgenommen.');}catch(error){this.error.set(this.describe(error));}}
 async recordContact():Promise<void>{
  const step=this.pendingStep();if(!step||!this.contactForm.acknowledged)return;
  try{if(!this.contactForm.channel)throw new Error('Bitte die tatsächliche Kontaktart auswählen.');const occurredAt=resolveLocalTime(this.contactForm.localTime,this.contactForm.offset||undefined);
   await this.send('record-contact',{stepId:step.id,stepDigest:step.digest,channel:this.contactForm.channel,occurredAt,evidenceReference:this.text(this.contactForm.evidenceReference,'den Beleg oder die Fundstelle des tatsächlichen Kontakts',4000)},'Der manuell belegte Kontakt wurde als Ihre Angabe gespeichert.');
  }catch(error){this.error.set(this.describe(error));}
 }
 async dispute():Promise<void>{try{await this.send('dispute',{amount:decimalInput(this.disputeForm.amount),reason:this.text(this.disputeForm.reason,'den Streitgrund'),evidenceReference:this.text(this.disputeForm.evidenceReference,'den Beleg des Einwands',4000),nextReviewOn:this.day(this.disputeForm.nextReviewOn,'den nächsten Prüftermin')},'Der Streitfall wurde erfasst. Weitere Mahnfreigaben sind bis zur ausdrücklichen Klärung gesperrt.');}catch(error){this.error.set(this.describe(error));}}
 async resolve():Promise<void>{try{await this.send('resolve-dispute',{reason:this.text(this.resolveForm.reason,'die ausdrückliche Klärung'),evidenceReference:this.text(this.resolveForm.evidenceReference,'den Nachweis der Klärung',4000),nextReviewOn:this.day(this.resolveForm.nextReviewOn,'den nächsten Prüftermin')},'Die Klärung wurde dokumentiert. Rechnungen, Gutschriften und Zahlungen wurden dadurch nicht verändert.');}catch(error){this.error.set(this.describe(error));}}
 async review():Promise<void>{try{await this.send('review',{assignedToUserId:this.owner(this.reviewForm.assignedToUserId),nextReviewOn:this.day(this.reviewForm.nextReviewOn,'den nächsten Prüftermin'),reason:this.text(this.reviewForm.reason,'die Begründung der neuen Zuordnung oder Prüfung')},'Verantwortung und Prüftermin wurden dokumentiert.');}catch(error){this.error.set(this.describe(error));}}
 async close():Promise<void>{
  if(!this.closeForm.acknowledged)return;try{if(!this.closeForm.kind)throw new Error('Bitte den Abschlussgrund ausdrücklich auswählen.');await this.send('close',{kind:this.closeForm.kind,reason:this.text(this.closeForm.reason,'die Abschlussbegründung'),evidenceReference:this.closeForm.kind==='ManualHandling'?this.text(this.closeForm.evidenceReference,'den Nachweis der weiteren manuellen Behandlung',4000):this.closeForm.evidenceReference.trim()},'Der Fall wurde geschlossen. Die gespeicherten finanziellen Ansprüche bleiben erhalten.');}catch(error){this.error.set(this.describe(error));}
 }
 async reopen():Promise<void>{try{await this.send('reopen',{assignedToUserId:this.owner(this.reopenForm.assignedToUserId),nextReviewOn:this.day(this.reopenForm.nextReviewOn,'den nächsten Prüftermin'),reason:this.text(this.reopenForm.reason,'den Wiedereröffnungsgrund'),evidenceReference:this.text(this.reopenForm.evidenceReference,'den Nachweis der Wiedereröffnung',4000)},'Der Fall wurde mit seiner bisherigen Historie wiedereröffnet.');}catch(error){this.error.set(this.describe(error));}}
 private async send(action:string,body:unknown,label:string):Promise<void>{const entity=this.selected();if(!this.canAct()||!entity)return;await this.execute({path:`collection-cases/${entity.id}/${action}`,body,revision:entity.revision,key:crypto.randomUUID(),label,caseId:entity.id});}
 retryPending():void{const command=this.pending();if(command&&!this.busy()&&this.writable())void this.execute(command,true);}
 private async execute(command:PendingCommand,retry=false):Promise<void>{
  if(!this.writable()||this.busy()||(!retry&&this.pending()))return;const context=this.context();const request=retry?command:{...command,body:structuredClone(command.body)};this.pending.set(request);this.busy.set(true);this.error.set('');this.notice.set('');
  try{const entity=await this.api.command<CollectionCase>(request.path,request.body,request.revision,request.key);if(!this.current(context))return;
   checkCollectionCase(entity,context.tenantId,request.caseId);if(this.invoice()&&entity.invoiceId!==this.invoice()!.id)throw new Error('Der zurückgegebene Fall gehört nicht zur gewählten Rechnung. Bitte denselben Vorgang erneut prüfen.');
   this.pending.set(null);this.selected.set(entity);this.invoice.set(null);this.preview.set(null);this.balance.set(null);this.refreshRequired.set(true);this.notice.set(request.label);this.resetForms();
   try{if(!this.current(context))return;const balance=await this.api.get<CollectionBalanceResponse>(`collection-cases/${entity.id}/balance`);if(!this.current(context))return;this.acceptBalance(balance,entity,context);this.refreshRequired.set(false);}
   catch(error){if(this.current(context)){this.clearForbidden(error);this.error.set('Der Vorgang wurde gespeichert; der aktuelle Saldo konnte anschließend nicht geprüft werden. '+this.describe(error));}}
   if(this.current(context)){void this.loadWorklist(this.page());void this.loadAssignees();}
  }catch(error){if(this.current(context)){
   this.preview.set(null);this.previewAcknowledged=false;const status=error instanceof ApiError?error.status:0;
   if(status>=400&&status<500){this.pending.set(null);this.refreshRequired.set(!!this.selected());this.balance.set(null);this.clearForbidden(error);this.error.set(this.describe(error));}
   else this.error.set('Die Bestätigung ist ausgeblieben. Der Vorgang könnte bereits gespeichert sein. Bitte denselben Vorgang mit unverändertem Inhalt erneut prüfen; dabei wird dieselbe Vorgangskennung verwendet. '+this.describe(error));
  }}finally{if(this.current(context))this.busy.set(false);}
 }
}
