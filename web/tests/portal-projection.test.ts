import {describe,expect,it} from 'vitest';
import {portalQuotePreview} from '../src/app/core/portal.models';

function issuedQuote(){return {id:'quote-a',revision:7,tenantId:'tenant-internal',customerId:'customer-internal',number:'AN-2026-1',title:'Freigegebene Dachleistung',version:2,status:'Issued',validUntil:null,issuedDigest:'issued-evidence',currency:'EUR',presentation:{sellerName:'Dachbetrieb',buyerName:'Kunde',buyerAddress:'Objektanschrift',issuedAt:'2026-10-20T08:00:00Z',pricingDigest:'pricing-evidence'},snapshot:{net:'9007199254740993.10',tax:'19.00',gross:'9007199254741012.10',digest:'pricing-evidence',lines:[{inputs:{description:'Dachleistung',unit:'m²',quantity:'10.123456789012',vatPercent:'19',unitMaterialCost:'PRIVATE-COST',unitLaborHours:'PRIVATE-HOURS',hourlyRate:'PRIVATE-RATE',markupPercent:'PRIVATE-MARKUP'},unitNet:'10.00',net:'100.00',tax:'19.00',gross:'119.00'}]}};}

describe('portal selling-only preview',()=>{
  it('copies only the released party and selling-price fields without rounding decimal strings',()=>{
    const result=portalQuotePreview(issuedQuote());
    expect(result.lines[0]).toEqual({description:'Dachleistung',unit:'m²',quantity:'10.123456789012',vatPercent:'19',unitNet:'10.00',net:'100.00',tax:'19.00',gross:'119.00'});
    expect(result.net).toBe('9007199254740993.10');expect(result.issuedDigest).toBe('issued-evidence');
    expect(JSON.stringify(result)).not.toMatch(/PRIVATE|tenant-internal|customer-internal|pricing-evidence|unitMaterialCost|unitLaborHours|hourlyRate|markupPercent/);
  });
  it('requires the complete issued evidence instead of accepting a pricing-only legacy quote',()=>{
    expect(()=>portalQuotePreview({...issuedQuote(),presentation:null})).toThrow('Ausgabestand');
    expect(()=>portalQuotePreview({...issuedQuote(),issuedDigest:''})).toThrow('Ausgabestand');
  });
  it('creates independent visible values without modifying the source',()=>{
    const source=issuedQuote(),result=portalQuotePreview(source);result.presentation.buyerName='Anderer Anzeigename';result.lines[0].description='Geänderte Vorschau';
    expect(source.presentation.buyerName).toBe('Kunde');expect(source.snapshot.lines[0].inputs.description).toBe('Dachleistung');
  });
});
