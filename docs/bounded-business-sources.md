# Whole source budgets and scoped financial reads

This DP-02-06 package changes source selection and overload behavior relative to baseline `03f16300f36dabf7930eef2de8ae7690e48feb89`. [PR #21](https://github.com/Cenn0/FirstBlick/pull/21) identifies the final changed revision and its CI evidence. It does not complete the original roadmap task.

## Source contract

[`EntitySources.Read`](../src/FirstBlick.Infrastructure/Persistence/EntitySources.cs) reads a complete source set for an explicit tenant and registered entity kind. It enforces **10,000 records and 12 MiB of PostgreSQL JSONB text per read**, inclusive of both limits. Archived rows are excluded unless the internal caller explicitly includes them for retained maintenance invariants. There is no request option to raise either limit.

Tenant/kind/archive, retained target relationships and optional unique keys filter the set **before** budgeting. PostgreSQL window aggregates, conditional JSON projection and result limiting execute in one statement. If either budget is exceeded, the result contains bounded metadata and null payloads; the reader throws before deserialization. It never returns the first 10,000 records as a usable partial history. A matching source is returned once even if two target relationships match it.

The reader checks tenant, kind, identifier, revision and archived state against each deserialized body. A selected relationship must also exist in the body's references; a requested unique key must match its body-derived projection. The financial adapter additionally calls each entity's domain validation. This catches inconsistent retained metadata or stale selected links; it is not a complete repair or integrity audit of all missing/corrupt database links.

| Caller | Source selection | Failure code family |
| --- | --- | --- |
| `EntityStore.All<T>` | Active records of one tenant/kind, including RecordPolicy dependencies | `source_budget`, `source_metadata` |
| Project/contract maintenance validation | Related maintenance visits, including retained archived history | `source_budget`, `source_metadata`, `source_reference` |
| Financial histories | Complete retained relationships for the current order, original invoice, supplier invoice or bank transaction | `financial_source_budget`, `financial_source_metadata`, `financial_source_reference` |
| Bank import replay | At most 500 requested account/external-ID keys, using the existing unique index | `financial_source_budget`, `financial_source_metadata`, `financial_source_key` |

These domain errors use the existing HTTP 400 problem-details path. The existing bank-import content conflict remains HTTP 409 `idempotency_conflict`. Authorization, tenant locking, current revision checks and command receipts retain their existing roles. A failed mutating command must not post a successful receipt, audit event or payment.

## Financial completeness

[`FinancialSources`](../src/FirstBlick.Api/FinancialSources.cs) selects the union of **invoice allocations and bank allocations** when reconciling a customer or supplier payment. Invoice allocations include other bank transactions needed for the invoice balance. Bank allocations include other invoices and both original and returned transaction references needed for shared bank capacity. Customer and supplier allocations are both consulted. Returns therefore continue consuming their original bank entry and the linked return entry; a chargeback does not free the original receipt for reuse.

Invoice credits and supplier credits are selected through the corresponding original-invoice link. Reversals consult both customer and supplier allocations on the selected return transaction. Ordinary bank imports only read the requested external identifiers for the specified account. Repeating an import preserves its source digest check; unrelated history is not deserialized.

## Verification

[`EntitySourcesApiTests`](../tests/FirstBlick.Api.Tests/EntitySourcesApiTests.cs) covers exact/over record limits, byte overflow before invalid-body deserialization, tenant/archive isolation, metadata disagreement, worker-policy rejection, selective bank-import replay/conflict, empty targets, target-union deduplication and stale selected links. [`CommercialApiTests`](../tests/FirstBlick.Api.Tests/CommercialApiTests.cs) adds over-budget balance/allocation rejection with no posted receipt or audit; its existing cases exercise concurrent customer/supplier bank capacity, credits, retention, refund and chargeback. [`InvoiceRoundingApiTests`](../tests/FirstBlick.Api.Tests/InvoiceRoundingApiTests.cs) retains its financial metadata/atomic issuance regression.

The local .NET 10.0.402 build passes without warnings. The executed local test run has 601 passing core cases and 19 passing database-independent API cases; PostgreSQL cases are skipped locally and are **not** counted as passed. The unchanged client has 486 passing unit tests and a successful production build. npm audit and the transitive NuGet vulnerability query report no known vulnerable dependencies at this check. Nineteen Python script tests also pass. This is dependency inventory evidence and a scoped manual code review, not an exhaustive security audit or certification.

The local browser run could not launch the pinned Chromium: the download returned an invalid/truncated ZIP. No browser success is claimed from that run. The CI web job installs and launches the pinned browser independently. Required final evidence is the server job with real PostgreSQL and mandatory database tests, web/browser job, official standards jobs and Operations build/smoke/backup/restore/migration/restart workflow at the PR's final commit. Their actual outcomes must be checked before merge; historical runs do not establish a new commit's success.

## Remaining limits and operations

RecordPolicy still reads some complete tenant/kind sets. Its dependency reads now fail explicitly when oversized, so an individual read can be unavailable even if its eventual visible subset is small. Discovery retains its additional combined policy-input budget. Selective authorization queries, a general aggregate request budget, measured database resource limits and representative index/latency/volume testing remain work. The database still evaluates the matching set to calculate count and byte totals; this bounds application materialization, not all PostgreSQL CPU, I/O or temporary storage.

An operator seeing a source-budget error must retain the complete history and investigate source selection and volume. Arbitrary deletion, truncation or a raised limit is not a safe reconciliation procedure. No runtime version, migration, provider contract, deployment credential or normative engineering method is changed by this package. No production or business acceptance is inferred from these tests. Update this document when source selection, limits, error contracts or acceptance evidence change; an operational owner has not been assigned in this repository.
