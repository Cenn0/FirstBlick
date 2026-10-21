import {Entity,Page} from '../core/api.service';

export type CollectionStatus='Active'|'Disputed'|'Closed';
export type CollectionStage='PaymentReminder'|'FirstReminder'|'SecondReminder';
export type ContactChannel='Letter'|'Email'|'Phone'|'Other';
export type ClosureKind='Settled'|'ManualHandling';
export interface CollectionAssignee{id:string;displayName:string}
export interface CollectionSource{kind:'invoices'|'payment-allocations';id:string;revision:number;digest:string}
export interface CollectionBalance{gross:string;credits:string;paid:string;discount:string;retained:string;outstanding:string;collectible:string;overdue:boolean;customerCredit:string}
export interface CollectionLedger{
 version:string;tenantId:string;invoiceId:string;invoiceDigest:string;asOf:string;
 invoice:{number:string;buyerName:string;buyerAddress:string;sellerName:string;sellerAddress:string;currency:string;dueDate:string};
 balance:CollectionBalance;sources:CollectionSource[];digest:string;
}
export interface CollectionStepInput{stage:CollectionStage;respondBy:string;reason:string;additionalMessage:string}
export interface CollectionPreview{version:string;caseId:string;caseRevision:number;caseHistoryDigest:string;input:CollectionStepInput;ledger:CollectionLedger;subject:string;text:string;digest:string}
export interface CollectionContact{channel:ContactChannel;occurredAt:string;occurredOn:string;evidenceReference:string;recordedBy:string;recordedAt:string}
export interface CollectionStep{id:string;preview:CollectionPreview;approvedBy:string;approvedAt:string;digest:string;contact:CollectionContact|null;withdrawal:{reason:string;actorId:string;at:string}|null}
export interface CollectionDispute{id:string;amount:string;reason:string;evidenceReference:string;ledgerDigest:string;openedBy:string;openedAt:string;openedOn:string;resolvedBy:string|null;resolvedAt:string|null;resolutionReason:string;resolutionEvidence:string}
export interface CollectionClosure{id:string;kind:ClosureKind;reason:string;evidenceReference:string;ledger:CollectionLedger;closedBy:string;closedAt:string;reopenedBy:string|null;reopenedAt:string|null;reopeningReason:string;reopeningEvidence:string}
export interface CollectionEvent{id:string;action:string;at:string;on:string;actorId:string;reason:string;evidenceReference:string;data:{relatedId:string|null;assignedToUserId:string|null;nextReviewOn:string|null;amount:string|null;closureKind:ClosureKind|null;snapshotDigest:string|null};previousDigest:string;digest:string}
export interface CollectionCase extends Entity{tenantId:string;invoiceId:string;invoiceDigest:string;assignedToUserId:string;nextReviewOn:string;status:CollectionStatus;steps:CollectionStep[];disputes:CollectionDispute[];closures:CollectionClosure[];events:CollectionEvent[];historyDigest:string}
export interface CollectionBalanceResponse{schema:string;caseId:string;caseRevision:number;status:CollectionStatus;ledger:CollectionLedger}
export interface CollectionWorklistItem{id:string;revision:number;invoiceId:string;invoiceNumber:string;buyerName:string;invoiceDueOn:string;assignedToUserId:string;nextReviewOn:string;status:CollectionStatus;updatedAt:string}
export interface CollectionWorklist extends Page<CollectionWorklistItem>{schema:string;capturedAt:string}

