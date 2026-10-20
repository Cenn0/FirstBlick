import {Entity} from './api.service';
export interface QuantityEvidence{measurementId:string;measurementDigest:string;measurementLineId:string;baseQuantity:string;baseUnit:string;targetUnit:string;conversionFactor:string;wastePercent:string;ruleSource:string;methodVersion:string;quantity:string;digest:string}
export interface QuantityLine{description:string;unit:string;quantity:string;unitMaterialCost:string;unitLaborHours:string;hourlyRate:string;markupPercent:string;discountPercent:string;vatPercent:string;catalogItemId?:string|null;quantityEvidence?:QuantityEvidence|null}
export interface PurchaseLine{id:string;materialId:string;description:string;quantity:string;unitNet:string;vatPercent:string;materialDemandId?:string|null;quantityEvidence?:QuantityEvidence|null}
export interface MeasurementBasis extends Entity{title:string;projectId:string;status:string;snapshot:{digest:string;lines:{id:string;description:string;quantity:string;unit:string}[]}|null}
export interface QuoteDraft extends Entity{title:string;projectId:string;status:string;lines:QuantityLine[]}
export interface MaterialDemand extends Entity{title:string;projectId:string;materialId:string;requiredOn:string|null;status:'Pending'|'Converted'|'Cancelled';evidence:QuantityEvidence;purchaseOrderId:string|null;purchaseLineId:string|null;cancellationReason:string}
export interface QuoteQuantitySelection{measurementLineId:string;catalogItemId:string;conversionFactor:string;wastePercent:string;ruleSource:string}
export interface QuoteQuantityRequest{measurementId:string;measurementDigest:string;lines:QuoteQuantitySelection[];previewDigest?:string}
export interface QuoteQuantityPreview{quoteId:string;quoteRevision:number;measurementId:string;measurementDigest:string;catalog:{id:string;revision:number}[];lines:QuantityLine[];beforeNet:string;addedNet:string;afterNet:string;digest:string}
export interface MaterialDemandRequest{measurementId:string;measurementDigest:string;measurementLineId:string;materialId:string;title:string;requiredOn:string|null;conversionFactor:string;wastePercent:string;ruleSource:string;previewDigest?:string}
export interface MaterialDemandPreview{projectId:string;materialId:string;materialRevision:number;materialName:string;title:string;requiredOn:string|null;evidence:QuantityEvidence;digest:string}
export interface DemandPurchaseSelection{demandId:string;revision:number;unitNet:string;vatPercent:string}
export interface DemandPurchaseRequest{supplierId:string;title:string;expectedOn:string|null;lines:DemandPurchaseSelection[];previewDigest?:string}
export interface DemandPurchasePreview{projectId:string;supplierId:string;supplierRevision:number;title:string;expectedOn:string|null;selection:DemandPurchaseSelection[];lines:PurchaseLine[];net:string;tax:string;gross:string;digest:string}
