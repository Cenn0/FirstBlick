/** Monetary and quantity values are server-provided decimal strings. Display never re-prices a position. */
export interface FrozenCommercialLine{inputs:{description:string;quantity:string;unit:string};unitNet:string;net:string;tax:string;gross:string}
export interface FrozenCommercialSnapshot{net:string;tax:string;gross:string;digest:string;lines:FrozenCommercialLine[]}
export interface InvoiceCalculationSource{kind:string;id:string;label:string;documentDigest:string;snapshot:FrozenCommercialSnapshot}
export interface InvoicePreviousDocument{id:string;number:string;kind:'Final'|'Progress'|'CreditNote';originalInvoiceId:string|null;digest:string}
export interface InvoicePositionCalculation{sourceIndex:number;positionIndex:number;beforeQuantity:string;afterQuantity:string;beforeNet:string;afterNet:string;beforeTax:string;afterTax:string}
export interface InvoiceCalculatedLine{lineIndex:number;lineKey:string;beforeQuantity:string;afterQuantity:string;beforeNet:string;beforeTax:string;expectedBeforeNet:string;expectedBeforeTax:string;afterNet:string;afterTax:string;positions:InvoicePositionCalculation[]}
export interface InvoiceCalculation{methodVersion:string;basis:'Contract'|'OriginalInvoice';basisId:string;sources:InvoiceCalculationSource[];previousInvoices:InvoicePreviousDocument[];previousBalances:{lineKey:string;quantity:string;net:string;tax:string}[];lines:InvoiceCalculatedLine[];digest:string}
