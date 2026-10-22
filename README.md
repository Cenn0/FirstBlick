# FirstBlick

Business software for roofing contractors, built with **C# / ASP.NET Core 10** and **Angular 22**.

FirstBlick connects customers, buildings, projects and field work with quotations, purchasing and invoicing. The rework follows a [100-task roadmap](docs/ROADMAP.md). Implementation is in progress; task titles are not claims that every feature is available.

Active development takes place in FirstBlick. DachPlaner is retained solely as a reference for the rework and is no longer developed.

## Project documentation

* [Original 46 partial-task closeout register](docs/roadmap-closeout.md) — fixed scope, remaining implementation, dependencies and external acceptance inputs.
* [Advanced moisture/insulation client contracts](docs/advanced-technical-client.md) — input/result checks, interface maxima, immutable preview binding and core-derived browser fixtures.

* [Supplied wind, snow and drainage cases](docs/roof-actions.md) — local signed pressures, horizontal snow zones and independent emergency-flow balances with explicit sources and retained project evidence.

* [PV module layout](docs/pv-module-layout.md) — non-normative rectangular roof-plane placement with exclusions, explicit clearance, and auditable project calculations.

* [Project templates](docs/project-templates.md) — reviewed calendar-day task previews, atomic adoption and retained application/source evidence.
* [Task dependencies](docs/task-dependencies.md) — same-project predecessor rules, cycle checks and guarded start/completion transitions.
* [Project and task activity](docs/business-activity.md) — defined period comparisons, filtered sources and verified CSV/JSON exports.
* [Communication and vehicle-cost originals](docs/ledger-originals.md) — versioned PDF/image attachments with source-specific access and verified downloads after corrections.
* [Customer contacts](docs/customer-contacts.md) — multiple retained people per customer, explicit primary contact, archive/restore and protected legacy fields.
* [Contact change history](docs/customer-contact-history.md) — retained before/after values, recorded actors and explicit primary-contact changes.
* [Customer communications](docs/customer-communications.md) — manual customer/project conversation history, frozen contact snapshots and separate reasoned corrections.
* [Equipment inspections](docs/equipment-inspections.md) and [original reports](docs/equipment-originals.md) — frozen inspection history, availability checks and verified versioned PDF/image originals.
* [Vehicle trips and costs](docs/vehicle-ledger.md) — odometer/driver chronology, retained originals and reversals, and monthly operational summaries.
* [Maintenance workflow](docs/maintenance-workflow.md) and [calendar export](docs/maintenance-calendar.md) — saved occurrences, checklists and revision-bound all-day ICS downloads.
* [Maintenance workdays](docs/maintenance-workdays.md) — explicit weekdays and excluded dates, reviewed scheduling and retained execution-plan snapshots.
* [Checkpoint 11](docs/checkpoint11.md) — evidenced zero-cent final settlements, strengthened cost-source integrity and verified commit-specific CI/Operations.
* [Checkpoint 10](docs/checkpoint10.md) — bounded financial source reads and versioned, audited cost forecasts, with API and browser regression coverage.
* [Checkpoint 9](docs/checkpoint9.md) — preceding finance/UI implementation, exact verification and remaining roadmap work.
* [Roadmap](docs/ROADMAP.md) — complete target scope and acceptance criteria.
* [Roadmap evidence matrix](docs/roadmap-evidence.md) — all 100 original tasks, concrete source/test links and remaining acceptance work. A verified technical core is not a completed business acceptance.
* [Architecture decisions](docs/ARCHITECTURE.md) — server, identity, tenant, data and offline contracts.
* [Source baseline and reference workflows](docs/BASELINE.md) — evidence and limits of the starting point.
* [Background processing](docs/jobs.md) — leases, crash recovery, delivery uncertainty and provider configuration.
* [Technical methods](docs/technical-methods.md) — independent arithmetic, reference cases and applicability limits.
* [Inhomogeneous roof thermal estimate](docs/inhomogeneous-thermal.md) — separated resistance bounds, independent synthetic reference and explicit exclusion of metal bridges or normative approval.
* [Measurement quantity transfer](docs/quantity-links.md) — reviewed quote quantities, retained partial-invoice evidence, material demands and purchase drafts.
* [Source-based cost analysis](docs/cost-analysis.md) — contract/change sources, labor, stock and supplier valuations, plus immutable, manually reviewed forecast versions; missing costs remain unknown.
* [Supplier project-cost allocations](docs/supplier-cost-allocations.md) — auditable invoice-linked project allocation and reversal journal, separate from accounting and payment.
* [Cumulative invoice rounding](docs/invoice-rounding.md) — actual previous cents, retained original positions, corrections and calculation evidence.
* [Collections](docs/collections.md) — reviewed balances, manual reminder steps, disputes and auditable contact history.
* [Operational observations](docs/observability.md) — tenant-authorized queue/document snapshots and bounded process instruments.
* [Bank and export contracts](docs/integrations.md) — read-only CSV bank import and versioned accounting, open-item and payroll-time packages.
* [Search, project history and personal data copy](docs/discovery-data-copy.md) — authorized fields and explicit scope limits.
* [Operations](docs/operations.md) and [canonical migration](docs/migration.md) — actual database/S3 backup, isolated restore, account quarantine, import comparison and controlled restart.

## Start the complete local stack

With Docker Compose, Python 3 and OpenSSL installed:

```bash
./scripts/init-local.sh
./scripts/start-local.sh
```

