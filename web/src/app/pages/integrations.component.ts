import {Component,DestroyRef,computed,effect,inject,signal,untracked} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {RouterLink} from '@angular/router';
import {AuthService} from '../core/auth.service';
import {ApiService} from '../core/api.service';
import {ApiError,errorMessage} from '../core/transport';
import {BankCsvPreview,BankImportReceipt,ExportDefinition,downloadBlob,exportDefinitions,inclusiveDateRange,sha256Blob,verifiedExport} from '../core/integrations.models';

@Component({imports:[FormsModule,RouterLink],templateUrl:'./integrations.component.html'})
export class IntegrationsComponent{
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);
  readonly tab=signal<'bank'|'exports'>('exports');readonly busy=signal(false);readonly error=signal('');readonly notice=signal('');readonly file=signal<File|null>(null);readonly preview=signal<BankCsvPreview|null>(null);readonly receipt=signal<BankImportReceipt|null>(null);readonly exportEvidence=signal<{title:string;schema:string;sha256:string;from:string;to:string}|null>(null);
  readonly canImport=computed(()=>this.auth.hasModule('finance')&&['ManagingDirector','Accounting'].includes(this.auth.tenant()?.role||'')&&this.auth.can('bank-transactions.write'));
  readonly exports=computed(()=>exportDefinitions.filter(definition=>definition.kind==='payroll-time'?['ManagingDirector','Office'].includes(this.auth.tenant()?.role||'')&&this.auth.can('time-entries.read'):['ManagingDirector','Office','Accounting'].includes(this.auth.tenant()?.role||'')&&this.auth.can('invoices.read')));
  readonly allowed=computed(()=>this.canImport()||this.exports().length>0);
  consent=false;previewPage=1;from=new Date().toISOString().slice(0,8)+'01';to=new Date().toISOString().slice(0,10);private generation=0;private rawFile:File|null=null;private previewFingerprint='';
  constructor(){inject(DestroyRef).onDestroy(()=>{this.generation++;this.rawFile=null;});effect(()=>{this.auth.accessSignature();untracked(()=>{this.generation++;this.clearFile();this.error.set('');this.notice.set('');this.exportEvidence.set(null);this.busy.set(false);if(!this.canImport())this.tab.set('exports');});});}
  private current(generation:number,access:string):boolean{return generation===this.generation&&access===this.auth.accessSignature();}
  chooseTab(tab:'bank'|'exports'):void{this.tab.set(tab);this.error.set('');this.notice.set('');}
  clearFile():void{this.file.set(null);this.rawFile=null;this.preview.set(null);this.receipt.set(null);this.previewFingerprint='';this.consent=false;this.previewPage=1;}
  chooseFile(event:Event):void{const input=event.target as HTMLInputElement,chosen=input.files?.[0];input.value='';this.clearFile();this.error.set('');this.notice.set('');if(!chosen)return;if(chosen.size===0||chosen.size>2*1024*1024){this.error.set('Bitte eine CSV-Datei mit 1 Byte bis höchstens 2 MiB auswählen.');return;}this.rawFile=chosen;this.file.set(chosen);}
  rows(){return this.preview()?.rows.slice((this.previewPage-1)*25,this.previewPage*25)||[];}
  pages():number{return Math.max(1,Math.ceil((this.preview()?.rows.length||0)/25));}
  state(value:string):string{return {New:'Neu',Reused:'Bereits vorhanden',Conflict:'Abweichung klären'}[value]||value;}
  money(value:string):string{return new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR'}).format(Number(value));}
  date(value:string):string{return /^\d{4}-\d{2}-\d{2}$/.test(value)?value.split('-').reverse().join('.'):value;}
  private describe(error:unknown):string{if(error instanceof ApiError){if(error.problem.code==='preview_mismatch')return 'Die CSV-Datei stimmt nicht mehr mit der Vorschau überein. Bitte die Datei neu prüfen.';if(['csv_header','csv_columns'].includes(error.problem.code||''))return 'Die CSV-Spalten stimmen nicht mit dem FirstBlick-Bankformat überein. Verwenden Sie die Vorlage und genau die dort angegebene Reihenfolge.';if(error.problem.code==='csv_amount')return 'Ein Betrag ist ungültig. Verwenden Sie einen von null verschiedenen EUR-Betrag mit Dezimalpunkt und genau zwei Nachkommastellen; Ausgaben erhalten ein Minuszeichen. '+(error.problem.detail||'');}return errorMessage(error);}
  async checkFile():Promise<void>{const file=this.rawFile;if(!file||!this.canImport()||this.busy())return;const generation=++this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');this.notice.set('');this.preview.set(null);this.receipt.set(null);this.consent=false;
    try{const hash=await sha256Blob(file);if(!this.current(generation,access)||file!==this.rawFile)return;const result=await this.api.previewCsv<BankCsvPreview>(file);if(!this.current(generation,access)||file!==this.rawFile)return;if(result.schema!=='firstblick.bank-csv/1.0.0'||result.sourceSha256!==hash)throw new Error('Die Vorschau ist nicht eindeutig an die ausgewählte Datei gebunden. Bitte erneut prüfen.');this.previewFingerprint=hash;this.preview.set(result);this.previewPage=1;}
    catch(error){if(this.current(generation,access))this.error.set(this.describe(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  async importFile():Promise<void>{const file=this.rawFile,preview=this.preview();if(!file||!preview||!preview.canImport||!this.consent||!this.canImport()||this.busy())return;const generation=this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');
    try{if(await sha256Blob(file)!==this.previewFingerprint)throw new Error('Die Datei wurde nach der Vorschau geändert. Bitte erneut prüfen.');if(!this.current(generation,access)||file!==this.rawFile)return;const result=await this.api.importCsv<BankImportReceipt>(file,this.previewFingerprint);if(!this.current(generation,access))return;if(result.event.sourceSha256!==this.previewFingerprint)throw new Error('Der Importbeleg enthält eine abweichende Dateiprüfsumme. Bitte den Import im Betrieb prüfen.');this.receipt.set(result);this.consent=false;this.notice.set('Der Import wurde im Betrieb gespeichert. Die Bankumsätze können jetzt zugeordnet werden.');}
    catch(error){if(this.current(generation,access))this.error.set(this.describe(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  async exportPackage(definition:ExportDefinition):Promise<void>{if(!this.exports().some(item=>item.kind===definition.kind)||this.busy())return;const generation=this.generation,access=this.auth.accessSignature(),from=this.from,to=this.to;this.busy.set(true);this.error.set('');this.notice.set('');this.exportEvidence.set(null);
    try{inclusiveDateRange(from,to);const response=await this.api.downloadResponse(`integrations/v1/exports/${definition.kind}?${new URLSearchParams({from,to})}`);const verified=await verifiedExport(response,definition.schema);if(!this.current(generation,access))return;downloadBlob(verified.blob,`FirstBlick-${definition.kind}-v1-${from.replaceAll('-','')}-${to.replaceAll('-','')}.zip`);this.exportEvidence.set({title:definition.title,schema:definition.schema,sha256:verified.sha256,from,to});this.notice.set('Exportpaket geprüft und zum Download bereitgestellt.');}
    catch(error){if(this.current(generation,access))this.error.set(this.describe(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  template():void{downloadBlob(new Blob(['account_reference,external_id,booked_on,amount_eur,currency,counterparty,reference\r\nsynthetic-account,synthetic-booking-001,2026-10-20,119.00,EUR,Testkunde,Testbuchung\r\n'],{type:'text/csv;charset=utf-8'}),'FirstBlick-Bankformat-v1-Beispiel.csv');}
}
