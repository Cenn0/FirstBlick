# Checkpoint 10 – Begrenzte Rechnungsquellen und versionierte Nachkalkulation

Stand: **20.10.2026**. Ergänzender technischer Checkpoint zu [Checkpoint 9](checkpoint9.md). Er schließt nicht die 100 Roadmap-Aufgaben vollständig ab. Für die Gesamtübersicht gilt weiterhin die [Nachweismatrix](roadmap-evidence.md).

## 1. Datenbankgestützte Rechnungsquellen statt Mandantenvollabzug

[FinancialSources.cs](../src/FirstBlick.Api/FinancialSources.cs) lädt Rechnungen eines Auftrags, Korrekturbezüge, Auftragsnachträge und Zahlungszuordnungen über persistierte Entitätslinks, tenantbezogene Abfragen und ein explizites Quellenbudget. Die bisherige Rechnungsausgabe, Saldenlesung und Auftragskostenvergleichsvorschau laden dadurch nicht mehr grundsätzlich alle entsprechenden Mandantendatensätze. Bei einem überschrittenen Limit wird der gesamte Vorgang abgewiesen; es werden keine abgeschnittenen Historien berechnet.

Quellmetadaten (ID, Tenant, Revision, Archivzustand) sowie die referenzierte Zielentität werden vor der fachlichen Rechnungsauswertung überprüft. Die Änderungen nutzen den vorhandenen Geschäfts-Transaktionslock und bestehen aus [Implementierung](../src/FirstBlick.Api/CommercialEndpoints.cs), [PostgreSQL-Regressionen](../tests/FirstBlick.Api.Tests/InvoiceRoundingApiTests.cs) und [Methodendokumentation](invoice-rounding.md).

**Relevante Grenze:** Es gibt nach wie vor mandantweite Lesehistorien an anderen Finanzendpunkten. Eine Anfrage auf Korrekturrechnungen kann konservativ auch Rechnungen laden, deren eingefrorene Berechnung das Ausgangsdokument als Vorbeleg referenziert; die Fachlogik berücksichtigt ausschließlich tatsächlich zur Ursprungsrechnung gehörende Korrekturen. Ein Lastnachweis für hohe echte Belegzahlen und ein feingranularer separater Korrekturindex fehlen.

## 2. Historisierte und manuell geprüfte Prognosen

Eine neue [Kostenprognose-Entität](../src/FirstBlick.Core/Business/CostForecast.cs) speichert die geprüften Annahmen, Quellenrevisions- und Inhaltsdigests, verbleibende Lücken, Berichtsversion, Bearbeiter, Zeitpunkt und Begründung. Die Historie besitzt fortlaufende Versionsnummern und einen expliziten Vorgängerbezug. Sie ist nicht über generische Erstellungs-, Änderungs- oder Löschbefehle manipulierbar.

Die [dedizierten API-Endpunkte](../src/FirstBlick.Api/CostEndpoints.cs) sind:

- `POST /api/v1/orders/{id}/cost-analysis` – aktuelle lesende Vorschau, keine Ablage;
- `POST /api/v1/orders/{id}/cost-forecasts` – Vorschau-Eingabe und erwarteten Digest unter Tenant-Lock neu verifizieren, anschließend versioniert und auditiert speichern;
- `GET /api/v1/orders/{id}/cost-forecasts` – begrenzte Versionshistorie;
- `GET /api/v1/cost-forecasts/{id}` – unveränderten historischen Stand lesen.

Der Speichervorgang ist idempotent, erfordert die explizite Begründung und lehnt veraltete Quellen als `409 stale_cost_report` ab. Alle Endpunkte sind ausschließlich für Geschäftsführung und Büro nutzbar. Das [Angular-Modul](../web/src/app/pages/cost-analysis.component.ts) erlaubt Quellbewertung, bewusste Speicherung, Versionsübersicht, historischen Vergleich der Kennzahlen und Export eines einzelnen gespeicherten Berichts.

## 3. Reproduzierbare automatisierte Regressionen

Die [echten HTTP-/PostgreSQL-Tests](../tests/FirstBlick.Api.Tests/CostApiTests.cs) prüfen:

- unabhängige Prognoseversionen mit verketteter Vorgängerversion und konservierter Quelle;
- idempotente Wiederholung eines Speicherungskommandos;
- Konflikt eines veralteten Kostenreports, bevor ein dauerhafter Datensatz entsteht;
- Rollen- und Mandantentrennung sowie die Unerreichbarkeit einer generischen Mutation;
- Erkennung von manipuliertem JSONB im gespeicherten historischen Bericht.

Die [Browserfixtures](../web/tests/browser/cost-analysis.spec.ts) prüfen Versionserfassung, Begründung, historische Übersicht/JSON-Export und stale-report-Ablehnung an Desktop-, Tablet- und Mobil-Viewports.

**Veröffentlichte Ausführungsnachweise:** [Commit 28330c2](https://github.com/Cenn0/FirstBlick/commit/28330c2d43ccae4cc8be89a620d9bf6645388434) erhielt sechs erfolgreiche CI-/Operations-Checks einschließlich des echten Datenbanktests und Container-Restore. Im neuen Forecast-Zweig wurden beim [Zwischenstand 5ce43aa](https://github.com/Cenn0/FirstBlick/commit/5ce43aaa21ff5fcdb1fb345938246993756d6a74) **251 Core- und 131 API-/PostgreSQL-Tests** erfolgreich ausgeführt. Der erste Browserdurchlauf zeigte drei gleiche Selektorfehler auf den drei Viewports; die letzte Änderung korrigiert den mehrdeutigen Text-Selektor. **Ob die abschließenden CI- und Operations-Jobs erfolgreich sind, muss am jeweils neuesten Commit überprüft werden; die Zwischenstände ersetzen diesen Nachweis nicht.**

## 4. Was bewusst noch offen bleibt

Die Fachfunktion ist eine **konsistente historisierte Kosteneinschätzung**, keine Finanzbuchhaltung, keine verbindliche Kostenstellenverteilung, keine reale Lohn-/Lohnnebenkostenautomatik und keine Bestätigung von Umsatzsteuer- oder GAEB-Profilen. Offene Quellen werden weiterhin als unbekannt gekennzeichnet. Historische Quellen dürfen nicht fälschlich als garantiert vollständig gelten.

Die Gesamtroadmap umfasst weiter Produkt-, Rechts-/Normdaten-, Anbieter-, Datenschutz-, Last- und reale Betriebsabnahmen. Ein erfolgreiches synthetisches CI-/Restore-Szenario belegt die getesteten Funktions- und Integrationsverträge, **nicht** automatisch die Freigabe eines realen Dachdeckerbetriebs.

