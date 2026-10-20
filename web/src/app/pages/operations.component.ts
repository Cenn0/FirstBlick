import { Component, OnDestroy, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../core/transport';

interface OperationsSnapshot {
  schema: 'firstblick.operations/1.0.0';
  capturedAt: string;
  queue: { pending: number; due: number; leased: number; expiredLeases: number; failed: number; uncertain: number; oldestDueAgeSeconds: number };
  documents: { ready: number; staged: number; stalled: number };
  thresholds: { dueAgeSeconds: number; stagedAgeSeconds: number };
  signals: { code: string; severity: string; affected: number }[];
}

@Component({
  selector: 'app-operations',
  imports: [RouterLink],
  template: `
    <section class="page-heading"><div><p class="eyebrow">Verwaltung</p><h1>Betriebsübersicht</h1><p>Hintergrundaufträge und Dateiübernahmen Ihres ausgewählten Betriebs.</p></div>
      @if(allowed()){<button class="button" type="button" (click)="refresh()" [disabled]="loading()">Aktualisieren</button>}
    </section>
    @if(!allowed()){
      <section class="card"><h2>Zugriff eingeschränkt</h2><p>Die Betriebsübersicht steht der Geschäftsführung zur Verfügung.</p></section>
    } @else {
      @if(error()){<p class="notice error" role="alert">{{error()}}</p>}
      @if(loading()){<p role="status">Betriebszustand wird geladen …</p>}
      @if(snapshot(); as value){
        <p class="muted" role="status">Datenstand: {{timestamp(value.capturedAt)}}. Aktualisieren Sie die Ansicht für einen neuen Stand.</p>
        <section class="card" aria-labelledby="operation-signals"><h2 id="operation-signals">Handlungsbedarf</h2>
          @if(value.signals.length){<ul class="signals">@for(item of value.signals;track item.code){<li [class.urgent]="item.severity==='action'"><strong>{{signalTitle(item.code)}}</strong><span>{{signalDescription(item.code)}} Betroffene Vorgänge: {{item.affected}}.</span></li>}</ul>}
          @else{<p>Die erfassten Vorgänge überschreiten aktuell keine der unten genannten Warnschwellen.</p>}
          <a routerLink="/management">Hintergrundaufträge und Klärungsfälle öffnen →</a>
        </section>
        <div class="operations-grid">
          <section class="card" aria-labelledby="operation-queue"><h2 id="operation-queue">Hintergrundaufträge</h2><dl>
            <div><dt>Zur Bearbeitung vorgemerkt</dt><dd>{{value.queue.pending}}</dd></div>
            <div><dt>Davon jetzt fällig</dt><dd>{{value.queue.due}}</dd></div>
            <div><dt>Zurzeit in Bearbeitung</dt><dd>{{value.queue.leased}}</dd></div>
            <div><dt>Abgelaufene Bearbeitungssperren</dt><dd>{{value.queue.expiredLeases}}</dd></div>
            <div><dt>Fehlgeschlagene Aufträge</dt><dd>{{value.queue.failed}}</dd></div>
            <div><dt>Unklares Versandresultat</dt><dd>{{value.queue.uncertain}}</dd></div>
            <div><dt>Wartezeit des ältesten fälligen Auftrags</dt><dd>{{duration(value.queue.oldestDueAgeSeconds)}}</dd></div>
          </dl><p class="muted">Ein unklarer Versand muss anhand des Zustellwegs geprüft werden. Eine automatische Wiederholung könnte doppelt zustellen.</p></section>
          <section class="card" aria-labelledby="operation-files"><h2 id="operation-files">Dateiübernahmen</h2><dl>
            <div><dt>Bereitgestellte Dateien</dt><dd>{{value.documents.ready}}</dd></div>
            <div><dt>Offene Dateiübernahmen</dt><dd>{{value.documents.staged}}</dd></div>
            <div><dt>Seit mindestens {{duration(value.thresholds.stagedAgeSeconds)}} offen</dt><dd>{{value.documents.stalled}}</dd></div>
          </dl><p class="muted">Eine offene Übernahme wird hier sichtbar, sobald ihre Anmeldung gespeichert ist.</p><a routerLink="/documents">Dokumente öffnen →</a></section>
        </div>
        <section class="card"><h2>Was diese Ansicht erfasst</h2><p>Die Warnung zur Warteschlange beginnt ab {{duration(value.thresholds.dueAgeSeconds)}}. Offene Dateiübernahmen werden ab {{duration(value.thresholds.stagedAgeSeconds)}} hervorgehoben. Fehlgeschlagene Aufträge enthalten auch bereits abschließend geklärte Fehler. Unklare Versandresultate benötigen eine belegte Klärung.</p>
          <p class="muted">Die Ansicht zeigt gespeicherte Vorgangszustände. Die Erreichbarkeit des Workers, des Dateispeichers und externer Zustelldienste sowie die Wiederherstellbarkeit von Sicherungen werden dadurch nicht geprüft.</p>
        </section>
      }
    }
  `,
  styles: [`
    :host{display:block}.operations-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1rem;margin:1rem 0}
    .card{margin-bottom:1rem}.signals{padding:0;list-style:none;display:grid;gap:.75rem}.signals li{display:grid;gap:.25rem;padding:.75rem;border-left:3px solid #b78328;background:var(--surface-alt,#f6f3ed)}
    .signals li.urgent{border-left-color:#ae3b36}dl{margin:0}dl>div{display:flex;justify-content:space-between;gap:1rem;padding:.65rem 0;border-bottom:1px solid var(--border,#dce2e5)}
    dt{overflow-wrap:anywhere}dd{margin:0;font-weight:650;flex-shrink:0}.muted{color:var(--muted,#54636b)}
    @media(max-width:760px){.operations-grid{grid-template-columns:1fr}.page-heading{align-items:flex-start;gap:.75rem;flex-wrap:wrap}}
  `]
})
export class OperationsComponent implements OnDestroy {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  readonly allowed = computed(() => this.auth.tenant()?.role === 'ManagingDirector' && !this.auth.recoveryHold());
  readonly snapshot = signal<OperationsSnapshot | null>(null);
  readonly error = signal('');
  readonly loading = signal(false);
  private generation = 0;

  constructor() {
    effect(() => {
      this.auth.accessSignature();
      const allowed = this.allowed();
      untracked(() => { this.generation++; this.snapshot.set(null); this.error.set(''); this.loading.set(false); if (allowed) void this.refresh(); });
    });
  }

  ngOnDestroy(): void { this.generation++; }

  async refresh(): Promise<void> {
    if (!this.allowed()) return;
    const generation = ++this.generation, access = this.auth.accessSignature();
    this.snapshot.set(null); this.error.set(''); this.loading.set(true);
    const current = () => generation === this.generation && access === this.auth.accessSignature() && this.allowed();
    try {
      const value = await this.api.get<OperationsSnapshot>('operations/snapshot');
      if (!current()) return;
      if (value?.schema !== 'firstblick.operations/1.0.0' || !Number.isFinite(Date.parse(value.capturedAt)) ||
          !value.queue || !value.documents || !value.thresholds || !Array.isArray(value.signals) ||
          [value.queue.pending, value.queue.due, value.queue.leased, value.queue.expiredLeases, value.queue.failed, value.queue.uncertain,
            value.documents.ready, value.documents.staged, value.documents.stalled, value.thresholds.dueAgeSeconds, value.thresholds.stagedAgeSeconds]
            .some(x => !Number.isSafeInteger(x) || x < 0) ||
          typeof value.queue.oldestDueAgeSeconds !== 'number' || !Number.isFinite(value.queue.oldestDueAgeSeconds) || value.queue.oldestDueAgeSeconds < 0 ||
          value.signals.some(x => typeof x?.code !== 'string' || typeof x.severity !== 'string' || !Number.isSafeInteger(x.affected) || x.affected < 0))
        throw new Error('Die Betriebsübersicht enthält unvollständige Daten. Bitte aktualisieren Sie den Vorgang.');
      this.snapshot.set(value);
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) this.loading.set(false); }
  }

  timestamp(value: string): string { return new Intl.DateTimeFormat('de-DE', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date(value)); }
  duration(seconds: number): string { return seconds < 60 ? `${Math.floor(seconds)} Sek.` : `${Math.floor(seconds / 60)} Min.`; }
  signalTitle(code: string): string { return ({ external_outcome_uncertain: 'Versandresultat klären', failed_jobs: 'Fehlgeschlagene Aufträge prüfen', expired_leases: 'Unterbrochene Bearbeitung prüfen', queue_delay: 'Verzögerte Warteschlange prüfen', stalled_uploads: 'Offene Dateiübernahmen prüfen' } as Record<string,string>)[code] || 'Vorgang prüfen'; }
  signalDescription(code: string): string { return ({ external_outcome_uncertain: 'Der Zustelldienst könnte den Auftrag bereits angenommen haben.', failed_jobs: 'Für diese Vorgänge wurde ein Fehler gespeichert.', expired_leases: 'Die vorgesehene Bearbeitungszeit ist abgelaufen.', queue_delay: 'Mindestens ein fälliger Auftrag wartet länger als die Warnschwelle.', stalled_uploads: 'Diese Dateiübernahmen sind länger als die Warnschwelle offen.' } as Record<string,string>)[code] || 'Der Server meldet einen zu prüfenden Vorgang.'; }
}
