import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ApiService, Entity, Page } from '../core/api.service';
import { errorMessage } from '../core/transport';
import { SelfCopyComponent } from './self-copy.component';

@Component({imports:[FormsModule,RouterLink,SelfCopyComponent],template:`<header class="page-heading"><div><p class="eyebrow">IHR ARBEITSPLATZ</p><h1>Einstellungen</h1><p>Persönliche Benachrichtigungen und Zugriffsübersicht.</p></div></header>@if(error()){<div class="alert error" role="alert">{{error()}}</div>}@if(notice()){<div class="alert success" role="status">{{notice()}}</div>}<div class="settings-grid"><section class="panel"><p class="eyebrow">PERSÖNLICH</p><h2>Mein Morgenbriefing</h2>@if(canReadPreferences()){<p class="muted">Wählen Sie Zeitpunkt, Wochentage und gewünschte Inhalte.</p><form #preferencesForm="ngForm" (ngSubmit)="save()"><fieldset class="preferences-fields" [disabled]="!canWritePreferences()||busy()||loading()"><legend class="sr-only">Benachrichtigungswünsche</legend><label class="checkbox-label"><input name="briefing" type="checkbox" [(ngModel)]="morningBriefing">Morgenbriefing aktivieren</label><div class="form-grid"><div class="form-field"><label for="briefing-time">Uhrzeit</label><input id="briefing-time" name="time" type="time" [(ngModel)]="localTime" required></div><div class="form-field"><label for="timezone">Zeitzone</label><select id="timezone" name="timezone" [(ngModel)]="timeZone"><option value="Europe/Berlin">Deutschland (Europe/Berlin)</option><option value="Europe/Vienna">Österreich (Europe/Vienna)</option><option value="Europe/Zurich">Schweiz (Europe/Zurich)</option><option value="UTC">UTC</option></select></div></div><fieldset class="checkbox-group"><legend>Wochentage</legend>@for(day of weekdays;track day.value){<label class="checkbox-label"><input type="checkbox" [name]="day.value" [ngModel]="days.includes(day.value)" (ngModelChange)="toggle(days,day.value,$event)">{{day.label}}</label>}</fieldset><fieldset class="checkbox-group"><legend>Inhalte</legend><label class="checkbox-label"><input type="checkbox" name="yesterday" [(ngModel)]="includeYesterday">Rückblick auf gestern</label><label class="checkbox-label"><input type="checkbox" name="today" [(ngModel)]="includeToday">Heutige Aufgaben und Einsätze</label><label class="checkbox-label"><input type="checkbox" name="maintenance" [(ngModel)]="includeMaintenance">Anstehende Wartungen</label></fieldset><fieldset class="checkbox-group"><legend>Gewünschte Kanäle</legend>@for(channel of availableChannels;track channel.value){<label class="checkbox-label"><input type="checkbox" [name]="channel.value" [ngModel]="channels.includes(channel.value)" (ngModelChange)="toggle(channels,channel.value,$event)">{{channel.label}}</label>}</fieldset><p class="small muted">Eine Auswahl speichert Ihren Wunsch. Die Zustellung setzt eine eingerichtete Verbindung und gültige Empfängerangaben im Betrieb voraus.</p><button class="button primary" [disabled]="busy()||loading()||!canWritePreferences()||preferencesForm.invalid">{{busy()?'Wird gespeichert …':'Benachrichtigungen speichern'}}</button></fieldset></form>}@else{<p class="muted">Persönliche Benachrichtigungen sind mit Ihrem aktuellen Betriebszugang nicht verfügbar.</p>}</section><div><section class="panel"><p class="eyebrow">BETRIEB</p><h2>{{auth.tenant()?.name}}</h2><dl class="result-list"><div><dt>Persönliches Konto</dt><dd>{{auth.session()?.user.displayName}}</dd></div><div><dt>E-Mail</dt><dd>{{auth.session()?.user.email}}</dd></div><div><dt>Rolle im Betrieb</dt><dd>{{roleName(auth.tenant()?.role||'')}}</dd></div></dl><h3>Aktive Module</h3><div class="tag-list">@for(module of auth.tenant()?.modules;track module){<span class="badge">{{moduleName(module)}}</span>}</div><p class="small muted">Ihre Rechte werden bei jeder Anfrage im Betrieb geprüft.</p>@if(auth.tenant()?.role==='ManagingDirector'&&!auth.recoveryHold()&&!auth.offlineSession()){<p><a routerLink="/businesses/new">Weiteren Betrieb anlegen</a></p>}</section><section class="panel security-info"><h2>Auf diesem Gerät</h2><p>Abmelden entfernt die lokale Sitzungsübersicht und vorgemerkte Arbeitsdaten. Prüfen Sie die Synchronisierung, bevor Sie sich abmelden.</p><p class="small muted">Anmeldung und Betriebszugang sind voneinander getrennt. Ein persönliches Konto kann für mehrere Betriebe unterschiedliche Rechte haben.</p></section><app-self-copy></app-self-copy></div></div>`})
export class SettingsComponent {
  readonly auth=inject(AuthService);private readonly api=inject(ApiService);
  readonly busy=signal(false);readonly loading=signal(false);readonly error=signal('');readonly notice=signal('');
  private existing?:Entity;private loadVersion=0;
  morningBriefing=false;localTime='07:00';timeZone='Europe/Berlin';includeYesterday=true;includeToday=true;includeMaintenance=true;
  channels:string[]=[];days=['Monday','Tuesday','Wednesday','Thursday','Friday'];
  readonly weekdays=[{value:'Monday',label:'Mo'},{value:'Tuesday',label:'Di'},{value:'Wednesday',label:'Mi'},{value:'Thursday',label:'Do'},{value:'Friday',label:'Fr'},{value:'Saturday',label:'Sa'},{value:'Sunday',label:'So'}];
  readonly availableChannels=[{value:'Push',label:'Push'},{value:'Email',label:'E-Mail'},{value:'WhatsApp',label:'WhatsApp'}];
  constructor(){effect(()=>{this.auth.scope();this.canReadPreferences();this.canWritePreferences();untracked(()=>{this.reset();void this.load();});});}
  canReadPreferences():boolean{return this.auth.hasModule('maintenance')&&this.auth.can('notification-preferences.read');}
  canWritePreferences():boolean{return this.canReadPreferences()&&this.auth.can('notification-preferences.write');}
  moduleName(value:string):string{return ({training:'Ausbildung',maintenance:'Wartung & Benachrichtigungen',finance:'Finanzen',commercial:'Angebote & Aufträge',purchasing:'Einkauf & Material',field:'Baustelle & Team',technical:'Dach & Fachberechnungen',core:'Betrieb',portal:'Partnerportal'}as Record<string,string>)[value.toLowerCase()]||value;}
  roleName(value:string):string{return ({ManagingDirector:'Geschäftsführung',Office:'Büro',Accounting:'Buchhaltung',SiteManager:'Bauleitung',Worker:'Mitarbeiter',Trainer:'Ausbilder',Partner:'Partner'}as Record<string,string>)[value]||value;}
  toggle(list:string[],value:string,selected:boolean):void{const index=list.indexOf(value);if(selected&&index<0)list.push(value);if(!selected&&index>=0)list.splice(index,1);}
  private reset():void{this.loadVersion++;this.existing=undefined;this.morningBriefing=false;this.localTime='07:00';this.timeZone='Europe/Berlin';this.includeYesterday=true;this.includeToday=true;this.includeMaintenance=true;this.channels=[];this.days=['Monday','Tuesday','Wednesday','Thursday','Friday'];this.busy.set(false);this.loading.set(false);this.error.set('');this.notice.set('');}
  async load():Promise<void>{
    if(!this.auth.scope()||!this.canReadPreferences())return;
    const scope=this.auth.scope(),version=++this.loadVersion,userId=this.auth.session()?.user.id;this.loading.set(true);
    try{
      const page=await this.api.get<Page>('notification-preferences?pageSize=100');
      if(scope!==this.auth.scope()||version!==this.loadVersion)return;
      this.existing=page.items.find(item=>item['userId']===userId);
      if(this.existing){const x=this.existing;this.morningBriefing=Boolean(x['morningBriefing']);this.localTime=String(x['localTime']||'07:00').slice(0,5);this.timeZone=String(x['timeZone']||'Europe/Berlin');this.channels=[...(x['channels']as string[]||[])];this.days=[...(x['days']as string[]||[])];this.includeYesterday=Boolean(x['includeYesterday']);this.includeToday=Boolean(x['includeToday']);this.includeMaintenance=Boolean(x['includeMaintenance']);}
    }catch(error){if(scope===this.auth.scope()&&version===this.loadVersion)this.error.set(errorMessage(error));}
    finally{if(scope===this.auth.scope()&&version===this.loadVersion)this.loading.set(false);}
  }
  async save():Promise<void>{
    if(!this.canWritePreferences()||this.busy()||this.loading())return;
    const scope=this.auth.scope();this.busy.set(true);this.error.set('');
    try{
      await this.api.save('notification-preferences',{userId:this.auth.session()!.user.id,morningBriefing:this.morningBriefing,localTime:this.localTime.slice(0,5)+':00',timeZone:this.timeZone,channels:[...this.channels],days:[...this.days],includeYesterday:this.includeYesterday,includeToday:this.includeToday,includeMaintenance:this.includeMaintenance},this.existing);
      if(scope!==this.auth.scope())return;this.notice.set('Ihre Benachrichtigungswünsche sind gespeichert.');await this.load();
    }catch(error){if(scope===this.auth.scope())this.error.set(errorMessage(error));}
    finally{if(scope===this.auth.scope())this.busy.set(false);}
  }
}
