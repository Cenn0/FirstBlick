import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';

interface CreatedBusiness { id:string; name:string; timeZone:string; modules:string[] }

@Component({
  imports:[FormsModule,RouterLink],
  template:`<header class="page-heading"><div><p class="eyebrow">BETRIEBSVERWALTUNG</p><h1>Neuen Betrieb anlegen</h1><p>Der neue Betrieb beginnt leer. Ihr persönliches Konto erhält dort die Rolle Geschäftsführung.</p></div></header>
    <section class="panel">
      @if(error()){<div class="alert error" role="alert">{{error()}}</div>}
      @if(createdId()){<div class="alert success" role="status">Der Betrieb wurde angelegt. Laden Sie Ihre Zugriffsübersicht erneut, falls der Wechsel noch nicht angezeigt wird.</div>}
      @if(canCreate()){
        <form #businessForm="ngForm" (ngSubmit)="create()">
          <div class="form-grid">
            <div class="form-field"><label for="business-name">Name des Betriebs</label><input id="business-name" name="name" [(ngModel)]="name" required maxlength="200" autocomplete="organization" [disabled]="busy()||!!createdId()"></div>
            <div class="form-field"><label for="business-timezone">Betriebszeitzone</label><select id="business-timezone" name="timeZone" [(ngModel)]="timeZone" [disabled]="busy()||!!createdId()"><option value="Europe/Berlin">Deutschland (Europe/Berlin)</option><option value="Europe/Vienna">Österreich (Europe/Vienna)</option><option value="Europe/Zurich">Schweiz (Europe/Zurich)</option><option value="UTC">UTC</option></select></div>
          </div>
          <p class="small muted">Daten, Mitglieder, Nummernkreise und Module werden für diesen Betrieb getrennt angelegt. Bestehende Betriebe bleiben unverändert.</p>
          <button class="button primary" [disabled]="businessForm.invalid||busy()||!!createdId()||uncertain()">{{busy()?'Wird angelegt …':'Betrieb anlegen'}}</button>
        </form>
      } @else {<p>Für die Anlage benötigen Sie eine aktuelle Verbindung und die Rolle Geschäftsführung in einem aktiven Betrieb.</p>}
      @if(createdId()){<button class="button" [disabled]="busy()" (click)="finish()">Zugriff neu laden und Betrieb öffnen</button>}
      <p><a routerLink="/settings">Zurück zu Einstellungen</a></p>
    </section>`
})
export class BusinessCreationComponent {
  private readonly auth=inject(AuthService);
  private readonly api=inject(ApiService);
  private readonly router=inject(Router);
  readonly busy=signal(false);
  readonly error=signal('');
  readonly createdId=signal('');
  readonly uncertain=signal(false);
  readonly canCreate=computed(()=>this.auth.session()?.canCreateBusiness===true&&this.auth.tenant()?.role==='ManagingDirector'&&!this.auth.recoveryHold()&&!this.auth.offlineSession());
  name='';timeZone='Europe/Berlin';

  async create():Promise<void>{
    if(!this.canCreate()||this.busy()||this.createdId()||this.uncertain())return;
    const sourceScope=this.auth.scope();
    const requestedName=this.name.trim();
    this.busy.set(true);this.error.set('');
    try{
      const created=await this.api.command<CreatedBusiness>('businesses',{name:requestedName,timeZone:this.timeZone});
      if(!created||!/^[-a-f0-9]{36}$/i.test(created.id)||created.name!==requestedName||created.timeZone!==this.timeZone||!Array.isArray(created.modules)||!created.modules.includes('core')){
        this.uncertain.set(true);
        throw new Error('Der Anlagebeleg ist unvollständig. Laden Sie Ihre Betriebe neu, bevor Sie erneut anlegen.');
      }
      this.createdId.set(created.id);
      if(sourceScope!==this.auth.scope()){this.error.set('Der Betrieb wurde im vorherigen Zugang angelegt. Laden Sie Ihre Zugriffsübersicht neu.');return;}
      await this.finish();
    }catch(error){
      if(sourceScope!==this.auth.scope()){
        this.uncertain.set(true);
        this.error.set('Der Ausgangsbetrieb wurde während der Anlage gewechselt. Laden Sie Ihre Zugriffsübersicht neu, bevor Sie weitere Betriebe anlegen.');
      }else this.error.set(errorMessage(error));
    }
    finally{this.busy.set(false);}
  }

  async finish():Promise<void>{
    const id=this.createdId();if(!id||this.busy()&&this.error())return;
    try{
      await this.auth.refresh();
      if(!this.auth.session()?.tenants.some(tenant=>tenant.id===id))throw new Error('Der neue Betrieb ist in der aktuellen Sitzung noch nicht sichtbar. Laden Sie die Zugriffe erneut.');
      await this.auth.switchTenant(id);
      await this.router.navigateByUrl('/overview');
    }catch(error){this.error.set(errorMessage(error));}
  }
}
