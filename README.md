# FirstBlick

Business software for roofing contractors, built with **C# / ASP.NET Core 10** and **Angular 22**.

FirstBlick connects customers, buildings, projects and field work with quotations, purchasing and invoicing. The rework follows a [100-task roadmap](docs/ROADMAP.md). Implementation is in progress; task titles are not claims that every feature is available.

## Project documentation

* [Roadmap](docs/ROADMAP.md) — complete target scope and acceptance criteria.
* [Architecture decisions](docs/ARCHITECTURE.md) — server, identity, tenant, data and offline contracts.
* [Source baseline and reference workflows](docs/BASELINE.md) — evidence and limits of the starting point.

## Development principles

Personal accounts and server-side authorization separate each business's data. Financial operations use explicit, auditable commands. Bank connections are read-only. Technical calculations state their method and limitations. Proprietary demo programs, private records and credentials do not belong in the repository.

An open-source license has not yet been selected by the rights holder. No license grant is inferred from repository visibility.
