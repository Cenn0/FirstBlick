# Versioned imports and exports

FirstBlick supplies an explicit bank CSV import and three evidence-bearing export packages. These are FirstBlick formats. They do not claim compatibility or certification for DATEV, GAEB, XRechnung, ZUGFeRD, a payroll provider or a bank API.

The adapters operate on the same tenant-scoped ledger and approved work-time records as the application. Bank import records transactions that already occurred. No adapter initiates transfers, supplies bank credentials, automatically assigns payments, sends payroll or transmits data to another service.

## Available contracts

All paths below are relative to `/api/v1`. Requests use the existing authenticated session and `X-Tenant-Id`. Mutating requests also use the normal CSRF token. Server authorization is repeated after acquiring the tenant transaction lock.

| Operation | HTTP path | Contract | Required role |
|---|---|---|---|
| Bank preview | `POST /integrations/v1/bank-csv/preview` | `firstblick.bank-csv/1.0.0` | ManagingDirector or Accounting; finance module enabled |
| Bank import | `POST /integrations/v1/bank-csv/import` | Same CSV input and a versioned import receipt | ManagingDirector or Accounting; finance module enabled |
| Accounting evidence | `GET /integrations/v1/exports/accounting?from=2026-10-01&to=2026-10-31` | `firstblick.accounting/1.0.0` | ManagingDirector, Office or Accounting |
| Current open items | `GET /integrations/v1/exports/open-items?from=2026-10-01&to=2026-10-31` | `firstblick.open-items/1.0.0` | ManagingDirector, Office or Accounting |
| Approved work time | `GET /integrations/v1/exports/payroll-time?from=2026-10-01&to=2026-10-31` | `firstblick.payroll-time/1.0.0` | ManagingDirector or Office |

Read access to historical records remains available when a module is disabled, according to the application's existing policy. Disabling finance prevents new bank previews/imports. Bulk export has the explicit role restrictions above, including for employees who can otherwise read their own time records.

## Bank CSV-v1 input

Send `Content-Type: text/csv; charset=utf-8`. UTF-8 without a byte-order mark is recommended; a UTF-8 BOM is accepted. The adapter validates UTF-8 strictly and does not infer legacy encodings. The exact case-sensitive header, order and seven columns are:

```csv
account_reference,external_id,booked_on,amount_eur,currency,counterparty,reference
operating-account,booking-2026-0001,2026-10-20,119.00,EUR,"Example, customer",Invoice I-2026-1
operating-account,booking-2026-0002,2026-10-21,-59.50,EUR,Example supplier,Material receipt
```

| Column | Meaning and validation |
|---|---|
| `account_reference` | Stable account identifier supplied by the source adapter. Required, at most 120 characters; no surrounding whitespace or control characters. It is an identifier, not a credential or proof of account ownership. |
| `external_id` | Stable source transaction identifier within that account. Required, at most 240 characters; no surrounding whitespace or control characters. Re-exporting the same source transaction must retain this ID. |
| `booked_on` | Valid calendar date in exact `yyyy-MM-dd` form. |
| `amount_eur` | Signed, nonzero decimal EUR amount with exactly two digits after a period. Incoming money is positive; outgoing money is negative. Absolute value is bounded by the ledger's EUR 1,000,000,000,000 limit. |
| `currency` | Exact `EUR`. Currency conversion is not performed. |
| `counterparty` | Optional source text preserved in the transaction, at most 240 characters. |
| `reference` | Optional source booking reference preserved verbatim, including valid quoted line breaks, at most 2,000 characters. |

Examples rejected without rounding: `1`, `1.0`, `1.001`, `1,00`, `+1.00`, `01.00`, `1e2`, values with surrounding spaces and `0.00`. The fixed decimal representation removes ambiguity between grouping characters and decimal separators.

