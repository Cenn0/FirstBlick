# Checkpoint 11 – Mengenabschluss und gesicherte Kostenquellen

Stand: **20.10.2026**. Technischer Folgecheckpoint zur [100-Aufgaben-Roadmap](ROADMAP.md). Die ursprünglichen Abnahmekriterien und die Statusmethodik der [Nachweismatrix](roadmap-evidence.md) bleiben bestehen. Die folgenden Prüfungen beziehen sich ausschließlich auf ihre genannten Commit-SHAs; eine erfolgreich ausgeführte CI ersetzt keine Fach-, Sicherheits-, Steuer- oder Produktivabnahme.

## 1. Null-Euro-Schlussbeleg als Mengenabschluss

Implementierung: [Commit `5747da8`](https://github.com/Cenn0/FirstBlick/commit/5747da868905c4286f4bf7a7d84f8acb30d3a007), [Rechenregeln](../src/FirstBlick.Core/Business/InvoiceArithmetic.cs), [Workflow](../src/FirstBlick.Core/Business/Finance.cs), [PDF](../src/FirstBlick.Infrastructure/Documents/BusinessPdf.cs), [Vertragsdokumentation](invoice-rounding.md).

Ein vollständig belegter Abschluss mit einem zusätzlichen Bruttobetrag von 0,00 EUR ist technisch nur für eine Schlussrechnung zulässig. Sie muss die gesamte noch offene Vertragsmenge abschließen; bereits ausgestellte Belege müssen tatsächlich positive Centbeträge enthalten, und jede Schlussposition muss eine positive neue Menge ohne zusätzliche Netto-/Steuer-Cents beitragen. Ein eigenständiger Nullbeleg, eine Null-Euro-Teilrechnung und eine Null-Euro-Korrektur bleiben unzulässig; ebenso Übermengen oder negative Centdifferenzen. Der neue Beleg besitzt eine eigene Nummer, eingefrorene Ausgangsquellen, vorherige Rechnungsdigests und nachvollziehbare kumulative Positionen. Das PDF bezeichnet die Abwicklung ausdrücklich als Mengenabschluss ohne zusätzlichen Zahlbetrag.

Neue synthetische [Core-Fälle](../tests/FirstBlick.Core.Tests/Business/InvoiceCumulativeRoundingTests.cs) prüfen positive und negative Grenzen; der [HTTP-/PostgreSQL-Fall](../tests/FirstBlick.Api.Tests/InvoiceRoundingApiTests.cs) prüft Belegerstellung, Idempotenz-Replay, Nullforderung, gespeicherten Snapshot und Nummernzähler.

**Tatsächlich veröffentlichte Workflow-Ergebnisse für denselben Commit:**
- [CI erfolgreich](https://github.com/Cenn0/FirstBlick/actions/runs/39440735290).
- [Operations erfolgreich](https://github.com/Cenn0/FirstBlick/actions/runs/39440735303).

Die technische Regel bestätigt weder die steuerrechtliche Anerkennung des Abschlussdokuments noch die Vollständigkeit vorgeschriebener Rechnungspflichtfelder.

## 2. Mandanten-/Referenzintegrität von Nachkalkulationsquellen

Implementierung: [Commit `9cb889a`](https://github.com/Cenn0/FirstBlick/commit/9cb889a40d68fee60ea22105ce178ebd68cd8d50), [Kosten-API](../src/FirstBlick.Api/CostEndpoints.cs), [Regressionsfall](../tests/FirstBlick.Api.Tests/CostApiTests.cs), [fachliche Dokumentation](cost-analysis.md).

Die Quellabfragen für freigegebene Projektzeiten, Materialbewegungen, angenommene Nachträge, Bestellungen, Lieferantenrechnungen und gebuchte Lieferantenanteile filtern beide Datenbankseiten ausdrücklich nach Tenant. Bei verknüpften Quellen werden gespeicherte ID, Typ, Tenant, Revision, Archivzustand und fachliche Zielreferenz gegen den JSON-Inhalt geprüft. Entsprechende Metadaten werden für die Lieferanten- und Zuordnungsquelle vor der Kostenbewertung überprüft. Abweichungen brechen mit definierten Fehlercodes ab, statt verdächtige Werte in Istkosten oder eine gespeicherte Version aufzunehmen.

Ein neuer echter HTTP-/PostgreSQL-Test verändert die serialisierte Revision einer Zeitquelle bei unveränderten Tabellenspalten. Vorschau und Speicherung einer historisierten Prognose müssen die Quelle abweisen; Daten-, Audit-, Outbox- und Idempotenz-Bestände dürfen dabei nicht anwachsen.

**Tatsächlich veröffentlichte Workflow-Ergebnisse für denselben Commit:**
- [CI erfolgreich](https://github.com/Cenn0/FirstBlick/actions/runs/39441264392).
- [Operations erfolgreich](https://github.com/Cenn0/FirstBlick/actions/runs/39441264387).

## 3. Zuvor umgesetztes Lieferantenkostenjournal: dokumentierte Abgrenzung

Die bereits vor diesem Checkpoint vorhandene [SupplierCostAllocation-Implementierung](../src/FirstBlick.Core/Business/SupplierCostAllocation.cs) und [API](../src/FirstBlick.Api/SupplierCostAllocationEndpoints.cs) erzwingen explizite, positive Netto-Zuordnungen genehmigter Eingangsbelege innerhalb einer mandantengebundenen Transaktionssperre. Sie begrenzen die Summe über mehrere Projekte, halten jede Buchung samt Aufhebung unveränderlich fest und prüfen den Projektbezug von Gutschriften. Die [separate Beschreibung](supplier-cost-allocations.md) dokumentiert die API und die bewusst nicht übernommenen Buchhaltungsaufgaben. Dieser Checkpoint beansprucht nicht, dass das Journal erst mit den beiden oben genannten Commits eingeführt wurde.

## 4. Ausstehende Arbeiten und unabhängige Freigaben

- Versionsgebundener XML-/Schematron-Vertrag, Import-/Exportkette und Prüfung für E-Rechnungsprofile.
- Steuerliche Fachabnahme des Null-Euro-Mengenabschlusses und der Rechnungsprofile anhand verbindlicher Referenzfälle.
- Reale Lieferanten-/Lohn-/Material- und Buchhaltungsdaten, echte D1-/R2-Altbestände sowie ein fachlich bestätigter Kostenstellenplan.
- Messung von Last, Datenbank-Indexeffekten und Kapazitätskosten bei realistisch großen Mengen; externe Telemetrie, Alarmierung und SLOs.
- Normative bauphysikalische Verfahren mit lizenzierten Quellen und herstellerabhängigen Parametern; deren Eignung kann nicht aus kaufmännischen Prüfungen hergeleitet werden.

**Statusregel:** Keine der 100 Ursprungstasks wird allein anhand eines grünen CI-Laufs als vollständig fachlich abgenommen markiert.
