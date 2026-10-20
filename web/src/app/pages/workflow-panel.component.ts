import { Component, ElementRef, ViewChild, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Entity, Page } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ApiError, errorMessage } from '../core/transport';
import { decimalInput } from '../core/decimal-input';
import { labelFor, moduleByKind } from '../core/modules';

interface ActionField {
  key:string; label:string; type?:'text'|'email'|'number'|'date'|'textarea'|'select';
  required?:boolean; min?:number; max?:number; value?:unknown; relation?:string;
  options?:{value:string;label:string}[];
}
interface Action {
  key:string; title:string; description:string; path:string; fields:ActionField[];
  permission:string; target?:string; nestedResult?:string; payload?:Record<string,unknown>; approval?:string;
}
interface Delivery {id:string;channel:string;recipient:string;state:string;requestedAt:string;resolutionNote:string}
interface QuotePresentation {sellerName:string;buyerName:string;buyerAddress:string;number:string;title:string;validUntil:string|null;issuedAt:string;digest:string}

@Component({
  selector:'app-workflow-panel', imports:[FormsModule,RouterLink],
  template:`
    @if(presentation();as issued){<section class="snapshot"><h3>Festgehaltener Angebotsstand</h3><dl class="detail-grid"><div><dt>Angebot</dt><dd>{{issued.number}} · {{issued.title}}</dd></div><div><dt>Anbieter</dt><dd>{{issued.sellerName}}</dd></div><div><dt>Auftraggeber</dt><dd>{{issued.buyerName}}<span class="document-date">{{issued.buyerAddress}}</span></dd></div><div><dt>Ausgegeben am</dt><dd>{{date(issued.issuedAt)}}</dd></div>@if(issued.validUntil){<div><dt>Gültig bis</dt><dd>{{dateOnly(issued.validUntil)}}</dd></div>}</dl><p class="small muted">Annahme, Auftragsübernahme und PDF beziehen sich auf diese festgehaltenen Parteien, Positionen und Angebotsdaten.</p></section>}
    @if(lines().length){<div class="table-wrap detail-lines"><table><caption>Erfasste Positionen</caption><thead><tr><th>Leistung / Material</th><th>Menge</th><th>Einheit</th><th>Einzelpreis netto</th></tr></thead><tbody>@for(line of lines();track $index){<tr><td data-label="Leistung / Material">{{line['description']}}</td><td data-label="Menge">{{line['quantity']}}</td><td data-label="Einheit">{{line['unit']||'–'}}</td><td data-label="Einzelpreis netto">{{line['unitNet']===undefined?'Wird beim Festhalten berechnet':money(line['unitNet'])}}</td></tr>}</tbody></table></div>}
    @if(metrics().length){<section class="snapshot"><h3>{{kind()==='materials'?'Bestand im Hauptlager':'Aktueller Abgleich'}}</h3><dl class="detail-grid">@for(metric of metrics();track metric.key){<div><dt>{{metric.label}}</dt><dd>{{metric.value}}</dd></div>}</dl></section>}
    @if(metricError()){<div class="alert warning" role="status">{{metricError()}}</div>}
    @if(deliveries().length){<section class="snapshot"><h3>Versandverlauf dieser Version</h3>@for(delivery of deliveries();track delivery.id){<div class="activity-row"><div><strong>{{channelName(delivery.channel)}} · {{delivery.recipient}}</strong><span>{{date(delivery.requestedAt)}}{{delivery.resolutionNote?' · '+delivery.resolutionNote:''}}</span></div><span class="badge">{{labelFor(delivery.state)}}</span></div>}</section>}
    @if(kind()==='orders'){<details class="workflow-detail"><summary>Nachkalkulation & Prognose</summary><form #costForm="ngForm" (ngSubmit)="compareCosts()"><p class="small muted">Geben Sie den tatsächlich angefallenen Aufwand und die verbleibenden Kosten ein. Die Vertragswerte stammen aus dem Auftrag und angenommenen Nachträgen.</p><div class="form-grid"><div class="form-field"><label for="actual-cost">Bisherige Kosten netto (€)</label><input id="actual-cost" name="actual" [type]="'number'" [(ngModel)]="actualCost" required min="0" step="0.01"></div><div class="form-field"><label for="forecast-cost">Verbleibende Kosten netto (€)</label><input id="forecast-cost" name="forecast" [type]="'number'" [(ngModel)]="forecastCost" required min="0" step="0.01"></div></div><button class="button secondary" [disabled]="busy()||costForm.invalid">Kosten vergleichen</button></form>@if(comparison().length){<dl class="detail-grid">@for(metric of comparison();track metric.key){<div><dt>{{metric.label}}</dt><dd>{{metric.value}}</dd></div>}</dl>}</details>}
    <div class="detail-actions">@for(action of actions();track action.key){<button class="button secondary" (click)="prepare(action)">{{action.title}}</button>}@if(canShare()){<a class="button secondary" routerLink="/portal" [queryParams]="{new:1,kind:'quotes',sourceId:item().id}">Im Kundenportal freigeben</a>}</div>
    <dialog #dialog class="action-dialog" (cancel)="cancel($event)">
      @if(active();as action){<form #actionForm="ngForm" (ngSubmit)="execute()"><header class="dialog-heading"><h2>{{action.title}}</h2><button type="button" class="icon-button" aria-label="Vorgang schließen" (click)="close()" [disabled]="busy()">×</button></header><div class="dialog-body"><p>{{action.description}}</p>
        @if(error()){<div class="alert error" role="alert">{{error()}}</div>}
        @if(approvalRequired()){<div class="alert information"><p>Eine andere Geschäftsführung muss genau diesen Vorgang freigeben.</p><button type="button" class="button secondary" (click)="requestApproval()" [disabled]="busy()">Freigabe anfragen</button><a routerLink="/management" (click)="close()">Freigaben verwalten →</a></div>}
        @for(field of action.fields;track field.key){<div class="form-field workflow-field"><label [for]="'command-'+field.key">{{field.label}}{{field.required?' *':''}}</label>
          @if(field.relation){<input [attr.aria-label]="field.label+' suchen'" [name]="'search-'+field.key" type="search" placeholder="Auswahlliste durchsuchen" (input)="searchOptions(field,$any($event.target).value)"><select [id]="'command-'+field.key" [name]="field.key" [(ngModel)]="values[field.key]" [required]="!!field.required"><option value="">Bitte auswählen</option>@for(option of options()[field.key]||[];track option.id){<option [value]="option.id">{{name(option)}}</option>}</select>}
          @else if(field.type==='select'){<select [id]="'command-'+field.key" [name]="field.key" [(ngModel)]="values[field.key]" [required]="!!field.required"><option value="">Bitte auswählen</option>@for(option of field.options||[];track option.value){<option [value]="option.value">{{option.label}}</option>}</select>}
          @else if(field.type==='textarea'){<textarea [id]="'command-'+field.key" [name]="field.key" [(ngModel)]="values[field.key]" [required]="!!field.required" maxlength="2000" rows="3"></textarea>}
          @else{<input [id]="'command-'+field.key" [name]="field.key" [type]="field.type||'text'" [(ngModel)]="values[field.key]" [required]="!!field.required" [min]="field.min??null" [max]="field.max??null" [attr.maxlength]="field.type==='number'?null:500" [attr.step]="field.type==='number'?'any':null">}
        </div>}
      </div><footer class="dialog-footer"><button type="button" class="button secondary" (click)="close()" [disabled]="busy()">Abbrechen</button><button class="button primary" [disabled]="busy()||actionForm.invalid||approvalRequired()">{{busy()?'Wird verarbeitet …':action.title}}</button></footer></form>}
    </dialog>
  `
})
export class WorkflowPanelComponent {
  readonly kind=input.required<string>(); readonly item=input.required<Entity>();
  readonly completed=output<{kind:string;id:string;message:string}>();
  readonly auth=inject(AuthService); private readonly api=inject(ApiService);
  readonly active=signal<Action|null>(null); readonly busy=signal(false); readonly error=signal('');
  readonly approvalRequired=signal(false); readonly options=signal<Record<string,Entity[]>>({});
  readonly metrics=signal<{key:string;label:string;value:string}[]>([]); readonly metricError=signal('');
  readonly comparison=signal<{key:string;label:string;value:string}[]>([]); readonly labelFor=labelFor;
  @ViewChild('dialog') dialog?:ElementRef<HTMLDialogElement>;
  values:Record<string,unknown>={}; actualCost='';forecastCost='';
  private actionKey='';private source?:Entity;private sourceScope='';private optionVersions:Record<string,number>={};
  readonly actions=computed(()=>this.availableActions().filter(action=>this.auth.can(action.permission)&&this.auth.hasModule(moduleByKind.get(action.permission.replace(/\.write$/,''))?.module)));
  readonly deliveries=computed(()=>(this.item()['deliveries']||[]) as Delivery[]);
  readonly presentation=computed(()=>(this.item()['presentation']||this.item()['quotePresentation']||null)as QuotePresentation|null);
  readonly lines=computed<Record<string,unknown>[]>(()=>{
    const item=this.item(); const snapshot=(item['snapshot']||item['contract'])as {lines?:{inputs:Record<string,unknown>;unitNet:number}[]}|undefined;
    if(snapshot?.lines)return snapshot.lines.map(line=>({...line.inputs,unitNet:line.unitNet}));
    return (item['lines']||[]) as Record<string,unknown>[];
  });
  constructor(){effect(()=>{const item=this.item();const kind=this.kind();const scope=this.auth.scope();untracked(()=>{this.metrics.set([]);this.metricError.set('');this.comparison.set([]);void this.loadMetrics(kind,item,scope);});});}
  name(item:Entity):string{return String(item['name']||item['title']||item['number']||item['reference']||item.id);}
  channelName(value:string):string{return value==='Email'?'E-Mail':value;}
  date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'short',timeStyle:'short'}).format(new Date(value));}
  dateOnly(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium'}).format(new Date(value+'T12:00:00'));}
  money(value:unknown):string{return new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR'}).format(Number(value));}
  canShare():boolean{return this.kind()==='quotes'&&['Issued','Accepted'].includes(String(this.item()['status']))&&!!this.item()['issuedDigest']&&this.auth.hasModule('portal')&&this.auth.can('portal-shares.write');}
  private availableActions():Action[]{
    const kind=this.kind(),item=this.item(),status=String(item['status']),path=`${kind}/${item.id}`;
    const actions:Action[]=[];
    const add=(key:string,title:string,description:string,suffix:string,fields:ActionField[]=[],extra:Partial<Action>={})=>actions.push({key,title,description,path:`${path}/${suffix}`,fields,permission:`${kind}.write`,...extra});
    const amount:ActionField={key:'amount',label:'Betrag (€)',type:'number',required:true,min:0.01};
    const quantity:ActionField={key:'quantity',label:'Menge',type:'number',required:true,min:0.000001};
    const reason:ActionField={key:'reason',label:'Begründung',type:'textarea',required:true};
    const project:ActionField={key:'projectId',label:'Projekt',relation:'projects',required:true};
    if(['quotes','invoices'].includes(kind)&&item['snapshot'])add('pdf','PDF erstellen','Der Beleg wird aus den festgehaltenen Werten als Dokumentversion erzeugt. Sie finden das Ergebnis anschließend unter Dokumente.','pdf',[],{target:'documents'});
    if(kind==='quotes'){
      if(status==='Issued')add('reject','Ablehnung dokumentieren','Dokumentieren Sie die Ablehnung dieser Angebotsversion durch den Kunden.','reject');
      if(['Issued','Rejected'].includes(status))add('new-version','Neue Angebotsversion','Die bisherigen Werte bleiben erhalten. Es entsteht ein neuer bearbeitbarer Entwurf.','new-version',[],{nestedResult:'quote'});
      if(['Issued','Accepted'].includes(status))add('delivery','Angebot versenden','Die festgehaltene Angebotsversion wird zur Zustellung vorgemerkt. Prüfen Sie Kanal und Empfänger. Den tatsächlichen Versandstatus sehen Sie danach im Versandverlauf.','delivery',[{key:'channel',label:'Kanal',type:'select',required:true,value:'Email',options:[{value:'Email',label:'E-Mail'},{value:'WhatsApp',label:'WhatsApp'},{value:'Push',label:'Push'}]},{key:'recipient',label:'Empfängeradresse / Kanaladresse',required:true}]);
    }
    if(kind==='change-orders'&&status==='Draft')add('accept','Nachtrag annehmen','Halten Sie die nachweisbare Annahme dieser zusätzlichen Leistung fest. Der Nachtrag wird anschließend unveränderlich.','accept');
    if(kind==='purchase-orders'){
      if(status==='Draft')add('issue','Bestellung auslösen','Mengen und Preise werden verbindlich festgehalten. Eine Belegnummer wird im Betrieb vergeben.','issue',[],{approval:'purchase.issue'});
      if(['Ordered','PartiallyReceived'].includes(status))add('receive','Wareneingang erfassen','Erfassen Sie die tatsächlich gelieferte Menge einer Position. Teillieferungen bleiben nachvollziehbar.','receive',[{key:'lineId',label:'Bestellposition',type:'select',required:true,options:((item['lines']||[])as {id:string;description:string;quantity:number}[]).map(line=>({value:line.id,label:`${line.description} · bestellt: ${line.quantity}`}))},quantity,{key:'location',label:'Lagerort',required:true,value:'Hauptlager'}],{nestedResult:'order'});
    }
    if(kind==='materials')add('reserve','Material reservieren','Reservieren Sie verfügbares Material für ein Projekt. Der verfügbare Bestand wird auf dem Server geprüft.','reserve',[project,quantity,{key:'location',label:'Lagerort',required:true,value:'Hauptlager'},{key:'requiredOn',label:'Benötigt am',type:'date'}],{permission:'material-reservations.write',target:'material-reservations'});
    if(kind==='material-reservations'&&Number(item['quantity'])>Number(item['consumedQuantity'])+Number(item['releasedQuantity'])){
      add('consume','Verbrauch buchen','Der Verbrauch reduziert Reservierung und Lagerbestand. Geben Sie die belegten Kosten je Materialeinheit an.','consume',[quantity,{key:'unitCost',label:'Kosten je Einheit (€)',type:'number',required:true,min:0}],{nestedResult:'reservation'});
      add('release','Reservierung freigeben','Geben Sie nicht mehr benötigte Mengen für andere Projekte frei.','release',[quantity]);
    }
    if(kind==='time-entries'&&!item['approved']&&['ManagingDirector','SiteManager'].includes(this.auth.tenant()?.role||''))add('approve','Arbeitszeit bestätigen','Prüfen Sie Arbeitsbeginn, Arbeitsende, Pausen und die Projektzuordnung. Bestätigte Zeiten können nicht mehr frei geändert werden.','approve');
    if(kind==='absences'&&status==='Requested'){
      add('approve','Abwesenheit genehmigen','Die Abwesenheit wird mit bestehenden Einsätzen und genehmigten Abwesenheiten abgeglichen.','approve');
      add('reject','Abwesenheit ablehnen','Die beantragte Abwesenheit wird als abgelehnt dokumentiert.','reject');
    }
    if(kind==='defects'){
      if(['Open','InProgress'].includes(status))add('resolve','Behebung dokumentieren','Beschreiben Sie die ausgeführte Nacharbeit und das Ergebnis.','resolve',[{key:'resolution',label:'Behebung / Ergebnis',type:'textarea',required:true}]);
      if(status==='Resolved'&&['ManagingDirector','SiteManager'].includes(this.auth.tenant()?.role||''))add('accept','Behebung bestätigen','Bestätigen Sie nach Ihrer Prüfung die dokumentierte Behebung.','accept');
    }
    if(kind==='training-records'){
      if(status==='Draft'&&['ManagingDirector','Worker'].includes(this.auth.tenant()?.role||''))add('submit','Wochenbericht einreichen','Der Wochenbericht wird dem zugeordneten Ausbilder zur Prüfung vorgelegt.','submit');
      if(status==='Submitted'&&this.auth.tenant()?.employeeId===item['mentorEmployeeId'])add('review','Ausbildungsnachweis prüfen','Prüfen Sie als zugeordneter Ausbilder diesen Bericht und halten Sie die konkrete Rückmeldung fest.','review',[{key:'mentorFeedback',label:'Rückmeldung des Ausbilders',type:'textarea',required:true}]);
    }
    if(kind==='maintenance-contracts'&&item['active']!==false)add('complete','Wartung abschließen',`Dokumentieren Sie die erledigte Fälligkeit ${String(item['nextDueDate'])}. Wählen Sie das abgeschlossene Wartungsprojekt für denselben Kunden und dasselbe Gebäude. Der nächste Termin folgt dem vertraglichen Intervall.`,'complete',[project],{nestedResult:'contract',payload:{expectedDueDate:item['nextDueDate']}});
    if(kind==='payment-allocations'&&!item['reversedAt']){
      const released=((item['retentionReleases']||[])as {amount:number}[]).reduce((sum,x)=>sum+Number(x.amount),0);
      if(Number(item['retainedAmount'])>released)add('release-retention','Einbehalt freigeben','Der freigegebene Einbehalt wird wieder einziehbar. Die Freigabe ersetzt keinen Zahlungseingang.','release-retention',[{...amount,max:Number(item['retainedAmount'])-released},reason],{path:`payments/${item.id}/release-retention`});
    }
    if(kind==='supplier-invoices'&&status==='Draft'){
      add('approve','Eingangsrechnung prüfen','Prüfen Sie Lieferant, Originalbeleg, Rechnungsnummer, Fälligkeit und Beträge. Anschließend werden diese Werte festgehalten.','approve',[],{approval:'supplier-invoice.approve'});
      add('reject','Eingangsrechnung ablehnen','Dokumentieren Sie den Grund, weshalb dieser Beleg nicht freigegeben wird.','reject',[reason]);
    }
    if(kind==='supplier-payment-allocations'&&!item['reversedAt'])add('reverse','Lieferantenzuordnung zurücknehmen','Die ursprüngliche Zuordnung bleibt erhalten. Eine tatsächliche Rücklastschrift kann über den importierten Gegenumsatz belegt werden.','reverse',[reason,{key:'returnBankTransactionId',label:'Rücklastschrift / Gegenumsatz (optional)',relation:'bank-transactions'}],{path:`supplier-payments/${item.id}/reverse`});
    if(kind==='bank-transactions'){
      if(Number(item['amount'])<0)add('refund','Kundenerstattung abgleichen','Ordnen Sie einen bereits importierten Zahlungsausgang dem Kundenguthaben einer Rechnung zu. Es wird keine Überweisung ausgelöst.','',[{key:'invoiceId',label:'Ursprüngliche Ausgangsrechnung',relation:'invoices',required:true},amount,reason],{path:'payments/refund',permission:'payment-allocations.write',target:'payment-allocations',payload:{bankTransactionId:item.id}});
      add('supplier-payment',Number(item['amount'])<0?'Lieferantenzahlung abgleichen':'Lieferantenerstattung abgleichen','Ordnen Sie diesen vorhandenen Bankumsatz einer geprüften Eingangsrechnung zu. Der Abgleich führt keine Banküberweisung aus.','',[{key:'supplierInvoiceId',label:'Eingangsrechnung',relation:'supplier-invoices',required:true},amount,...(Number(item['amount'])<0?[{key:'discountAmount',label:'Skonto (€)',type:'number' as const,min:0,value:0}]:[]),reason],{path:'supplier-payments/allocate',permission:'supplier-payment-allocations.write',target:'supplier-payment-allocations',payload:{bankTransactionId:item.id,direction:Number(item['amount'])<0?'Payment':'Refund'}});
    }
    return actions;
  }
  async prepare(action:Action):Promise<void>{
    this.source=structuredClone(this.item());this.sourceScope=this.auth.scope();this.actionKey=crypto.randomUUID();
    this.values={...action.payload};for(const field of action.fields)this.values[field.key]=field.value??'';
    this.error.set('');this.approvalRequired.set(false);this.options.set({});this.active.set(action);
    this.dialog?.nativeElement.showModal();
    for(const field of action.fields.filter(field=>field.relation))void this.searchOptions(field,'');
  }
  async searchOptions(field:ActionField,search:string):Promise<void>{
    const version=(this.optionVersions[field.key]||0)+1;this.optionVersions[field.key]=version;const scope=this.auth.scope();
    try{const result=await this.api.get<Page>(`${field.relation}?${new URLSearchParams({search,pageSize:'100'})}`);if(scope===this.auth.scope()&&version===this.optionVersions[field.key])this.options.update(x=>({...x,[field.key]:result.items}));}catch(error){if(scope===this.auth.scope())this.error.set(errorMessage(error));}
  }
  cancel(event:Event):void{if(this.busy())event.preventDefault();else this.active.set(null);}
  close():void{if(!this.busy()){this.dialog?.nativeElement.close();this.active.set(null);this.source=undefined;}}
  async execute():Promise<void>{
    const action=this.active(),source=this.source;if(!action||!source||this.busy())return;
    if(this.sourceScope!==this.auth.scope()){this.error.set('Der Betrieb wurde gewechselt. Öffnen Sie den Vorgang im gewünschten Betrieb erneut.');return;}
    this.busy.set(true);this.error.set('');
    try{
      const payload={...this.values};for(const field of action.fields){if(field.type==='number'&&payload[field.key]!=='')payload[field.key]=decimalInput(payload[field.key]);if(!field.required&&payload[field.key]===''&&(field.relation||field.type==='date'))payload[field.key]=null;}
      const result=await this.api.command<Entity&Record<string,Entity>>(action.path,payload,source.revision,this.actionKey);
      const entity=action.nestedResult?result[action.nestedResult]:result;
      this.dialog?.nativeElement.close();this.active.set(null);
      if(this.sourceScope===this.auth.scope())this.completed.emit({kind:action.target||this.kind(),id:entity?.id||source.id,message:action.key==='delivery'?'Versand angefragt. Maßgeblich ist der tatsächliche Versandverlauf.':'Vorgang gespeichert.'});
    }catch(error){this.error.set(errorMessage(error));if(error instanceof ApiError&&error.problem.code==='approval_required'&&action.approval)this.approvalRequired.set(true);if(error instanceof ApiError&&error.status>=400&&error.status<500)this.actionKey=crypto.randomUUID();}
    finally{this.busy.set(false);}
  }
  async requestApproval():Promise<void>{const action=this.active(),source=this.source;if(!action?.approval||!source)return;this.busy.set(true);try{await this.api.command('approvals',{action:action.approval,kind:this.kind(),entityId:source.id,revision:source.revision,payload:this.values});this.dialog?.nativeElement.close();this.active.set(null);this.completed.emit({kind:this.kind(),id:source.id,message:'Konkreten Vorgang zur zweiten Freigabe vorgelegt.'});}catch(error){this.error.set(errorMessage(error));}finally{this.busy.set(false);}}
  private async loadMetrics(kind:string,item:Entity,scope:string):Promise<void>{
    let path='';
    if(kind==='invoices'&&item['status']==='Issued'&&item['kind']!=='CreditNote')path=`invoices/${item.id}/balance`;
    if(kind==='bank-transactions')path=`bank-transactions/${item.id}/balance`;
    if(kind==='supplier-invoices'&&item['status']==='Approved'&&item['kind']!=='CreditNote')path=`supplier-invoices/${item.id}/balance`;
    if(kind==='materials'&&this.auth.can('stock-movements.read'))path=`materials/${item.id}/stock`;
    if(!path)return;
    try{const data=await this.api.get<Record<string,unknown>>(path);if(this.auth.scope()===scope&&this.item().id===item.id)this.metrics.set(this.formatMetrics(data,kind==='materials'));}
    catch(error){if(this.auth.scope()===scope&&this.item().id===item.id)this.metricError.set(errorMessage(error));}
  }
  private formatMetrics(data:Record<string,unknown>,quantity=false):{key:string;label:string;value:string}[]{
    const labels:Record<string,string>={gross:'Brutto',credits:'Korrekturen',paid:'Zugeordnete Zahlungen',discount:'Skonto',retained:'Einbehalt',outstanding:'Offener Betrag',collectible:'Einziehbarer Betrag',overdue:'Überfällig',customerCredit:'Kundenguthaben',supplierCredit:'Lieferantenguthaben',allocated:'Bereits zugeordnet',unallocated:'Noch zuzuordnen',onHand:'Vorhanden',reserved:'Reserviert',available:'Verfügbar',contractNet:'Vertrag netto',plannedCost:'Geplante Kosten',actualCost:'Bisherige Kosten',forecastCost:'Gesamtkosten laut Prognose',forecastMargin:'Voraussichtlicher Deckungsbeitrag'};
    return Object.entries(data).filter(([key])=>key in labels).map(([key,value])=>({key,label:labels[key],value:typeof value==='boolean'?value?'Ja':'Nein':quantity?new Intl.NumberFormat('de-DE',{maximumFractionDigits:6}).format(Number(value)):this.money(value)}));
  }
  async compareCosts():Promise<void>{const item=this.item(),scope=this.auth.scope();this.busy.set(true);try{const data=await this.api.get<Record<string,unknown>>(`orders/${item.id}/cost-comparison?${new URLSearchParams({actualCost:String(this.actualCost),remainingForecastCost:String(this.forecastCost)})}`);if(scope===this.auth.scope()&&item.id===this.item().id)this.comparison.set(this.formatMetrics(data));}catch(error){this.metricError.set(errorMessage(error));}finally{this.busy.set(false);}}
}