Fields containing commas, quotes or line breaks must be enclosed in double quotes. A quote inside a quoted field is doubled. CRLF and LF are accepted as record endings; output always uses CRLF. A closing quote may only be followed by a delimiter or record ending. Unclosed quotes, stray unquoted quotes, unquoted bare carriage returns, invalid UTF-8 and NUL are rejected. Header normalization, delimiter guessing and locale guessing are not performed.

Limits: at least one and at most 1,000 transaction records, 2 MiB of actual input bytes, seven fields per record, at most 4,096 characters per parsed field and the stricter existing domain limit for each bank field. Multiline quoted values count as one logical record. Record numbers in errors and preview results include the header as record 1. A trailing record terminator does not add an empty transaction.

### Preview, then import

The preview validates the complete file and returns its server-calculated `sourceSha256`, counts and one result for each logical input record:

| State | Meaning |
|---|---|
| `New` | No prior transaction has this account/external-ID pair. |
| `Reused` | A stored or earlier same-file transaction has this pair and identical immutable imported content. A stored match supplies `existingId`. |
| `Conflict` | The pair already exists with different immutable imported content. The whole batch has `canImport: false`. |

Preview does not create entities, receipts, audit writes or jobs. A preview response is not an authorization token or reservation. Its source hash binds the bytes reviewed; current membership, module state and duplicate evidence are checked again during import.

Import requires:

- The same raw CSV bytes, including any BOM and record endings.
- `X-Import-SHA256` equal to the actual SHA-256 calculated by the server from those bytes.
- An `Idempotency-Key` conforming to the API's existing safe-character/length contract.

The header hash is never trusted by itself. The command fingerprint includes the actual source SHA, adapter schema and header contract. Reusing a successful command key for a different raw file returns an idempotency conflict, even if both files were individually valid. Retrying the same command returns the stored receipt only after current role/module authorization. A later import with a new command key may reuse identical bank rows without inserting them again.

All rows are parsed and validated before mutation. Under the tenant advisory lock, every account/external-ID pair is checked again before any row is staged. A conflict rejects the complete batch. New bank rows, their immutable import digests, audit entries, internal change jobs and the command receipt are committed in one PostgreSQL transaction. Concurrent imports serialize; the tenant-qualified unique database key provides an additional constraint. Identical duplicates inside a single file resolve to one persisted bank ID.

The adapter creates no payment allocations. Users reconcile imported rows through the existing customer/supplier payment commands, which enforce shared bank capacity, reversals, discounts and retainage. An import conflict must be investigated at the source; the adapter never overwrites sealed bank evidence to make a file fit.

### Import event receipt

The persisted response includes `event`, `adapter`, `created`, `reused` and `items`. Each item identifies the logical input record, bank entity ID, immutable import digest and whether that occurrence created the entity.

The `event` envelope uses `firstblick.integration-event/1.0.0` and contains:

| Field | Meaning |
|---|---|
| `id` | Server-generated capture/event UUID. Same successful command replay returns the same ID. |
| `tenantId`, `actorId` | Current authenticated business and actor, never supplied by CSV fields. |
| `occurredAt` | Server UTC completion timestamp. |
| `type` | `bank-import.completed`. |
| `sourceSha256` | SHA-256 of the exact source CSV bytes. |

This envelope is a durable command receipt and audit correlation, not an external webhook subscription. The worker's internal `entity.changed` jobs remain internal application jobs. External event delivery, webhook signatures, subscriber registration, retention commitments and provider replay contracts are not implemented by this adapter.

## Common export package

Each export returns `application/zip`, an attachment filename containing the adapter/version/date window, `X-FirstBlick-Export-Schema`, and `X-Content-SHA256` for the exact ZIP response. ZIP entries consist of `manifest.json` and named CSV tables. An empty selection produces the manifest and header-only tables; it does not invent zero-value business records.

The manifest identifies the schema, unique capture ID, tenant, actor, UTC capture time, inclusive date window, business timezone and its resolved UTC start/end boundaries. It describes selection/state semantics and text encoding, sets `initiatesPayments: false` and `certifiedProviderFormat: false`, and lists each file's schema, columns, data-row count, byte length and SHA-256. Consumers should verify every file against that manifest before processing it.

