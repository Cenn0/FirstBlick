import { Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { RecordChoice, RecordChoicePage, validRecordChoices } from '../core/ledger-contracts';
import { errorMessage } from '../core/transport';

@Component({
  selector: 'app-ledger-picker', imports: [FormsModule],
  template: `<div class="record-picker"><p class="small"><strong>{{label()}}</strong>@if(selection();as current){: {{current.name}} <button class="text-button" type="button" [disabled]="disabled()" (click)="chosen.emit(null)">{{label()}} entfernen</button>}@else{ · {{required()?'Bitte auswählen':'Optional'}}}</p>
    <div class="picker-search"><input type="search" [attr.aria-label]="label()+' suchen'" [(ngModel)]="search" [ngModelOptions]="{standalone:true}" [disabled]="disabled()||loading()" maxlength="200" (keydown.enter)="$event.preventDefault();find(1)"><button class="button secondary" type="button" [disabled]="disabled()||loading()" (click)="find(1)">{{label()}} suchen</button></div>
    @if(error()){<p role="alert" class="alert error">{{error()}}</p>}@if(loading()){<p role="status">{{label()}} werden gesucht …</p>}
    @if(results();as result){<ul>@for(row of result.items;track row.id){<li><button class="record-option" type="button" [disabled]="disabled()||row.active===false" (click)="select(row)">{{row.name}}@if(row.number){ · {{row.number}}}@if(row.active===false){ · inaktiv}</button></li>}@empty{<li class="muted">Keine passenden Einträge.</li>}</ul><div class="picker-pages"><button class="text-button" type="button" [disabled]="disabled()||loading()||result.page<=1" (click)="find(result.page-1)">Vorherige {{label()}}</button><span>Seite {{result.page}} · {{result.total}} Einträge</span><button class="text-button" type="button" [disabled]="disabled()||loading()||result.page*result.pageSize>=result.total" (click)="find(result.page+1)">Weitere {{label()}}</button></div>}
  </div>`,
  styles: [':host{display:block;min-width:0}.record-picker{border:1px solid var(--line,#ddd);border-radius:.5rem;padding:.8rem}.picker-search,.picker-pages{display:flex;gap:.6rem;align-items:center;flex-wrap:wrap}.picker-search input{flex:1;min-width:140px}.picker-pages{font-size:.75rem;justify-content:space-between}ul{list-style:none;padding:0;margin:.5rem 0}.record-option{display:block;border:0;border-bottom:1px solid var(--line,#ddd);background:transparent;padding:.7rem 0;text-align:left;width:100%;overflow-wrap:anywhere}.record-picker p{margin-bottom:.5rem}']
})
export class LedgerPickerComponent {
  readonly kind = input.required<'projects' | 'employees'>(); readonly label = input.required<string>();
  readonly customerId = input<string>(); readonly selection = input<RecordChoice | null>(null); readonly disabled = input(false); readonly required = input(false);
  readonly chosen = output<RecordChoice | null>(); readonly results = signal<RecordChoicePage | null>(null); readonly loading = signal(false); readonly error = signal('');
  private readonly auth = inject(AuthService); private readonly api = inject(ApiService); private generation = 0; search = '';
  constructor() {
    effect(() => { this.kind(); this.customerId(); this.auth.accessSignature(); untracked(() => { this.generation++; this.results.set(null); this.search = ''; this.error.set(''); this.loading.set(false); }); });
    inject(DestroyRef).onDestroy(() => { this.generation++; });
  }
  async find(page: number): Promise<void> {
    if (this.disabled() || this.loading()) return;
    const generation = ++this.generation, access = this.auth.accessSignature(), kind = this.kind(), customerId = this.customerId();
    const current = () => generation === this.generation && access === this.auth.accessSignature();
    this.loading.set(true); this.error.set(''); this.results.set(null);
    try {
      const value = await this.api.list(kind, this.search.trim(), page, '', customerId ? { customerId } : {});
      if (current()) this.results.set(validRecordChoices(value, kind, page, customerId));
    } catch (error) { if (current()) this.error.set(errorMessage(error)); }
    finally { if (current()) this.loading.set(false); }
  }
  select(row: RecordChoice): void { if (!this.disabled() && row.active !== false) { this.chosen.emit(row); this.results.set(null); } }
}
