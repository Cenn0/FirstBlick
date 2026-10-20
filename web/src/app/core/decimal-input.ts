/** Decimal business values travel as text; only the server performs commercial arithmetic. */
export function decimalInput(value:unknown):string {
  const text=String(value??'').trim().replace(',','.');
  if(!/^-?\d+(?:\.\d+)?$/.test(text))throw new Error('Geben Sie Geldbeträge und Mengen als Dezimalzahl ohne Tausendertrennzeichen oder Exponent ein.');
  return text;
}