These are integrity and provenance checks, not a digital signature or third-party attestation. A digest does not prove that an upstream transaction was true, a work record was complete or a document met a provider/legal profile. Export downloads are audited with their manifest and ZIP digest. Exporting does not modify the financial source records.

CSV output is comma-separated UTF-8 without a BOM, with all cells quoted, internal quotes doubled and CRLF endings. Every table begins with `schema_version`, `tenant_id`, `capture_id`, `captured_at_utc`. Dates are ISO calendar dates, instants are UTC ISO timestamps, IDs use canonical UUID strings, flags are `true`/`false`, and numeric fields use invariant decimal periods without exponent notation. Monetary totals retain exact cents. Source cost/quantity/percentage inputs may retain their finer decimal precision in explicitly named input columns.

### Reversible spreadsheet text protection

**Every user-controlled text cell begins with the literal `text:` prefix.** This applies to ordinary names as well as values beginning with `=`, `+`, `-`, `@`, full-width variants, whitespace or line breaks. Embedded delimiters and line endings remain inside a correctly quoted cell. Numeric/date/ID/code cells originate from typed validated values and do not carry that text prefix.

For example, the bank reference `=1+1` is exported as the quoted cell `"text:=1+1"`; the name `00123` becomes `"text:00123"`. A source value already beginning with `text:` becomes `text:text:...`. A trusted typed downstream importer removes exactly one prefix from columns declared as text. It must not remove the prefix before opening an untrusted CSV in a spreadsheet. The prefix is part of this versioned format, so no heuristic string sanitization or single-quote behavior is required for round-trip decoding.

A consumer converting this package into another CSV format owns that transformation and its spreadsheet-safety behavior. No sanitization scheme is claimed to remain safe after arbitrary editor rewrites or removal of the protective text encoding. The accounting CSV files have a different schema from bank CSV-v1 and cannot be fed directly into the bank importer.

### Time windows and transactional capture

`from` and `to` are inclusive valid calendar dates in the business's configured timezone; the maximum window is 366 days. UTC event selection uses `[local midnight at from, local midnight after to)`. The tenant advisory lock protects source selection, current authorization, linked evidence loading and the audit write from concurrent application mutations in the same tenant.

The export rejects `asOf` and `as_of` parameters: these adapters do not reconstruct historical ledger state. An invalid or ambiguous local midnight is rejected as `unsupported_time_boundary` instead of assigning a guessed UTC offset. Calendar/offset interpretation uses the deployment's installed timezone data; the manifest's UTC boundaries and each payroll segment's UTC endpoints preserve the interpretation actually used.

Budgets are checked before loading the full source JSON: at most 10,000 selected or connected records and 12 MiB of source JSON. CSV output is limited to 50,000 data rows per table and 25 MiB across all CSV tables. A graph with many linked settlements may exceed the limit even for a short invoice-date window; the response fails explicitly instead of silently omitting evidence. Further partitioning or a separately reviewed streaming adapter is required in that case.

## Accounting export

`firstblick.accounting/1.0.0` starts with issued customer invoice dates, approved supplier invoice dates, bank booking dates, and payment allocation/reversal/retention-release timestamps inside the window. It follows financially relevant links in both directions to include complete connected posted context. Draft or rejected invoices are not exported as posted ledger evidence. Archived posted financial records remain part of the evidence where linked.

Each primary table marks whether the record was directly selected by the window (`selection=window`) or included as reconciliation context (`selection=context`). Later credits, payments, returns and releases may therefore appear even when their own dates lie outside the window. This context makes current reconciliation inspectable; consumers must not sum every connected context row as if it were a new posting inside the requested period.

