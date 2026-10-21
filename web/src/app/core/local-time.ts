export interface LocalTimeChoice{instant:string;offset:string;label:string}
function offsetLabel(minutes:number):string{const absolute=Math.abs(minutes);return (minutes<=0?'+':'-')+String(Math.floor(absolute/60)).padStart(2,'0')+':'+String(absolute%60).padStart(2,'0');}
export function localDateTime(instant:string):string{const date=new Date(instant);if(!Number.isFinite(date.getTime()))throw new Error('Ungültiger Zeitpunkt.');const value=new Date(date.getTime()-date.getTimezoneOffset()*60000).toISOString().slice(0,-1);return value.endsWith(':00.000')?value.slice(0,-7):value.endsWith('.000')?value.slice(0,-4):value;}
export function instantOffset(instant:string):string{return offsetLabel(new Date(instant).getTimezoneOffset());}

/** Find actual instants for a device-local wall time; gaps and repeated hours are never guessed. */
export function localTimeChoices(local:string):LocalTimeChoice[]{
  const match=/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?$/.exec(local);if(!match)return [];
  const [,year,month,day,hour,minute,second='0',fraction='0']=match,ms=Number(fraction.padEnd(3,'0'));
  const utc=new Date(0);utc.setUTCFullYear(Number(year),Number(month)-1,Number(day));utc.setUTCHours(Number(hour),Number(minute),Number(second),ms);const base=utc.getTime();
  if(utc.getUTCFullYear()!==Number(year)||utc.getUTCMonth()+1!==Number(month)||utc.getUTCDate()!==Number(day)||utc.getUTCHours()!==Number(hour)||utc.getUTCMinutes()!==Number(minute)||utc.getUTCSeconds()!==Number(second))return [];
  const offsets=new Set<number>();for(let hours=-48;hours<=48;hours+=6)offsets.add(new Date(base+hours*3600000).getTimezoneOffset());
  const choices:LocalTimeChoice[]=[];for(const offset of offsets){const date=new Date(base+offset*60000);if(date.getFullYear()===Number(year)&&date.getMonth()+1===Number(month)&&date.getDate()===Number(day)&&date.getHours()===Number(hour)&&date.getMinutes()===Number(minute)&&date.getSeconds()===Number(second)&&date.getMilliseconds()===ms)choices.push({instant:date.toISOString(),offset:offsetLabel(offset),label:'UTC'+offsetLabel(offset)});}
  choices.sort((a,b)=>a.instant.localeCompare(b.instant));return choices.map((choice,index)=>({...choice,label:choices.length>1?`${index+1}. Auftreten · ${choice.label}`:choice.label}));
}
export function resolveLocalTime(local:string,offset?:string):string{const choices=localTimeChoices(local);if(!choices.length)throw new Error('Diese Ortszeit existiert in der Gerätezeitzone nicht oder ist ungültig. Prüfen Sie Datum und Uhrzeit, insbesondere bei einer Zeitumstellung.');if(choices.length===1)return choices[0].instant;const chosen=choices.find(choice=>choice.offset===offset);if(!chosen)throw new Error('Diese Ortszeit kommt bei der Zeitumstellung zweimal vor. Wählen Sie für den Zeitpunkt ausdrücklich den passenden Zeitversatz.');return chosen.instant;}
