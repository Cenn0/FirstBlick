/** Display server decimals without converting their significant digits to a binary float. */
export function decimalDisplay(value:string,minimumFractionDigits:number,maximumFractionDigits:number):string|null{
  const match=/^(-?)(\d+)(?:\.(\d+))?$/.exec(value);
  if(!match)return null;
  const sign=match[1],whole=match[2],fraction=match[3]||'';
  // Monetary reports already contain rounded cents; do not silently round a malformed response.
  if(fraction.slice(maximumFractionDigits).replace(/0/g,''))return null;
  const displayed=fraction.slice(0,maximumFractionDigits).replace(/0+$/,'').padEnd(minimumFractionDigits,'0');
  const grouped=whole.replace(/^0+(?=\d)/,'').replace(/\B(?=(\d{3})+(?!\d))/g,'.');
  return sign+grouped+(displayed?','+displayed:'');
}

export function moneyDisplay(value:string):string{
  const formatted=decimalDisplay(value,2,2);
  return formatted===null?'Betrag nicht verfügbar':formatted+' €';
}