| File | Principal evidence |
|---|---|
| `receivables.csv` | Invoice/version/number, customer and order IDs, original invoice ID, dates, frozen seller/buyer fields, currency, net/tax/gross, correction reason, issued/pricing/current-record digests. Credit notes carry `effect_sign=-1`; absolute amounts retain their original positive representation. |
| `receivable_lines.csv` | Parent invoice and issued digest, line number, optional catalog ID, frozen description/unit/quantity/cost/labor/rates/discount/VAT inputs and independently stored calculation outputs. |
| `payables.csv` | Supplier invoice, purchase order/original invoice references, frozen supplier fields, exact totals, approval actor/time, approved/current-record digests and credit effect sign. Supplier invoices currently contain summary totals rather than a full supplier line model. |
| `bank_transactions.csv` | Stable account/external IDs, booking date, signed actual money, counterparty/reference, immutable import digest and current-record digest. |
| `customer_allocations.csv` | Invoice and original bank IDs, receipt/refund direction, cash, discount, original retention, allocation actor/time, reversal actor/time/reason, optional return bank ID, immutable posted digest and current-record digest. Separate flags identify allocation/reversal timestamps inside the window. |
| `retention_releases.csv` | Parent allocation/invoice, stable append-order release index, exact amount, actor/time/reason, window flag, parent posted digest and event-content digest. |
| `supplier_allocations.csv` | Supplier invoice/bank IDs, payment/refund direction, cash/discount, allocation/reversal evidence and optional return bank ID, posted/current-record digests. |

This is an evidence-oriented ledger export. It does not infer general-ledger account numbers, tax keys, statutory report classifications or a provider posting batch. A provider adapter must explicitly map and independently validate those fields before treating this package as a bookkeeping import.

## Current open items

`firstblick.open-items/1.0.0` selects original customer/supplier invoices by their invoice dates. It includes the accounting evidence tables above, plus `open_receivables.csv` and `open_payables.csv` with **current** balances derived from the same domain workflows used by the application.

Receivables distinguish original gross, issued credits, net received cash, applied discount, unreleased retention, outstanding amount, collectible amount and customer credit. Payables distinguish gross, credits, paid cash, discount, outstanding amount and supplier credit. Each row carries the immutable issued/approved digest and a balance-evidence digest that binds the current payment/correction states. The overdue comparison date is the local capture date; it is named explicitly in the CSV.

Reversed allocations no longer reduce the current outstanding amount; their reversal history and any return-bank link remain in the evidence tables. A released retention amount becomes collectible without inventing a cash receipt. Corrections can produce credit balances; those remain visible until a separately reconciled refund settles them. Fully settled originals with no customer/supplier credit are omitted from the `open_*` tables, though connected evidence tables may still include them.

Independent reference case: EUR 119.00 gross, EUR 23.80 credit, EUR 47.60 cash and EUR 1.19 discount leave EUR 46.41 outstanding. An original EUR 11.90 retention with EUR 5.95 released leaves EUR 5.95 retained and EUR 40.46 collectible. Tests assert these amounts together with the linked credit, allocation and release IDs/digests.

## Approved work-time export

`firstblick.payroll-time/1.0.0` contains `approved_time.csv`. Only approved, nonarchived time entries whose UTC intervals intersect the local date window are included. A currently inactive employee profile does not erase previously approved time; its current active flag and profile digest are exported for review.

Each interval is clipped to the requested window and split at every business-local midnight. Every break is independently clipped to each segment. Durations are computed from elapsed UTC ticks, so a daylight-saving jump/repetition cannot invent or lose recorded time. Exactly 10,000,000 ticks equal one second. `net_hours` is rounded once per segment to six decimal places; `net_ticks` is the exact authoritative duration for downstream aggregation. Summing rounded display hours may differ slightly from rounding the aggregate exact ticks.

Rows preserve employee/user/project IDs, current profile/project names, local date and timezone, original UTC record endpoints, UTC segment endpoints, gross/break/net ticks, rounded hours, activity text and source/profile digests. Entirely break-covered clipped segments produce no worked-time row. A half-open endpoint exactly at midnight does not create work on the next date.

