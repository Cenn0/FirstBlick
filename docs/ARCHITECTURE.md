# Architecture decisions

## ADR-001: server and browser application

FirstBlick is an independently implemented server application for roofing businesses. The binding stack is Angular 22, ASP.NET Core 10, EF Core with PostgreSQL, S3-compatible object storage, and a separate .NET worker. The browser and API share an origin. The application is a modular monolith; business calculations have no HTTP or persistence dependency.

The .NET SDK is pinned in `global.json`, server packages in `Directory.Packages.props`, and browser packages in `web/package-lock.json`. Supported runtime combinations are checked against [Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) and [Angular's version compatibility](https://angular.dev/reference/versions). Dependency updates must pass the same tests as application changes.

## ADR-002: identity, tenants, capabilities

Personal ASP.NET Core Identity accounts and business memberships are separate. An authenticated cookie identifies a person; it never grants a tenant or a role by itself. The server resolves active membership on every request. A client-provided tenant identifier is a selector, not authorization. Membership revocation therefore takes effect on the next request.

Mutation requests require antiforgery validation. HttpOnly authentication cookies, SameSite restrictions, persistent data-protection keys and explicit key backup form one session contract. No authentication bearer token is stored in browser local storage.

Capabilities are enforced at API and job execution boundaries. Modules can prohibit new actions while retaining authorized access to historical data. Critical approvals bind requester, different approver, tenant, action, exact object revision and content digest. Changing an object invalidates its approval. Multiple managing directors retain individual identities.

## ADR-003: persistence and business invariants

Tenant-scoped entities carry a revision used as an optimistic concurrency token. Relational tenant keys and reference checks complement query filters. Financial transitions use explicit commands; unrestricted entity editing must never change accepted quotes, issued invoices, imported bank transactions or approval state.

Business changes, audit entries, idempotency receipts and queued work are committed in one PostgreSQL transaction. External delivery occurs after commit. A worker uses persistent leases; ambiguous external acceptance requires reconciliation and is never blindly retried. Audit records contain actor, tenant, operation, object and revision, without passwords or document bodies.

Amounts use .NET decimal with explicit rounding. Historical effective calculation inputs are frozen. User-supplied tax rates are calculation inputs; support for a legally valid invoice profile requires separate validation of that concrete format and tax case.

## ADR-004: files and offline data

Objects use tenant-owned opaque keys, version metadata, size limits and SHA-256 checksums. Uploaded content cannot become executable application content. Missing or mismatched objects produce an explicit repairable state. The database and object storage cannot share a transaction: staged upload/finalization and reconciliation preserve the distinction.

Offline storage is opt-in and scoped to person and tenant. The durable queue permits only explicitly supported field-work commands, preserves idempotency keys across restarts, stops on permission errors and surfaces revision conflicts. Browser storage cannot enforce a permission change while physically offline; synchronization must reauthorize each command. Financial and approval actions remain online.

## ADR-005: evidence and external boundaries

The original report is a requirements source, not evidence of implementation. The checked legacy source is not proven identical to the previously hosted website. No proprietary demo binaries, extracted program resources, private records, credentials or third-party source are published here. Basic geometry and thermal calculations may be independently authored with clear input assumptions and exact reference tests. Normative moisture, drainage, wind, snow, fixing and structural claims require verified methods, data rights and independent reference cases.

Provider credentials, contract choices, a live-data copy and a license decision remain external inputs. An adapter interface alone does not count as a working provider integration. All 100 roadmap tasks remain in scope and retain their IDs; evidence and unfulfilled conditions belong in the implementation report.