Open [http://localhost:8080](http://localhost:8080). The initialization script creates random local credentials in the private `.env`; it refuses to overwrite an existing setup. The stack builds the Angular client and API, runs schema migration and the initial personal-account bootstrap, enables versioned S3 storage, and starts the independent worker. Database and S3 ports bind to the local host only.

Compose is a local development setup. Production uses the separate HTTPS, certificate, identity, backup and restart procedure in the operations runbook. The CI Operations workflow exercises the real pinned PostgreSQL 18.6 and SeaweedFS containers, API login/CRUD, backup/restore, migration and application restart with synthetic data.

## Run the server and client

The SDK is pinned by `global.json`; package versions and lockfiles are checked in. Install .NET 10.0.402, Node 24.19.0, PostgreSQL and the DejaVu Sans fonts. Native integration checks use PostgreSQL 16.15; the container acceptance uses PostgreSQL 18.6. Create an empty development database and provide its connection string through the environment. Credentials are never stored in this repository.

```bash
dotnet restore FirstBlick.slnx --locked-mode
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Database='Host=localhost;Database=firstblick;Username=firstblick;Password=your-local-password'
dotnet run --project src/FirstBlick.Api -- --migrate
```

For the first personal managing-director account, set `Bootstrap__Email`, `Bootstrap__Password`, `Bootstrap__DisplayName` and `Bootstrap__TenantName`, then run `dotnet run --project src/FirstBlick.Api -- --bootstrap`. The password must satisfy the server's password rules. Remove the bootstrap password afterward. The bootstrap command refuses a database that already contains a business.

```bash
dotnet run --project src/FirstBlick.Api --urls http://localhost:5080
# In a second terminal:
cd web
npm ci
npm start
```

The Angular development proxy forwards `/api` to the local server. The production build is served from the API's `wwwroot` under one origin. Production additionally requires HTTPS and a shared RSA PFX certificate (`DataProtection__CertificatePath` / `DataProtection__CertificatePassword`) to encrypt the persistent key ring used by the API and worker.

Documents require a configured S3-compatible bucket with versioning enabled. `Storage__Endpoint`, `Storage__Bucket`, `Storage__AccessKey`, `Storage__SecretKey` and optional `Storage__Region` configure it. Uploads verify declared length, file signature, SHA-256 and the returned immutable storage version. No configured storage means an explicit unavailable result. The independent worker is started with `dotnet run --project src/FirstBlick.Worker`; its provider setup is documented above.

## Verification

```bash
dotnet test tests/FirstBlick.Core.Tests
export FIRSTBLICK_TEST_POSTGRES='Host=localhost;Database=postgres;Username=test-user;Password=test-password'
export FIRSTBLICK_REQUIRE_POSTGRES=true
dotnet test tests/FirstBlick.Api.Tests
cd web
npm test
npm run build
npx playwright install --with-deps chromium
npm run test:browser
```

Integration tests create and remove isolated `firstblick_test_*` databases and therefore require a PostgreSQL role with `CREATEDB`. They never reset an application database. CI requires these tests instead of silently skipping missing PostgreSQL. Browser fixture tests exercise the client contract at desktop, tablet and mobile sizes; they are distinct from end-to-end verification against the running API.

Completed real browser captures are retained separately for [renovation through invoicing](docs/evidence/live-workflow-checkpoint4.json), [personal customer-portal sharing and acceptance](docs/evidence/live-portal-checkpoint5.json), and [durable offline field work](docs/evidence/live-offline-checkpoint7.json). Each uses synthetic accounts and data at desktop, tablet and mobile viewport sizes. The offline run uses the production Angular service worker, closes and reopens the entire browser process, requires personal sign-in again and checks that queued time/diary changes reach the real API exactly once. It simulates a workday; it does not claim eight hours of elapsed testing or physical-device acceptance.

The implementation includes explicit commands for frozen quotes, accepted orders, changes, billing, payments and returns, purchasing, stock, planning, forms and maintenance. A source-based project cost report can be explicitly retained as a versioned immutable forecast; read-only previews remain transient and do not post financial transactions. New quantities require a reviewed preview; historic prices, parties and source evidence remain frozen. Personal accounts and current permissions govern the portal and offline queue. The canonical migration drill preserves historical business data and document bytes while keeping imported accounts and outbound work quarantined.

Remaining roadmap acceptance, the real D1/R2 source export and its independently reviewed mapping, external-provider formats, representative production volume and normative technical evidence are tracked separately. No actual production cutover, financial/engineering certification, provider contract or missing licensed dataset is inferred from a green build or synthetic drill.

## Actual browser workflows

Build and serve the production client from an explicitly started isolated API/PostgreSQL/S3 application. Set `FIRSTBLICK_LIVE_URL` to that application origin and provide synthetic personal `FIRSTBLICK_LIVE_EMAIL` / `FIRSTBLICK_LIVE_PASSWORD` credentials. Portal and offline cases additionally use the documented synthetic partner and worker environment variables. Live and offline configurations require the explicit origin; they never select an existing development server implicitly.

`npm run test:live` exercises the online workflows; `npm run test:offline` exercises the production service worker and complete browser restart. The collections scenario requires historical synthetic invoices prepared with the [checked-in fixture runner](docs/collections.md#reproducing-the-historical-live-fixtures). Prepare a fresh manifest before another mutating collections run. The fixture runner is separate from production and only permits a loopback `firstblick_live_*` database and an `@example.test` operator.

`python3 scripts/production_assets.py http://127.0.0.1:8080` checks the actual served shell, enforced CSP and normal screen stylesheet without signing in. Container smoke includes this gate before its authenticated workflow. This caught the critical-CSS inline-activation regression; production now uses `inlineCritical=false` while preserving `script-src 'self'`.

## Development principles

Personal accounts and server-side authorization separate each business's data. Financial operations use explicit, auditable commands. Bank connections are read-only. Technical calculations state their method and limitations. Proprietary demo programs, private records and credentials do not belong in the repository.

An open-source license has not yet been selected by the rights holder. No license grant is inferred from repository visibility.