Tests cover an overnight break split equally across midnight, the Berlin spring change (four displayed wall hours are three elapsed hours), the autumn repeated hour, clipping a repeated-hour break using UTC, and an invalid skipped local date. The adapter does not calculate wages, statutory allowances, tax, overtime entitlements, sick/vacation pay, training allowances or working-time compliance. Those require a separately specified and reviewed payroll mapping.

## Versioning, maintenance and unimplemented formats

The URL major version, schema identifier, exact headers, typed columns, text encoding and receipt semantics form the adapter contract. Breaking header/type/meaning changes require a new major contract and compatibility tests. New optional provider metadata must not silently change existing amount/date interpretation. Existing imports retain sealed source evidence; a new adapter version must not rewrite its meaning in place.

Before enabling a named provider/standard adapter, record the owner, upstream specification/profile/version, licensing and allowed use, sample fixtures, independent acceptance evidence, update monitoring, rejection behavior and migration policy. Version changes must have reviewed fixtures for rounding, encoding, duplicate IDs, corrections and source references. The current code has no dependency on a CSV provider library or an unverified proprietary implementation.

GAEB, catalog import, bank-provider synchronization and structured e-invoice profiles remain separate work. Catalog entities currently lack a defined stable external catalog/version mapping; this adapter does not guess identity from display names or silently duplicate materials. XML profile validation must reject unsupported profiles and external entity resolution before any posting; a successful generic XML parse would not be sufficient acceptance evidence. External webhook delivery and provider-side exports remain unimplemented.

## Verification

Focused Core tests live in `tests/FirstBlick.Core.Tests/Integrations`: strict UTF-8/quoting/header/bounds, exact money, same-file/stored duplicate conflicts, reversible formula-text protection, finance reference amounts, reversals and returned-bank links, supplier corrections, ZIP file hashes and daylight-saving work-time segmentation.

`tests/FirstBlick.Api.Tests/IntegrationApiTests.cs` uses real HTTP sessions/CSRF and a disposable PostgreSQL database. It verifies preview without writes; actual-byte hash matching; changed-body command-key conflicts; complete rollback on a later invalid/conflicting row; six concurrent identical imports creating one bank row; current role/module checks including cached receipt replay; tenant isolation; manifest/file hashes; connected current correction/allocation evidence; approved time selection and all seven business roles' export restrictions.

Run the focused suites with the installed .NET SDK:

```bash
dotnet test tests/FirstBlick.Core.Tests/FirstBlick.Core.Tests.csproj --filter FullyQualifiedName~Integrations
FIRSTBLICK_TEST_POSTGRES='Host=localhost;Database=firstblick_test;Username=test;Password=synthetic-test-password' \
  dotnet test tests/FirstBlick.Api.Tests/FirstBlick.Api.Tests.csproj --filter FullyQualifiedName~IntegrationApiTests
```

The PostgreSQL account must be able to create/drop the fixture's uniquely prefixed test databases. Tests never connect to a bank, send payroll, initiate a payment or send external messages. Repository CI requires its synthetic PostgreSQL fixture and does not treat a missing database as an accepted integration test.

## Sources and design provenance

- [RFC 4180, Common Format and MIME Type for CSV Files](https://www.rfc-editor.org/rfc/rfc4180.html) describes the comma-separated, quoted/doubled-quote format and `text/csv` media type. It is informational. FirstBlick defines the stricter business schema above and explicitly accepts LF input as an interoperability choice.
- [OWASP, CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection) documents spreadsheet formula interpretation and the limits of common quoting/escape approaches. The mandatory reversible `text:` encoding is FirstBlick's own explicit contract; it does not depend on single-quote behavior in a particular spreadsheet product.
- Ledger rules, immutable digests, interval validation and reference fixtures are independently authored FirstBlick domain code in `Core/Business` and `Core/Integrations`. No proprietary legacy program code, bundled calculations or assets were copied into these adapters.
