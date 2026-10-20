import {describe,expect,it} from 'vitest';
import {decimalInput} from '../src/app/core/decimal-input';

describe('decimal transport',()=>{
  it('preserves commercial precision and meaningful trailing digits without a Number conversion',()=>{
    expect(decimalInput('9007199254740993.123456789012')).toBe('9007199254740993.123456789012');
    expect(decimalInput('0.10000000000000000001')).toBe('0.10000000000000000001');
    expect(decimalInput('12.5000')).toBe('12.5000');
  });
  it('accepts a German decimal separator and signed adjustments',()=>{
    expect(decimalInput(' 12,50 ')).toBe('12.50');
    expect(decimalInput('-0,25')).toBe('-0.25');
    expect(decimalInput('0')).toBe('0');
  });
  it.each(['1e3','1.234,56','1,234.56','',null,undefined,'NaN','Infinity','12.50 EUR'])('rejects ambiguous or missing amount %s',value=>{
    expect(()=>decimalInput(value)).toThrow('Dezimalzahl');
  });
});
