import {describe,expect,it} from 'vitest';
import {decimalDisplay,moneyDisplay} from '../src/app/core/decimal-format';

describe('exact server-decimal presentation',()=>{
  it('preserves a single cent in a contract aggregate beyond binary-float precision',()=>{
    expect(moneyDisplay('100000000000000.01')).toBe('100.000.000.000.000,01 €');
    expect(moneyDisplay('-100000000000000.01')).toBe('-100.000.000.000.000,01 €');
  });
  it('retains declared ratio precision and formats zero and trailing decimal zeroes',()=>{
    expect(decimalDisplay('100000000000000.000001',0,6)).toBe('100.000.000.000.000,000001');
    expect(moneyDisplay('0')).toBe('0,00 €');
    expect(moneyDisplay('80.5000')).toBe('80,50 €');
  });
  it('does not silently round unsupported monetary scale or reinterpret exponent input',()=>{
    expect(moneyDisplay('1.001')).toBe('Betrag nicht verfügbar');
    expect(moneyDisplay('1e12')).toBe('Betrag nicht verfügbar');
  });
});
