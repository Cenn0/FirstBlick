import {Component,DestroyRef,computed,effect,inject,signal,untracked} from '@angular/core';
import {FormsModule,NgModel} from '@angular/forms';
import {ActivatedRoute,RouterLink} from '@angular/router';
import {ApiService,Entity,Page} from '../core/api.service';
import {AuthService} from '../core/auth.service';
import {ApiError,errorMessage} from '../core/transport';
import {decimalInput} from '../core/decimal-input';
import {decimalDisplay,moneyDisplay} from '../core/decimal-format';
import {downloadBlob} from '../core/integrations.models';
import {CostAnalysisInput,CostCapture,CostSource,CostForecast,CostForecastSummary,SupplierTreatment,costMissingLabel,emptyCostInput} from '../core/cost.models';
interface TimeInput{selected:boolean;hourlyCost:string;evidence:string}
interface SupplierInput{selected:boolean;treatment:SupplierTreatment|'';projectNet:string;evidence:string}
interface ZeroInput{selected:boolean;evidence:string}
interface SupplierCostAllocation{id:string;revision:number;supplierInvoiceId:string;projectId:string;netAmount:string;reason:string;reversesAllocationId:string|null}
interface SupplierCostAllocationList{items:SupplierCostAllocation[];active:string[];allocatedNet:string;availableNet:string}
const uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
@Component({imports:[FormsModule,RouterLink],templateUrl:'./cost-analysis.component.html'})
export class CostAnalysisComponent{
 readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);
 readonly allowed=computed(()=>['ManagingDirector','Office'].includes(this.auth.tenant()?.role||'')&&this.auth.can('orders.read')&&!this.auth.recoveryHold());
 readonly orders=signal<Entity[]>([]);readonly supplierOptions=signal<Entity[]>([]);readonly capture=signal<CostCapture|null>(null);readonly forecasts=signal<CostForecastSummary[]>([]);readonly openedForecast=signal<CostForecast|null>(null);readonly forecastError=signal('');forecastReason='';readonly busy=signal(false);readonly loading=signal(false);readonly error=signal('');readonly dirty=signal(false);readonly extraSuppliers=signal<Entity[]>([]);
 readonly allocationJournal=signal<SupplierCostAllocation[]>([]);readonly allocationActive=signal<string[]>([]);readonly allocationAvailable=signal<string|null>(null);
 allocationSupplierId='';allocationNet='';allocationReason='';allocationReverseReason='';
 readonly kinds=[{key:'time-entries',title:'Arbeitszeiten'},{key:'stock-movements',title:'Materialverbrauch & Rückgaben'},{key:'supplier-invoices',title:'Eingangsbelege & Fremdleistungen'}];
 orderId='';supplierId='';timeInputs:Record<string,TimeInput>={};supplierInputs:Record<string,SupplierInput>={};zeroInputs:Record<string,ZeroInput>={};sourcePage:Record<string,number>={};recordedCostsCoverage='';forecastEnabled=false;remainingForecastCost='';forecastEvidence='';
 private generation=0;private orderLookup=0;private supplierLookup=0;private initial=true;
 constructor(){inject(DestroyRef).onDestroy(()=>{this.generation++;});effect(()=>{this.auth.accessSignature();untracked(()=>{this.generation++;this.reset();if(this.allowed())void this.loadOrders();});});}
 private current(generation:number,access:string):boolean{return generation===this.generation&&access===this.auth.accessSignature();}
 private reset():void{this.allocationJournal.set([]);this.allocationActive.set([]);this.allocationAvailable.set(null);this.allocationSupplierId='';this.allocationNet='';this.allocationReason='';this.allocationReverseReason='';this.forecasts.set([]);this.openedForecast.set(null);this.forecastError.set('');this.forecastReason='';this.orders.set([]);this.supplierOptions.set([]);this.extraSuppliers.set([]);this.orderId='';this.supplierId='';this.capture.set(null);this.timeInputs={};this.supplierInputs={};this.zeroInputs={};this.sourcePage={};this.recordedCostsCoverage='';this.forecastEnabled=false;this.remainingForecastCost='';this.forecastEvidence='';this.dirty.set(false);this.error.set('');this.busy.set(false);this.loading.set(false);}
 name(item:Entity|undefined):string{return String(item?.['title']||item?.['name']||item?.['number']||'Vorgang');}
 money(value:string|null):string{return value===null?'Noch ungeklärt':moneyDisplay(value);}
 percent(value:string|null):string{if(value===null)return 'Noch ungeklärt';const formatted=decimalDisplay(value,0,6);return formatted===null?'Wert nicht verfügbar':formatted+' %';}
 date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short'}).format(new Date(value));}
 missingLabel=costMissingLabel;
 missingDetail(value:string):string{const key=value.split(':')[0],source=this.capture()?.report.sources.find(item=>item.kind+'/'+item.id===key);return (source?this.sourceTitle(source)+': ':'')+costMissingLabel(value);}
 basisLabel(value:string):string{return value==='Recorded unit cost of posted consumption/return; receipts are not counted again'?'Dokumentierte Stückkosten von gebuchtem Verbrauch oder Rückgabe. Wareneingänge werden nicht zusätzlich gezählt.':value;}
 sourceTitle(source:CostSource):string{return source.title||this.kinds.find(kind=>kind.key===source.kind)?.title||'Kostenquelle';}
 sources(kind:string):CostSource[]{const current=this.capture()?.report.sources.filter(source=>source.kind===kind)||[];if(kind!=='supplier-invoices')return current;return [...current,...this.extraSuppliers().filter(item=>!current.some(source=>source.id===item.id)).map(item=>({kind,id:item.id,revision:item.revision,digest:'',title:String(item['number']||item['title']||'Ausgewählter Eingangsbeleg'),netCost:null,basis:'',missing:item['status']==='Approved'?'supplier_cost_treatment_missing':'supplier_invoice_not_approved'}))];}
 pagedSources(kind:string):CostSource[]{return this.sources(kind).slice(((this.sourcePage[kind]||1)-1)*25,(this.sourcePage[kind]||1)*25);}
 pages(kind:string):number{return Math.max(1,Math.ceil(this.sources(kind).length/25));}
 changed():void{this.dirty.set(true);this.error.set('');}
 canConfirmZero(source:CostSource):boolean{return source.missing==='material_cost_missing'||!!this.zeroInputs[source.id]?.selected;}
 treatmentChanged(id:string):void{const input=this.supplierInputs[id];input.projectNet=input.treatment==='StockValuation'?'0':'';this.changed();}
 private reject(error:unknown):void{if(error instanceof ApiError&&(error.status===401||error.status===403)){this.generation++;this.orderLookup++;this.supplierLookup++;this.reset();}this.error.set(this.describe(error));}
 changeOrder(id:string,select:NgModel):void{if(id===this.orderId)return;if(this.dirty()&&!confirm('Die eingegebenen Kostenansätze verwerfen und einen anderen Auftrag öffnen?')){select.control.setValue(this.orderId,{emitEvent:false,emitViewToModelChange:false});return;}void this.chooseOrder(id);}
 private describe(error:unknown):string{if(error instanceof ApiError){if(error.problem.code==='cost_allocation_exceeded')return 'Der Projektanteil darf den Nettoansatz des Eingangsbelegs nicht überschreiten. Bereits über Materialbewegungen bewertete Kosten erhalten hier keinen weiteren Kostenansatz.';if(error.problem.code==='unknown_cost_source')return 'Eine bewertete Quelle ist nicht mehr verfügbar. Bitte die Projektquellen neu einlesen und die Bewertung erneut prüfen.';if(error.problem.code==='wrong_cost_project')return 'Ein ausgewählter Beleg gehört zu einer Bestellung eines anderen Projekts. Bitte die Zuordnung prüfen.';if(error.problem.code==='cost_report_budget')return 'Die Nachkalkulation überschreitet den Umfang von 5.000 Quellen pro Kategorie. Bitte eine begleitete Auswertung mit der Betriebsverwaltung abstimmen; es wurde kein unvollständiger Bericht erzeugt.';}return errorMessage(error);}
 async loadOrders(search=''):Promise<void>{if(!this.allowed())return;const initial=this.initial;this.initial=false;const version=++this.orderLookup,generation=this.generation,access=this.auth.accessSignature();this.loading.set(true);try{const result=await this.api.get<Page>(`orders?${new URLSearchParams({search,pageSize:'100'})}`);if(!this.current(generation,access)||version!==this.orderLookup)return;const selected=this.orders().find(order=>order.id===this.orderId);this.orders.set(selected&&!result.items.some(order=>order.id===selected.id)?[selected,...result.items]:result.items);if(initial){const selected=this.route.snapshot.queryParamMap.get('orderId');if(selected)await this.chooseOrder(selected);}}
 catch(error){if(this.current(generation,access))this.reject(error);}finally{if(this.current(generation,access)&&version===this.orderLookup)this.loading.set(false);}}
 async lookupSuppliers(search=''):Promise<void>{if(!this.allowed())return;const version=++this.supplierLookup,generation=this.generation,access=this.auth.accessSignature();try{const result=await this.api.get<Page>(`supplier-invoices?${new URLSearchParams({search,status:'Approved',pageSize:'100'})}`);if(this.current(generation,access)&&version===this.supplierLookup)this.supplierOptions.set(result.items);}catch(error){if(this.current(generation,access)&&version===this.supplierLookup)this.reject(error);}}
 async chooseOrder(id:string):Promise<void>{const generation=++this.generation,access=this.auth.accessSignature();this.orderId=id;this.allocationJournal.set([]);this.allocationActive.set([]);this.allocationAvailable.set(null);this.allocationSupplierId='';this.allocationNet='';this.allocationReason='';this.allocationReverseReason='';this.capture.set(null);this.forecasts.set([]);this.openedForecast.set(null);this.forecastError.set('');this.forecastReason='';this.extraSuppliers.set([]);this.supplierOptions.set([]);this.supplierId='';this.timeInputs={};this.supplierInputs={};this.zeroInputs={};this.sourcePage={};this.recordedCostsCoverage='';this.forecastEnabled=false;this.remainingForecastCost='';this.forecastEvidence='';this.dirty.set(false);this.error.set('');this.loading.set(false);if(!id||!this.allowed())return;if(!uuid.test(id)){this.error.set('Bitte einen gültigen Auftrag auswählen.');return;}this.busy.set(true);try{const result=await this.api.command<CostCapture>(`orders/${id}/cost-analysis`,emptyCostInput());if(!this.current(generation,access))return;this.accept(result);void this.loadForecasts(id,generation,access);if(!this.orders().some(order=>order.id===id)){const order=await this.api.get<Entity>(`orders/${id}`);if(!this.current(generation,access))return;this.orders.update(items=>[order,...items]);}void this.lookupSuppliers();}catch(error){if(this.current(generation,access))this.reject(error);}finally{if(this.current(generation,access))this.busy.set(false);}}
 refresh():void{if(this.dirty()&&!confirm('Die eingegebenen Kostenansätze verwerfen und die aktuellen Projektquellen neu einlesen?'))return;void this.chooseOrder(this.orderId);}
 private accept(value:CostCapture):void{if(value.report.orderId!==this.orderId)throw new Error('Der Bericht gehört nicht zum ausgewählten Auftrag. Bitte erneut einlesen.');this.capture.set(value);this.extraSuppliers.update(items=>items.filter(item=>value.report.sources.some(source=>source.kind==='supplier-invoices'&&source.id===item.id)));for(const source of value.report.sources){if(source.kind==='time-entries')this.timeInputs[source.id]??={selected:false,hourlyCost:'',evidence:''};if(source.kind==='supplier-invoices')this.supplierInputs[source.id]??={selected:false,treatment:'',projectNet:'',evidence:''};if(source.kind==='stock-movements')this.zeroInputs[source.id]??={selected:false,evidence:''};}this.dirty.set(false);}
 isAdditionalSupplier(id:string):boolean{return this.extraSuppliers().some(item=>item.id===id);}
 addSupplier():void{const invoice=this.supplierOptions().find(item=>item.id===this.supplierId);if(!invoice||this.sources('supplier-invoices').some(source=>source.id===invoice.id))return;this.extraSuppliers.update(items=>[...items,invoice]);this.supplierInputs[invoice.id]={selected:true,treatment:'',projectNet:'',evidence:''};this.sourcePage['supplier-invoices']=this.pages('supplier-invoices');this.supplierId='';this.changed();}
 removeSupplier(id:string):void{if(this.capture()?.report.sources.some(source=>source.kind==='supplier-invoices'&&source.id===id)){this.supplierInputs[id]={selected:false,treatment:'',projectNet:'',evidence:''};}else{this.extraSuppliers.update(items=>items.filter(item=>item.id!==id));delete this.supplierInputs[id];}this.changed();}
 private text(value:string,label:string):string{const trimmed=value.trim();if(!trimmed)throw new Error('Bitte '+label+' benennen.');return trimmed;}
 private input():CostAnalysisInput{return {timeCosts:Object.entries(this.timeInputs).filter(([,value])=>value.selected).map(([timeEntryId,value])=>({timeEntryId,hourlyCost:decimalInput(value.hourlyCost),evidence:this.text(value.evidence,'die Quelle des Stundensatzes')})),supplierCosts:Object.entries(this.supplierInputs).filter(([,value])=>value.selected).map(([supplierInvoiceId,value])=>{if(!value.treatment)throw new Error('Bitte die Behandlung jedes ausgewählten Eingangsbelegs ausdrücklich auswählen.');return {supplierInvoiceId,treatment:value.treatment,projectNet:decimalInput(value.projectNet),evidence:this.text(value.evidence,'die Grundlage der Belegzuordnung')};}),zeroStockCosts:Object.entries(this.zeroInputs).filter(([,value])=>value.selected).map(([stockMovementId,value])=>({stockMovementId,evidence:this.text(value.evidence,'den Nachweis für den kostenlosen Materialansatz')})),remainingForecastCost:this.forecastEnabled?decimalInput(this.remainingForecastCost):null,forecastEvidence:this.forecastEnabled?this.text(this.forecastEvidence,'die Grundlage der Restkostenschätzung'):'',recordedCostsCoverage:this.recordedCostsCoverage.trim()};}
 async calculate():Promise<void>{if(!this.allowed()||!this.capture()||this.busy()||!uuid.test(this.orderId))return;const generation=this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');try{const body=this.input(),value=await this.api.command<CostCapture>(`orders/${this.orderId}/cost-analysis`,body);if(this.current(generation,access))this.accept(value);}catch(error){if(this.current(generation,access))this.reject(error);}finally{if(this.current(generation,access))this.busy.set(false);}}
 private async loadForecasts(id:string,generation:number,access:string):Promise<void>{
   try{const data=await this.api.get<{items:CostForecastSummary[];total:number}>(`orders/${id}/cost-forecasts`);
     if(this.current(generation,access)&&this.orderId===id){this.forecasts.set(data.items);this.forecastError.set('');}
   }catch(error){if(this.current(generation,access)&&this.orderId===id){if(error instanceof ApiError&&(error.status===401||error.status===403))this.reject(error);
     else this.forecastError.set(errorMessage(error));}}
 }

 async allocationSourceChanged(id:string):Promise<void>{
   this.allocationSupplierId=id;this.allocationJournal.set([]);this.allocationActive.set([]);this.allocationAvailable.set(null);
   if(!uuid.test(id)||!this.allowed())return;
   const generation=this.generation,access=this.auth.accessSignature();
   try{const view=await this.api.get<SupplierCostAllocationList>(`supplier-invoices/${id}/cost-allocations`);
      if(this.current(generation,access)&&this.allocationSupplierId===id){this.allocationJournal.set(view.items);this.allocationActive.set(view.active);this.allocationAvailable.set(view.availableNet);}}
   catch(error){if(this.current(generation,access)&&this.allocationSupplierId===id)this.reject(error);}
 }
 activeAllocations():SupplierCostAllocation[]{return this.allocationJournal().filter(item=>this.allocationActive().includes(item.id));}
 async postAllocation():Promise<void>{
   const capture=this.capture(),invoiceId=this.allocationSupplierId,projectId=capture?.report.projectId;
   if(!this.allowed()||this.busy()||this.dirty()||!uuid.test(invoiceId)||!projectId||!uuid.test(projectId))return;
   const generation=this.generation,access=this.auth.accessSignature(),orderId=this.orderId;
   this.busy.set(true);this.error.set('');
   try{
     const invoice=await this.api.get<Entity>(`supplier-invoices/${invoiceId}`);
     if(!this.current(generation,access)||this.orderId!==orderId)return;
     const digest=invoice['approvedDigest'];
     if(invoice['status']!=='Approved'||typeof digest!=='string'||digest.length!==64)
       throw new Error('Der Eingangsbeleg ist noch nicht freigegeben oder besitzt keinen überprüfbaren Belegstand.');
     const amount=decimalInput(this.allocationNet),reason=this.allocationReason.trim();
     if(!reason)throw new Error('Bitte die Grundlage des Projektanteils benennen.');
     const posted=await this.api.command<SupplierCostAllocation>(`supplier-invoices/${invoiceId}/cost-allocations`,
       {projectId,netAmount:amount,reason,expectedSourceDigest:digest});
     if(!this.current(generation,access)||this.orderId!==orderId)return;
     if(posted.supplierInvoiceId!==invoiceId||posted.projectId!==projectId)
       throw new Error('Die Bestätigung der Kostenbuchung gehört zu einem anderen Projekt.');
     this.allocationNet='';this.allocationReason='';
     await this.allocationSourceChanged(invoiceId);
     if(this.current(generation,access)&&this.orderId===orderId)await this.recalculateAfterAllocation();
   }catch(error){if(this.current(generation,access))this.reject(error);}
   finally{if(this.current(generation,access))this.busy.set(false);}
 }
 async reverseAllocation(entry:SupplierCostAllocation):Promise<void>{
   if(!this.allowed()||this.busy()||this.dirty()||!this.allocationActive().includes(entry.id)
     ||entry.projectId!==this.capture()?.report.projectId)return;
   const reason=this.allocationReverseReason.trim();if(!reason){this.error.set('Bitte die Stornierungsbegründung eingeben.');return;}
   const generation=this.generation,access=this.auth.accessSignature(),invoiceId=this.allocationSupplierId,orderId=this.orderId;
   this.busy.set(true);this.error.set('');
   try{
     await this.api.command<SupplierCostAllocation>(`supplier-cost-allocations/${entry.id}/reverse`,{reason},entry.revision);
     if(!this.current(generation,access)||this.orderId!==orderId)return;
     this.allocationReverseReason='';
     await this.allocationSourceChanged(invoiceId);
     if(this.current(generation,access)&&this.orderId===orderId)await this.recalculateAfterAllocation();
   }catch(error){if(this.current(generation,access))this.reject(error);}
   finally{if(this.current(generation,access))this.busy.set(false);}
 }
 private async recalculateAfterAllocation():Promise<void>{
   if(!this.capture()||!this.allowed()||!uuid.test(this.orderId))return;
   const generation=this.generation,access=this.auth.accessSignature(),id=this.orderId;
   // Keep the user's reviewed source inputs; committed allocations are merged by the server.
   // A stale manual supplier estimate cannot be silently used after an allocation.
   const inputs=this.capture()!.report.reviewedInputs;
   const value=await this.api.command<CostCapture>(`orders/${id}/cost-analysis`,{
     ...inputs,supplierCosts:inputs.supplierCosts.filter(item=>!this.allocationJournal()
       .some(entry=>this.allocationActive().includes(entry.id)&&entry.supplierInvoiceId===item.supplierInvoiceId))
   });
   if(this.current(generation,access)&&this.orderId===id)this.accept(value);
 }

 async saveForecast():Promise<void>{
   const value=this.capture(),reason=this.forecastReason.trim();
   if(!value||!this.allowed()||this.dirty()||this.busy()||!uuid.test(this.orderId)||!reason)return;
   const id=this.orderId,generation=this.generation,access=this.auth.accessSignature();
   this.busy.set(true);this.error.set('');this.forecastError.set('');
   try{
     const saved=await this.api.command<CostForecast>(`orders/${id}/cost-forecasts`,{input:value.report.reviewedInputs,expectedReportDigest:value.report.digest,reason});
     if(!this.current(generation,access)||this.orderId!==id)return;
     if(saved.orderId!==id)throw new Error('Der gespeicherte Forecast gehört zu einem anderen Auftrag.');
     this.openedForecast.set(saved);this.forecastReason='';await this.loadForecasts(id,generation,access);
   }catch(error){if(this.current(generation,access))this.reject(error);}
   finally{if(this.current(generation,access))this.busy.set(false);}
 }
 async openForecast(id:string):Promise<void>{
   if(!this.allowed()||this.busy()||!uuid.test(id)||!this.orderId)return;
   const orderId=this.orderId,generation=this.generation,access=this.auth.accessSignature();
   this.busy.set(true);this.forecastError.set('');
   try{const saved=await this.api.get<CostForecast>(`cost-forecasts/${id}`);
     if(!this.current(generation,access)||this.orderId!==orderId)return;
     if(saved.orderId!==orderId)throw new Error('Die historische Prognose gehört nicht zum ausgewählten Auftrag.');
     this.openedForecast.set(saved);
   }catch(error){if(this.current(generation,access)){
     if(error instanceof ApiError&&(error.status===401||error.status===403))this.reject(error);
     else this.forecastError.set(errorMessage(error));}}
   finally{if(this.current(generation,access))this.busy.set(false);}
 }
 downloadForecast():void{
   const saved=this.openedForecast();if(!saved||!this.allowed()||this.busy())return;
   downloadBlob(new Blob([JSON.stringify({schema:'firstblick.cost-forecast/1.0.0',...saved},null,2)+'\n'],{type:'application/json;charset=utf-8'}),
     `FirstBlick-Prognose-${saved.orderId}-v${saved.version}.json`);
 }
 download():void{const value=this.capture();if(!value||!this.allowed()||this.dirty()||this.busy())return;downloadBlob(new Blob([JSON.stringify({schema:'firstblick.cost-analysis/1.0.0',...value},null,2)+'\n'],{type:'application/json;charset=utf-8'}),`FirstBlick-Nachkalkulation-v1-${value.report.orderId}.json`);}
}
