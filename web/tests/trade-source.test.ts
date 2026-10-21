import { describe, expect, test } from 'vitest';
import { createHash } from 'node:crypto';
import { OriginalSource, originalSource, originalReceipt, originalPage, originalDetail, verifiedOriginal } from '../src/app/core/trade-source.models';

const tenant = '10000000-0000-4000-8000-000000000001';
const bytes = new TextEncoder().encode('\ufeff<original> äöü <!-- keep --> </original>\r\n');
const sha = createHash('sha256').update(bytes).digest('hex');
const source = (): OriginalSource => ({ contract: 'firstblick.trade-source/1.0.0', id: '20000000-0000-4000-8000-000000000002', tenantId: tenant,
  format: 'gaeb', documentId: '30000000-0000-4000-8000-000000000003', fileName: 'Original.x83', sha256: sha, size: bytes.length,
  objectVersionId: 'version-1', receivedBy: '40000000-0000-4000-8000-000000000004', receivedAt: '2026-10-20T20:00:00+00:00', digest: 'e'.repeat(64) });
const preview = () => ({ contract: 'firstblick.gaeb-structural/1.0.0', sourceSha256: sha, exchangePhase: 'X83', schemaVersion: '3.3', schemaEdition: '2021-05',
  scheduleName: 'Synthetic', currency: 'EUR', externalSchemaValidationPerformed: false, reviewIssues: [],
  items: [{ positionKey: '01.001', positionId: 'i1', description: 'Synthetic work', tenderQuantity: '1', unit: 'm²', offeredUnitPrice: null, itemDigest: 'a'.repeat(64), reviewIssues: [] }] });
const detail = () => ({ source: source(), preview: preview(), catalogItems: [], contentVerified: true });
const response = (body: Uint8Array = bytes, headers: Record<string, string> = {}) => new Response(body as BodyInit, { headers: { 'Content-Type': 'application/octet-stream', 'X-Content-SHA256': sha, ...headers } });

describe('immutable original receipt contracts', () => {
  test('binds source and capture receipt to current business, syntax and exact bytes', () => {
    expect(originalSource(source(), tenant, 'gaeb').sha256).toBe(sha);
    const receipt = originalReceipt({ source: source(), alreadyStored: false, contentVerified: true }, tenant, 'gaeb', sha, bytes.length);
    expect(receipt.source.objectVersionId).toBe('version-1');
    expect(() => originalReceipt(receipt, tenant, 'gaeb', 'f'.repeat(64), bytes.length)).toThrow();
    expect(() => originalReceipt(receipt, tenant, 'gaeb', sha, bytes.length + 1)).toThrow();
  });
  test.each([
    { tenantId: '50000000-0000-4000-8000-000000000005' }, { format: 'e-invoice' }, { contract: 'unknown' },
    { id: 'not-an-id' }, { documentId: '00000000-0000-0000-0000-000000000000' },
    { fileName: '../original.xml' }, { fileName: 'original.html' }, { fileName: 'original.xml\n' },
    { size: 0 }, { size: 1048577 }, { objectVersionId: 'null' }, { receivedAt: 'invalid' }, { digest: 'wrong' }
  ])('rejects malformed or mismatching metadata %j', patch => {
    expect(() => originalSource({ ...source(), ...patch }, tenant, 'gaeb')).toThrow();
  });
  test('a renamed duplicate keeps its first receipt while still binding the selected byte hash', () => {
    expect(originalReceipt({ source: { ...source(), fileName: 'first-name.xml' }, alreadyStored: true, contentVerified: true }, tenant, 'gaeb', sha, bytes.length).alreadyStored).toBe(true);
  });
  test('does not describe unverified capture or list metadata as verified contents', () => {
    expect(() => originalReceipt({ source: source(), alreadyStored: true, contentVerified: false }, tenant, 'gaeb', sha, bytes.length)).toThrow();
    const page = { items: [source()], total: 1, page: 1, pageSize: 20, contentVerified: false };
    expect(originalPage(page, tenant, 'gaeb', 1, 20).contentVerified).toBe(false);
    expect(() => originalPage({ ...page, contentVerified: true }, tenant, 'gaeb', 1, 20)).toThrow();
  });
  test('rejects inconsistent pagination, duplicate identifiers and silently truncated pages', () => {
    const page = { items: [source()], total: 1, page: 1, pageSize: 20, contentVerified: false };
    expect(() => originalPage({ ...page, page: 2 }, tenant, 'gaeb', 1, 20)).toThrow();
    expect(() => originalPage({ ...page, total: 2 }, tenant, 'gaeb', 1, 20)).toThrow();
    expect(() => originalPage({ ...page, items: [source(), source()], total: 2 }, tenant, 'gaeb', 1, 20)).toThrow();
  });
  test('reopened detail is bound to the selected immutable receipt and preview SHA', () => {
    expect(originalDetail(detail(), source()).source.id).toBe(source().id);
    expect(() => originalDetail({ ...detail(), source: { ...source(), objectVersionId: 'changed' } }, source())).toThrow();
    expect(() => originalDetail({ ...detail(), preview: { ...preview(), sourceSha256: 'b'.repeat(64) } }, source())).toThrow();
    expect(() => originalDetail({ ...detail(), contentVerified: false }, source())).toThrow();
  });
  test('catalogue references must belong to an actual original position and cannot be repeated', () => {
    const link = { id: '60000000-0000-4000-8000-000000000006', positionKey: '01.001', name: 'Current editable name', unit: 'm²', archived: false };
    expect(originalDetail({ ...detail(), catalogItems: [link] }, source()).catalogItems[0].name).toBe(link.name);
    expect(() => originalDetail({ ...detail(), catalogItems: [{ ...link, positionKey: 'missing' }] }, source())).toThrow();
    expect(() => originalDetail({ ...detail(), catalogItems: [link, link] }, source())).toThrow();
  });
});

describe('byte-preserving bounded original downloads', () => {
  test('preserves UTF-8 BOM, comments, whitespace and original bytes', async () => {
    const blob = await verifiedOriginal(response(), source());
    expect(new Uint8Array(await blob.arrayBuffer())).toEqual(bytes);
    expect(blob.type).toBe('application/octet-stream');
  });
  test('detects same-length content corruption independently of a matching server header', async () => {
    const corrupt = bytes.slice(); corrupt[5] ^= 1;
    await expect(verifiedOriginal(response(corrupt), source())).rejects.toThrow('Originalbytes');
  });
  test('rejects wrong headers or an executable response type', async () => {
    await expect(verifiedOriginal(response(bytes, { 'X-Content-SHA256': 'a'.repeat(64) }), source())).rejects.toThrow();
    await expect(verifiedOriginal(response(bytes, { 'Content-Type': 'text/html' }), source())).rejects.toThrow();
    await expect(verifiedOriginal(response(bytes, { 'Content-Length': '9999' }), source())).rejects.toThrow();
  });
  test('rejects incomplete and over-budget byte streams', async () => {
    await expect(verifiedOriginal(response(bytes.slice(1)), source())).rejects.toThrow('unvollständig');
    await expect(verifiedOriginal(response(new Uint8Array(bytes.length + 1)), source())).rejects.toThrow('überschreitet');
  });
});
