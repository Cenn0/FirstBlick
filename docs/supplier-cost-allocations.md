# Projektbezogene Lieferantenkosten – verbindliches Journal

Dieses technische Journal ist ausdrücklich **kein Buchhaltungsjournal, keine Zahlung und keine Buchung auf ein externes Kostenstellenkonto**. Es bindet einen vom Anwender überprüften Nettoanteil eines genehmigten Eingangsbelegs an ein konkretes Projekt, um doppelte projektübergreifende Zuordnungen zu verhindern.

## Fachvertrag und Abgrenzung

- Jede Zuordnung verwendet die freigegebene Eingangsbeleg-ID, Projekt-ID, positiven Nettobetrag mit exakt zwei Dezimalstellen, prüfbaren Genehmigungsdigest, Bearbeiter, UTC-Zeit und Begründung. Der eigene Digest bindet diese Felder.
- Alle Änderungen werden als **neue** Journaleinträge erfasst. Eine Stornierung schreibt eine referenzierende Gegenbuchung, keine Mutation des Ursprungs. Eine doppelte Stornierung wird zurückgewiesen.
- Die Summe aller aktiven Zuordnungen eines Eingangsbelegs über **alle** Projekte darf den genehmigten Belegnetto-Betrag nicht übersteigen. Der Server prüft den Saldo innerhalb des vorhandenen mandantenbezogenen Transaktionslocks, einschließlich konkurrierender Zugriffe.
- Zuordnungen für Lieferantengutschriften sind positive Journalbeträge, die im Kostenbericht als **negative** Projektkosten dargestellt werden. Eine Gutschrift für ein Projekt darf den dort aktiv zugeordneten Anteil der zugehörigen Originalrechnung nicht übersteigen. Die Rücknahme des Originals wird verhindert, solange zugehörige Gutschriften noch gültig projektbezogen wirken.
- Ungebuchte, manuell in der Kostenberichtsvorschau deklarierte Anteile können weiterhin projektübergreifend überschneiden. Sobald ein Eingangsbeleg gebuchte Zuordnungen besitzt, darf er im Bericht nicht zusätzlich als freie manuelle Basis verwendet werden.

## Endpunkte

| HTTP | Pfad | Zweck |
|---|---|---|
| POST | `/api/v1/supplier-invoices/{id}/cost-allocations` | Genehmigten Eingangsbeleg mit Projekt, Nettoanteil, Begründung und `expectedSourceDigest` ausdrücklich zuordnen |
| POST | `/api/v1/supplier-cost-allocations/{id}/reverse` | Gebuchten Anteil mit Begründung vollständig aufheben |
| GET | `/api/v1/supplier-invoices/{id}/cost-allocations` | Journal, aktive IDs, zugeordneter und verfügbarer Nettoanteil |
| GET | `/api/v1/supplier-cost-allocations/{id}` | Einzelbeleg mit gespeicherter Herkunft und Digest |
| GET | `/api/v1/projects/{id}/supplier-cost-allocations` | Projektbezogene Belege und aktive IDs; `grossPostedNet` ist die unvorzeichenbehaftete Summe aktiver Zuordnungen, **nicht** das saldierte Projektergebnis |

Schreibbefehle benötigen den bestehenden idempotenten Kommando-Vertrag. Zusätzlich werden die aktuelle Mandantenmitgliedschaft, Rolle, aktiviertes Modul und Wiederherstellungssperre serverseitig geprüft; Zugriff auf dieses Journal ist Geschäftsführung/Büro vorbehalten. Lesende Antworten werden `no-store, private` ausgeliefert. Das Journal ist auf höchstens 5.000 Einträge je relevanter Lesesammlung begrenzt und schneidet bei Überschreitung nicht still ab.

## Quellcode und Nachweise

- [Domäne](../src/FirstBlick.Core/Business/SupplierCostAllocation.cs) und [HTTP-Endpunkte](../src/FirstBlick.Api/SupplierCostAllocationEndpoints.cs)
- [Domänentests](../tests/FirstBlick.Core.Tests/Business/SupplierCostAllocationTests.cs) und [HTTP-/PostgreSQL-Tests](../tests/FirstBlick.Api.Tests/SupplierCostAllocationApiTests.cs)
- [Kostenbericht und Forecast-Integration](cost-analysis.md), [Kosten-API](../src/FirstBlick.Api/CostEndpoints.cs)

Die synthetischen Prüfungen decken überprojektliche Höchstbeträge, Konkurrenzzugriffe, Gegenbuchungen, Original-/Gutschrift-Bezug, Rollengrenzen, Mandantenisolation und gebuchte Projektnachkalkulation ab. Sie ersetzen keine unabhängige betriebliche Kontierungs-, Vollständigkeits- oder steuerliche Prüfung.
