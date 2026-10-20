import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';

@Component({
  imports:[FormsModule,RouterLink],
  template:`<div class="login-layout"><section class="login-brand"><a class="brand" href="/"><img src="/icon.svg" width="44" height="44" alt=""><span>First<span class="brand-accent">Blick</span></span></a><div><p class="eyebrow">FÜR DEN ALLTAG IM HANDWERK</p><h1>Gute Arbeit.<br>Alles im Blick.</h1><p>Projekte, Baustelle und Büro an einem Ort.<br>Damit mehr Zeit für das Wesentliche bleibt.</p></div><span>Von der ersten Anfrage bis zum fertigen Dach.</span></section><section class="login-form-panel"><form class="login-form" (ngSubmit)="login()"><p class="eyebrow">WILLKOMMEN ZURÜCK</p><h2>Bei FirstBlick anmelden</h2><p class="muted">Melden Sie sich mit Ihrem persönlichen Konto an.</p>@if(error() || auth.bootstrapError()){<div class="alert error" role="alert">{{error() || auth.bootstrapError()}}</div>}<label for="email">E-Mail-Adresse</label><input id="email" name="email" type="email" [(ngModel)]="email" required autocomplete="username" maxlength="254" autofocus><label for="password">Passwort</label><input id="password" name="password" type="password" [(ngModel)]="password" required autocomplete="current-password" minlength="1" maxlength="256"><button class="button primary full-width" [disabled]="busy()">{{busy() ? 'Anmeldung läuft …' : 'Anmelden'}}<span aria-hidden="true">→</span></button><div class="account-links"><a routerLink="/recover">Passwort vergessen?</a><a routerLink="/activate">Einladung annehmen</a></div><p class="small muted">Noch kein Zugang? Ihre Betriebsverwaltung kann Sie persönlich einladen.</p></form></section></div>`
})
export class LoginComponent {
  readonly auth=inject(AuthService);private readonly router=inject(Router);
  email='';password='';readonly error=signal('');readonly busy=signal(false);
  async login():Promise<void>{this.busy.set(true);this.error.set('');try{await this.auth.login(this.email.trim(),this.password);this.password='';await this.router.navigateByUrl('/overview');}catch(error){this.error.set(errorMessage(error));}finally{this.busy.set(false);}}
}
