import { Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { QueuedMutation } from '../core/offline-store';
import { moduleByKind } from '../core/modules';

@Component({imports:[RouterLink],template:`<header class="page-heading"><div><p class="eyebrow">AUF DER BAUSTELLE</p><h1>Synchronisierung</h1><p>Lokale Zeit- und Tagesberichte für {{auth.tenant()?.name}}.</p></div><button class="button primary" [disabled]="api.syncing()" (click)="api.synchronize()">{{api.syncing()?'Wird synchronisiert …':'Jetzt synchronisieren'}}</button></header><div class="alert information">Vorgemerkte Einträge bleiben nach einem Neustart erhalten. Erst nach erneuter Anmeldung und Berechtigungsprüfung werden sie übertragen. Bei einer Abmeldung werden lokale Arbeitsdaten entfernt.</div>@if(notice()){<div class="alert success" role="status">{{notice()}}</div>}<section class="panel">@for(item of api.queued();track item.key){<article class="queue-item"><div class="queue-heading"><div><span class="badge" [attr.data-status]="item.state">{{stateLabel(item)}}</span><h2>{{title(item)}}</h2><p class="muted small">{{date(item.createdAt)}} · {{item.method==='POST'?'Neuer Eintrag':'Änderung'}}</p></div><a class="button secondary" [routerLink]="'/'+item.kind">Bereich öffnen</a></div><dl class="detail-grid">@for(entry of fields(item);track entry.key){<div><dt>{{entry.label}}</dt><dd>{{entry.value}}</dd></div>}</dl>@if(item.error){<div class="alert warning">{{item.error}}</div>}<div class="detail-actions"><button class="text-button" (click)="copy(item)">Angaben kopieren</button><button class="text-button danger-text" (click)="discard(item)">Lokalen Vorgang verwerfen</button></div></article>}@empty{<div class="empty-state"><span class="empty-icon" aria-hidden="true">✓</span><h2>Alles übertragen.</h2><p>Für diesen Betrieb sind auf diesem Gerät keine lokalen Vorgänge vorgemerkt.</p><a class="button secondary" routerLink="/time-entries">Arbeitszeit erfassen</a></div>}</section>`})
export class OfflineComponent {
  readonly api=inject(ApiService);readonly auth=inject(AuthService);readonly notice=signal('');
  constructor(){effect(()=>{this.auth.scope();untracked(()=>void this.api.reloadQueue());});}
  title(item:QueuedMutation):string{return String(item.body['activity']||item.body['work']||moduleByKind.get(item.kind)?.singular||'Eintrag').slice(0,120);}
  stateLabel(item:QueuedMutation):string{return {pending:'Vorgemerkt',conflict:'Änderungskonflikt',blocked:'Klärung erforderlich'}[item.state];}
  date(value:string):string{return new Intl.DateTimeFormat('de-DE',{dateStyle:'medium',timeStyle:'short'}).format(new Date(value));}
  fields(item:QueuedMutation):{key:string;label:string;value:string}[]{return (moduleByKind.get(item.kind)?.fields||[]).filter(f=>!['relation','breaks'].includes(f.type||'')).map(f=>({key:f.key,label:f.label,value:String(item.body[f.key]??'–')}));}
  async discard(item:QueuedMutation):Promise<void>{if(!window.confirm('Diesen lokalen Vorgang dauerhaft verwerfen? Noch nicht übertragene Angaben gehen verloren.'))return;await this.auth.store.remove(item.key);await this.api.reloadQueue();this.notice.set('Lokaler Vorgang entfernt.');}
  async copy(item:QueuedMutation):Promise<void>{await navigator.clipboard.writeText(this.fields(item).map(f=>`${f.label}: ${f.value}`).join('\n'));this.notice.set('Angaben in die Zwischenablage kopiert.');}
}
