import {Component,DestroyRef,effect,inject,signal,untracked} from '@angular/core';
import {ApiService} from '../core/api.service';
import {AuthService} from '../core/auth.service';
import {ApiError,errorMessage} from '../core/transport';
import {downloadBlob,verifiedExport} from '../core/integrations.models';

@Component({
  selector:'app-self-copy',standalone:true,
  styles:[`:host{display:block}.copy-hash{display:block;overflow-wrap:anywhere;font-size:.78rem;line-height:1.5;margin-top:8px}.copy-scope{line-height:1.65}.copy-evidence{margin-top:18px}.copy-download{white-space:normal;max-width:100%}`],
  template:`
    <section class="panel" aria-labelledby="self-copy-heading">
      <p class="eyebrow">PERSÖNLICHES KONTO</p><h2 id="self-copy-heading">Meine Datenkopie</h2>
      <p class="copy-scope">Eigene Kontodaten und aktuell lesbare eigene Arbeitszeiten, Tagebucheinträge, Formularantworten und Benachrichtigungsregeln aus <strong>{{auth.tenant()?.name||'diesem Betrieb'}}</strong> herunterladen.</p>
      <p class="small muted copy-scope">Die Auswahl richtet sich nach Ihren aktuellen Rechten. Archivierte Einträge, Anhänge und weitere Geschäftsunterlagen sind nicht enthalten. Dies ist eine begrenzte Self-Service-Kopie. Für eine umfassende Auskunft wenden Sie sich an Ihre Betriebsverwaltung.</p>
      @if(error()){<div class="alert error" role="alert">{{error()}}</div>}
      <button type="button" class="button secondary copy-download" (click)="download()" [disabled]="busy()||!auth.scope()||auth.recoveryHold()">{{busy()?'Eigene Daten werden zusammengestellt …':'Eigene Daten als ZIP herunterladen'}}</button>
      @if(evidence();as item){<div class="copy-evidence" role="status"><p>Ihre Datenkopie wurde geprüft und zum Download bereitgestellt.</p><p class="small muted">{{item.name}} · {{size(item.bytes)}}</p><details><summary class="small">Prüfsumme der heruntergeladenen Datei</summary><code class="copy-hash">{{item.sha256}}</code></details></div>}
    </section>
  `
})
export class SelfCopyComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);
  readonly busy=signal(false);readonly error=signal('');readonly evidence=signal<{name:string;sha256:string;bytes:number}|null>(null);private generation=0;
  constructor(){
    inject(DestroyRef).onDestroy(()=>{this.generation++;});
    effect(()=>{this.auth.accessSignature();untracked(()=>{this.generation++;this.busy.set(false);this.error.set('');this.evidence.set(null);});});
  }
  private current(generation:number,access:string):boolean{return generation===this.generation&&access===this.auth.accessSignature();}
  size(value:number):string{return new Intl.NumberFormat('de-DE',{maximumFractionDigits:1}).format(value/1024)+' KB';}
  async download():Promise<void>{
    if(this.busy()||!this.auth.scope()||this.auth.recoveryHold())return;
    const generation=++this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');this.evidence.set(null);
    try{
      const response=await this.api.downloadResponse('self/data-copy');const verified=await verifiedExport(response,'firstblick.self-data-copy/1.0.0');
      if(!this.current(generation,access))return;
      const name='FirstBlick-meine-Daten-'+new Date().toISOString().slice(0,10)+'.zip';downloadBlob(verified.blob,name);this.evidence.set({name,sha256:verified.sha256,bytes:verified.blob.size});
    }catch(error){
      if(!this.current(generation,access))return;
      if(error instanceof ApiError&&error.problem.code==='data_copy_budget')this.error.set('Ihre eigenen Datensätze überschreiten den Umfang dieser Self-Service-Kopie. Bitte fragen Sie Ihre Betriebsverwaltung nach einer begleiteten Datenkopie. Es wurde kein unvollständiges Archiv heruntergeladen.');
      else if(error instanceof ApiError&&error.status===403)this.error.set('Der Zugriff auf diesen Betrieb besteht nicht mehr. Es wurde keine Datenkopie heruntergeladen.');
      else this.error.set(errorMessage(error));
    }finally{if(this.current(generation,access))this.busy.set(false);}
  }
}