export const collectionStages:{value:CollectionStage;label:string}[]=[{value:'PaymentReminder',label:'Zahlungserinnerung'},{value:'FirstReminder',label:'Erste Mahnung'},{value:'SecondReminder',label:'Zweite Mahnung'}];
export const collectionStatuses:{value:CollectionStatus;label:string}[]=[{value:'Active',label:'Aktiv'},{value:'Disputed',label:'Streitfall'},{value:'Closed',label:'Geschlossen'}];
export const collectionChannels:{value:ContactChannel;label:string}[]=[{value:'Letter',label:'Brief'},{value:'Email',label:'E-Mail'},{value:'Phone',label:'Telefon'},{value:'Other',label:'Anderer dokumentierter Kontakt'}];
export const collectionEventNames:Record<string,string>={'opened':'Fall angelegt','step.approved':'Interner Entwurf freigegeben','step.withdrawn':'Unkontaktierter Entwurf zurückgenommen','contact.recorded':'Manuellen Kontakt dokumentiert','dispute.opened':'Streitfall eröffnet','dispute.resolved':'Streitfall ausdrücklich geklärt','review.changed':'Verantwortung oder Prüftermin geändert','closed':'Fall geschlossen','reopened':'Fall wiedereröffnet'};
const uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const digest=/^[a-f0-9]{64}$/i;
const exactMoney=/^\d+(?:\.\d{1,2})?$/;
export function collectionId(value:string):boolean{return uuid.test(value)&&value!=='00000000-0000-0000-0000-000000000000';}
export function collectionMoney(value:string):string{
 if(!exactMoney.test(value))return 'Betrag nicht verfügbar';
 const [whole,fraction='']=value.split('.');return whole.replace(/\B(?=(\d{3})+(?!\d))/g,'.')+','+fraction.padEnd(2,'0')+' €';
}
export function collectionDate(value:string):string{return /^\d{4}-\d{2}-\d{2}$/.test(value)?value.slice(8,10)+'.'+value.slice(5,7)+'.'+value.slice(0,4):'Datum nicht verfügbar';}
export function collectionState(value:CollectionStatus):string{return collectionStatuses.find(item=>item.value===value)?.label||'Unbekannter Stand';}
export function collectionStageName(value:CollectionStage):string{return collectionStages.find(item=>item.value===value)?.label||'Unbekannte Stufe';}
export function collectionContactName(value:ContactChannel):string{return collectionChannels.find(item=>item.value===value)?.label||'Unbekannter Kontakt';}
function require(condition:unknown):asserts condition{if(!condition)throw new Error('Der zurückgegebene Mahnfall passt nicht zum angefragten Stand. Bitte den Vorgang erneut öffnen.');}
export function checkCollectionPage(value:Page<unknown>,page:number,pageSize=25):void{require(value&&Array.isArray(value.items)&&value.page===page&&value.pageSize===pageSize&&value.items.length<=pageSize&&Number.isSafeInteger(value.total)&&value.total>=0);}
export function checkCollectionLedger(value:CollectionLedger,tenantId:string,invoiceId:string,invoiceDigest?:string):void{
 require(value&&value.version==='1.0.0'&&value.tenantId===tenantId&&value.invoiceId===invoiceId&&digest.test(value.invoiceDigest)&&(!invoiceDigest||value.invoiceDigest===invoiceDigest));
 require(value.invoice&&value.invoice.currency==='EUR'&&typeof value.invoice.number==='string'&&typeof value.invoice.buyerName==='string'&&/^\d{4}-\d{2}-\d{2}$/.test(value.asOf)&&digest.test(value.digest));
 require(value.balance&&['gross','credits','paid','discount','retained','outstanding','collectible','customerCredit'].every(key=>typeof value.balance[key as keyof CollectionBalance]==='string'&&exactMoney.test(value.balance[key as keyof CollectionBalance] as string))&&typeof value.balance.overdue==='boolean');
 require(Array.isArray(value.sources)&&value.sources.length>0&&value.sources.length<=1000&&value.sources.every(source=>(source.kind==='invoices'||source.kind==='payment-allocations')&&collectionId(source.id)&&Number.isSafeInteger(source.revision)&&source.revision>0&&digest.test(source.digest)));
}
export function checkCollectionCase(value:CollectionCase,tenantId:string,caseId?:string):void{
 require(value&&collectionId(value.id)&&(!caseId||value.id===caseId)&&value.tenantId===tenantId&&collectionId(value.invoiceId)&&Number.isSafeInteger(value.revision)&&value.revision>0&&digest.test(value.invoiceDigest)&&digest.test(value.historyDigest)&&collectionStatuses.some(item=>item.value===value.status));
 require(Array.isArray(value.steps)&&value.steps.length<=50&&Array.isArray(value.events)&&value.events.length>0&&value.events.length<=500&&Array.isArray(value.disputes)&&value.disputes.length<=100&&Array.isArray(value.closures)&&value.closures.length<=100);
 for(const step of value.steps){require(collectionId(step.id)&&digest.test(step.digest)&&step.preview?.caseId===value.id&&digest.test(step.preview.digest)&&typeof step.preview.text==='string'&&typeof step.preview.subject==='string');checkCollectionLedger(step.preview.ledger,tenantId,value.invoiceId,value.invoiceDigest);}
 for(const closure of value.closures)checkCollectionLedger(closure.ledger,tenantId,value.invoiceId,value.invoiceDigest);
}
export function checkCollectionPreview(value:CollectionPreview,entity:CollectionCase,tenantId:string,input:CollectionStepInput):void{
 require(value&&value.version==='1.0.0'&&value.caseId===entity.id&&value.caseRevision===entity.revision&&value.caseHistoryDigest===entity.historyDigest&&digest.test(value.digest)&&typeof value.subject==='string'&&typeof value.text==='string');
 require(value.input&&(['stage','respondBy','reason','additionalMessage'] as const).every(key=>value.input[key]===input[key]));checkCollectionLedger(value.ledger,tenantId,entity.invoiceId,entity.invoiceDigest);
}
