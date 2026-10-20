# FirstBlick

Business software for roofing contractors, built with **C# / ASP.NET Core 10** and **Angular 22**.

FirstBlick connects customers, buildings, projects and field work with quotations, purchasing and invoicing. The rework follows a [100-task roadmap](docs/ROADMAP.md). Implementation is in progress; task titles are not claims that every feature is available.

## Project documentation

* [Roadmap](docs/ROADMAP.md) — complete target scope and acceptance criteria.
* [Architecture decisions](docs/ARCHITECTURE.md) — server, identity, tenant, data and offline contracts.
* [Source baseline and reference workflows](docs/BASELINE.md) — evidence and limits of the starting point.
* [Background processing](docs/jobs.md) — leases, crash recovery, delivery uncertainty and provider configuration.
* [Technical methods](docs/technical-methods.md) — independent arithmetic, reference cases and applicability limits.

## Run the server and client

The SDK is pinned by `global.json`; package versions and lockfiles are checked in. Install .NET 10.0.402, Node 24.19.0, PostgreSQL 16 and the DejaVu Sans fonts. Create an empty development database and provide its connection string through the environment. Credentials are never stored in this repository.

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

The implementation includes explicit commands for frozen quotes, accepted orders, changes, billing, payments and returns, purchasing, stock, planning, forms and maintenance. Remaining roadmap acceptance, actual legacy migration, external-provider validation and normative technical evidence are tracked separately; a green build does not establish production or professional approval.

## Development principles

Personal accounts and server-side authorization separate each business's data. Financial operations use explicit, auditable commands. Bank connections are read-only. Technical calculations state their method and limitations. Proprietary demo programs, private records and credentials do not belong in the repository.

An open-source license has not yet been selected by the rights holder. No license grant is inferred from repository visibility.
