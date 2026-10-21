import {Component,DestroyRef,computed,effect,inject,signal,untracked} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {ActivatedRoute,RouterLink} from '@angular/router';
import {toSignal} from '@angular/core/rxjs-interop';
import {ApiService,Page} from '../core/api.service';
import {AuthService} from '../core/auth.service';
import {moduleByKind} from '../core/modules';
import {ApiError,errorMessage} from '../core/transport';

interface SearchHit {kind:string;id:string;revision:number;title:string;number:string;updatedAt:string}
interface SearchResult extends Page<SearchHit> {schema:string;capturedAt:string}
interface HistoryEvent {id:number;occurredAt:string;action:string;kind:string;entityId:string;revision:number;title:string;performedByCurrentUser:boolean;contentDigest:string|null}
interface HistoryResult extends Page<HistoryEvent> {schema:string;projectId:string;associationScope:string;capturedAt:string}

const searchableKinds=['customers','buildings','projects','tasks','catalog-items','quotes','orders','change-orders','invoices','suppliers','purchase-orders','supplier-invoices',
  'materials','equipment','defects','acceptances','maintenance-contracts','form-definitions','roof-revisions','measurements','technical-results','documents'];
const uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;

@Component({
  imports:[FormsModule,RouterLink],templateUrl:'./discovery.component.html',
  styles:[`
    :host{display:block}.discovery-form{display:grid;grid-template-columns:minmax(0,1fr) minmax(180px,.45fr) auto;gap:16px;align-items:end}.discovery-form .form-field{min-width:0;margin:0}
    .discovery-list{list-style:none;padding:0;margin:0}.discovery-row{display:flex;align-items:flex-start;gap:16px;justify-content:space-between;padding:20px 0;border-bottom:1px solid var(--line,#e5e7eb)}.discovery-row:last-child{border-bottom:0}
    .discovery-main{min-width:0;flex:1}.discovery-title{display:block;font-size:1.05rem;font-weight:650;line-height:1.5;overflow-wrap:anywhere}.discovery-meta{display:flex;flex-wrap:wrap;gap:8px 16px;align-items:center;margin-top:8px;font-size:.84rem;color:var(--muted,#586671)}
    .discovery-actions{display:flex;flex-wrap:wrap;gap:12px;align-items:center;justify-content:flex-end}.discovery-actions a{white-space:nowrap}.discovery-note{margin-top:16px;max-width:82ch;line-height:1.6}
    .discovery-paging{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;padding-top:20px;border-top:1px solid var(--line,#e5e7eb)}.discovery-paging p{margin:0}
    .discovery-event{display:grid;grid-template-columns:180px minmax(0,1fr);gap:20px;width:100%}.discovery-event time{font-size:.86rem;color:var(--muted,#586671);line-height:1.7}.discovery-check{margin-top:10px;font-size:.82rem;color:var(--muted,#586671)}
    @media(max-width:760px){.discovery-form{grid-template-columns:1fr}.discovery-form .button{width:100%}.discovery-row{flex-direction:column;gap:12px}.discovery-actions{justify-content:flex-start}.discovery-event{grid-template-columns:1fr;gap:6px}.discovery-paging .button{padding:10px 12px}}
  `]
})
export class DiscoveryComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);private readonly route=inject(ActivatedRoute);
  private readonly routeQuery=toSignal(this.route.queryParamMap,{initialValue:this.route.snapshot.queryParamMap});
  readonly projectId=signal('');readonly busy=signal(false);readonly error=signal('');readonly searched=signal(false);
  readonly result=signal<SearchResult|null>(null);readonly history=signal<HistoryResult|null>(null);
  readonly canRead=computed(()=>this.auth.can('projects.read')&&!this.auth.recoveryHold());
  readonly categories=computed(()=>searchableKinds.filter(kind=>this.auth.can(`${kind}.read`)).map(kind=>({kind,title:this.category(kind)})));
  query='';kind='';private generation=0;private submitted:{query:string;kind:string}|null=null;

  constructor(){
    inject(DestroyRef).onDestroy(()=>{this.generation++;});
    effect(()=>{
      this.auth.accessSignature();const project=this.routeQuery().get('projectId')||'';
      untracked(()=>{this.reset();this.projectId.set(project);if(project){if(!uuid.test(project))this.error.set('Der Projektverweis ist ungültig. Öffnen Sie den Verlauf aus einer zugänglichen Projektakte.');else if(this.canRead())void this.loadHistory(1);}});
    });
  }

  private reset():void{this.generation++;this.query='';this.kind='';this.submitted=null;this.result.set(null);this.history.set(null);this.searched.set(false);this.error.set('');this.busy.set(false);}
  clearSearch():void{this.generation++;this.result.set(null);this.submitted=null;this.searched.set(false);this.error.set('');this.busy.set(false);}
  private current(generation:number,access:string):boolean{return generation===this.generation&&access===this.auth.accessSignature();}
  category(kind:string):string{return moduleByKind.get(kind)?.title||({documents:'Dokumente','technical-results':'Berechnungen'}as Record<string,string>)[kind]||'Datensatz';}
  routeFor(kind:string):string|null{return moduleByKind.has(kind)||kind==='documents'?'/'+kind:null;}
  when(value:string):string{const date=new Date(value);return Number.isFinite(date.getTime())?new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short'}).format(date):'Zeitpunkt nicht verfügbar';}
  private describe(error:unknown,history=false):string{
    if(error instanceof ApiError){
      if(error.status===403)return 'Für diese Ansicht besteht im ausgewählten Betrieb kein Zugriff. Ihre Berechtigung oder Zuordnung kann sich geändert haben.';
      if(error.status===404&&history)return 'Dieses Projekt ist nicht mehr verfügbar oder gehört nicht zu Ihren aktuell zugewiesenen Projekten.';
      if(error.problem.code==='discovery_policy_budget')return 'Die Berechtigungsgrundlagen sind für diese gemeinsame Ansicht zu umfangreich. Bitte wenden Sie sich an die Betriebsverwaltung.';
      if(error.problem.code==='history_budget')return 'Dieses Projekt hat zu viele verknüpfte Datensätze für den gemeinsamen Verlauf. Öffnen Sie die benötigten Unterlagen in der Projektakte.';
    }
    return errorMessage(error);
  }
  private checkPage(result:Page<unknown>,requested:number):void{
    if(!Array.isArray(result.items)||result.items.length>25||!Number.isSafeInteger(result.total)||result.total<0||result.page!==requested||result.pageSize!==25)
      throw new Error('Die Antwort konnte nicht als vollständige Ergebnisseite gelesen werden. Bitte erneut laden.');
  }
  async search():Promise<void>{
    if(!this.canRead()||this.busy())return;
    const query=this.query.trim();if(query.length<2||query.length>200){this.clearSearch();this.error.set('Geben Sie einen Suchbegriff mit 2 bis 200 Zeichen ein.');return;}
    if(this.kind&&!this.categories().some(x=>x.kind===this.kind)){this.clearSearch();this.error.set('Dieser Bereich ist mit Ihren aktuellen Rechten nicht verfügbar.');return;}
    this.submitted={query,kind:this.kind};await this.loadSearch(1);
  }
  async loadSearch(page:number):Promise<void>{
    if(!this.submitted||!this.canRead()||page<1||page>10000)return;
    const generation=++this.generation,access=this.auth.accessSignature(),request={...this.submitted};this.busy.set(true);this.error.set('');this.result.set(null);this.searched.set(true);
    try{
      const query=new URLSearchParams({q:request.query,page:String(page),pageSize:'25'});if(request.kind)query.set('kind',request.kind);
      const result=await this.api.get<SearchResult>(`search?${query}`);if(!this.current(generation,access))return;
      this.checkPage(result,page);if(result.schema!=='firstblick.search/1.0.0'||result.items.some(x=>!searchableKinds.includes(x.kind)||!uuid.test(x.id)))throw new Error('Das Suchergebnis enthält ein unbekanntes Format. Bitte erneut laden.');
      this.result.set(result);
    }catch(error){if(this.current(generation,access))this.error.set(this.describe(error));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  async loadHistory(page:number):Promise<void>{
    const project=this.projectId();if(!uuid.test(project)||!this.canRead()||page<1||page>10000)return;
    const generation=++this.generation,access=this.auth.accessSignature();this.busy.set(true);this.error.set('');this.history.set(null);
    try{
      const result=await this.api.get<HistoryResult>(`projects/${project}/history?${new URLSearchParams({page:String(page),pageSize:'25'})}`);if(!this.current(generation,access))return;
      this.checkPage(result,page);if(result.schema!=='firstblick.project-history/1.0.0'||result.projectId.toLowerCase()!==project.toLowerCase()||result.associationScope!=='current-accessible-project-links')
        throw new Error('Der Verlauf stimmt nicht mit dem geöffneten Projekt überein. Bitte erneut laden.');
      this.history.set(result);
    }catch(error){if(this.current(generation,access))this.error.set(this.describe(error,true));}finally{if(this.current(generation,access))this.busy.set(false);}
  }
  retry():void{if(this.projectId())void this.loadHistory(1);else if(this.submitted)void this.loadSearch(1);}
}
