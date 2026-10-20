import {Entity,Page} from './api.service';

export interface PortalShare extends Entity {
  title:string;sourceKind:'quotes'|'documents';sourceId:string;projectId:string;sourceDigest:string;
  allowAcceptance:boolean;expiresAt:string;recipientUserId?:string;revokedAt?:string|null;
}
export interface PortalRecipient {userId:string;displayName:string;email:string}
export interface PortalFeedback {id:string;message:string;fromBusiness:boolean;sourceDigest:string;createdAt:string}
export interface PortalQuote {
  id:string;revision:number;number:string;title:string;version:number;status:string;validUntil:string|null;issuedDigest:string;currency:string;
  presentation:{sellerName:string;buyerName:string;buyerAddress:string;issuedAt:string};
  net:string;tax:string;gross:string;
  lines:{description:string;unit:string;quantity:string;unitNet:string;vatPercent:string;net:string;tax:string;gross:string}[];
}
export interface PortalDocument {id:string;name:string;contentType:string;size:number;sha256:string;sourceDigest?:string}
export interface PortalDetail {share:PortalShare;quote:PortalQuote|null;document:PortalDocument|null;feedback:Page<PortalFeedback>}

/** The internal preview deliberately uses the same selling-only shape as the partner endpoint. */
export function portalQuotePreview(entity:Entity):PortalQuote {
  const snapshot=entity['snapshot'] as {net:string;tax:string;gross:string;lines:{inputs:{description:string;unit:string;quantity:string;vatPercent:string};unitNet:string;net:string;tax:string;gross:string}[]};
  const presentation=entity['presentation'] as PortalQuote['presentation'];
  if(!snapshot||!presentation||!entity['issuedDigest'])throw new Error('Das Angebot benötigt einen vollständig festgehaltenen Ausgabestand.');
  return {id:entity.id,revision:entity.revision,number:String(entity['number']),title:String(entity['title']),version:Number(entity['version']),status:String(entity['status']),validUntil:entity['validUntil']as string|null,issuedDigest:String(entity['issuedDigest']),currency:String(entity['currency']),presentation:{sellerName:presentation.sellerName,buyerName:presentation.buyerName,buyerAddress:presentation.buyerAddress,issuedAt:presentation.issuedAt},net:snapshot.net,tax:snapshot.tax,gross:snapshot.gross,lines:snapshot.lines.map(line=>({description:line.inputs.description,unit:line.inputs.unit,quantity:line.inputs.quantity,vatPercent:line.inputs.vatPercent,unitNet:line.unitNet,net:line.net,tax:line.tax,gross:line.gross}))};
}
