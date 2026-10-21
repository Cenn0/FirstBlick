export type ExportKind='accounting'|'open-items'|'payroll-time';
export interface BankCsvRow{record:number;accountReference:string;externalId:string;bookedOn:string;amount:string;currency:string;counterparty:string;reference:string}
export interface BankCsvPreviewRow{record:number;state:'New'|'Reused'|'Conflict';existingId:string|null;importDigest:string;values:BankCsvRow}
export interface BankCsvPreview{schema:string;sourceSha256:string;new:number;reused:number;conflicts:number;canImport:boolean;rows:BankCsvPreviewRow[]}
export interface BankImportReceipt{event:{schema:string;id:string;tenantId:string;actorId:string;occurredAt:string;type:string;sourceSha256:string};adapter:string;created:number;reused:number;items:{record:number;id:string;importDigest:string;created:boolean}[]}
export interface ExportDefinition{kind:ExportKind;title:string;description:string;scope:string;schema:string}
export const exportDefinitions:ExportDefinition[]=[
  {kind:'accounting',title:'Buchhaltung',description:'Gebuchte Ausgangs- und Eingangsbelege, Bankumsätze, Zuordnungen und Korrekturen mit ihren Bezügen.',scope:'Der Zeitraum wählt Belege und Ereignisse aus. Verbundene Buchungen außerhalb des Zeitraums sind als Kontext gekennzeichnet.',schema:'firstblick.accounting/1.0.0'},
  {kind:'open-items',title:'Offene Posten',description:'Aktuelle offene Forderungen und Verbindlichkeiten mit Zahlungen, Skonto, Einbehalten und Gutschriften.',scope:'Der Zeitraum bezieht sich auf das Rechnungsdatum. Salden entsprechen dem aktuellen Stand beim Export; ein historischer Stichtag wird nicht rekonstruiert.',schema:'firstblick.open-items/1.0.0'},
  {kind:'payroll-time',title:'Freigegebene Arbeitszeiten',description:'Freigegebene Zeiten und Pausen mit Mitarbeiter-, Projekt- und Tagesbezug für eine weitere Lohnvorbereitung.',scope:'Zeiten werden an den Tagesgrenzen des Betriebs aufgeteilt. Vergütung, Zuschläge und Lohnberechnung sind nicht enthalten.',schema:'firstblick.payroll-time/1.0.0'}
];

export function inclusiveDateRange(from:string,to:string):number{
  const parse=(value:string)=>{if(!/^\d{4}-\d{2}-\d{2}$/.test(value))throw new Error('Bitte gültige Datumsgrenzen auswählen.');const instant=Date.parse(value+'T00:00:00Z');if(!Number.isFinite(instant)||new Date(instant).toISOString().slice(0,10)!==value)throw new Error('Bitte gültige Datumsgrenzen auswählen.');return instant;};
  const days=(parse(to)-parse(from))/86400000+1;if(days<1)throw new Error('Das Enddatum muss am oder nach dem Anfangsdatum liegen.');if(days>366)throw new Error('Ein Export darf höchstens 366 Kalendertage umfassen.');return days;
}
export async function sha256Blob(blob:Blob):Promise<string>{return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',await blob.arrayBuffer())),value=>value.toString(16).padStart(2,'0')).join('');}
export async function verifiedExport(response:Response,expectedSchema:string):Promise<{blob:Blob;sha256:string}>{
  if(response.headers.get('X-FirstBlick-Export-Schema')!==expectedSchema)throw new Error('Das Exportformat stimmt nicht mit der ausgewählten Version überein. Die Datei wurde nicht heruntergeladen.');
  if(response.headers.get('Content-Type')?.split(';')[0].trim()!=='application/zip')throw new Error('Der Server hat kein ZIP-Paket geliefert. Bitte erneut versuchen.');
  const expected=response.headers.get('X-Content-SHA256');if(!expected||!/^[a-f0-9]{64}$/i.test(expected))throw new Error('Die Prüfsumme des Exportpakets fehlt. Die Datei wurde nicht heruntergeladen.');
  const blob=await response.blob(),actual=await sha256Blob(blob);if(actual!==expected.toLowerCase())throw new Error('Die Prüfsumme des Exportpakets stimmt nicht. Die Datei wurde nicht heruntergeladen. Bitte erneut versuchen.');return {blob,sha256:actual};
}
export function downloadBlob(blob:Blob,name:string):void{const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=name;link.click();setTimeout(()=>URL.revokeObjectURL(url),60000);}
