import {Component,input} from '@angular/core';
import {RouterLink} from '@angular/router';
import {FrozenCommercialSnapshot,InvoiceCalculation,InvoiceCalculatedLine,InvoicePositionCalculation} from '../core/invoice.models';
@Component({selector:'app-invoice-evidence',imports:[RouterLink],templateUrl:'./invoice-evidence.component.html'})
export class InvoiceEvidenceComponent{
 readonly value=input<InvoiceCalculation|null>(null);readonly snapshot=input<FrozenCommercialSnapshot|null>(null);
 money(value:string|undefined):string{return value===undefined?'Nicht vorhanden':new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR'}).format(Number(value));}
 quantity(value:string|undefined):string{if(value===undefined)return 'Nicht vorhanden';const parts=String(value).split('.'),fraction=(parts[1]||'').replace(/0+$/,'');return parts[0].replace(/\B(?=(\d{3})+(?!\d))/g,'.')+(fraction?','+fraction:'');}
 actual(line:InvoiceCalculatedLine){return this.snapshot()?.lines[line.lineIndex];}
 source(position:InvoicePositionCalculation){return this.value()?.sources[position.sourceIndex];}
 original(position:InvoicePositionCalculation){return this.source(position)?.snapshot.lines[position.positionIndex];}
 kind(value:string):string{return ({Final:'Schlussrechnung',Progress:'Teilrechnung',CreditNote:'Korrekturrechnung'}as Record<string,string>)[value]||value;}
}
