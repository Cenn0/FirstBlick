import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
@Component({imports:[RouterLink],template:`<section class="empty-state"><p class="eyebrow">SEITE NICHT GEFUNDEN</p><h1>Hier geht es zur Übersicht.</h1><p>Die angeforderte Seite ist nicht vorhanden.</p><a class="button primary" routerLink="/overview">Übersicht öffnen</a></section>`})
export class NotFoundComponent {}
