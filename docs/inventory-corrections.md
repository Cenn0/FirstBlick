# Lagerkorrekturen, Rückgaben und Umlagerungen – DP-07-03 / DP-07-04

Stand: 21.10.2026. Technischer Teilumfang; **kein** Abschluss der ursprünglichen betrieblichen Abnahme und **keine** Auswirkung auf Buchhaltung oder Lieferantenzahlungen.

## Der unveränderliche Lagerstrom

Das bestehende PostgreSQL-/JSONB-Entities-Modell und die versionierte Auditspur werden weiterverwendet.
Lagerbewegungen können nicht per generischer CRUD-API geschrieben, verändert oder archiviert werden.
Alle neuen Befehle verwenden dieselben bestehenden Prüfungen für Mandant, Modul, aktuelle Rolle,
Idempotency-Key, atomaren Datenbank-Commit und den tenantweiten Schreib-Lock. Der Lock
serialisiert die Prüfung des Lagerbestands mit der tatsächlichen Buchung gegen konkurrierende Aktionen
innerhalb derselben Anwendung.

| Befehl | Ziel | Schutz- und Bestandsregeln |
|---|---|---|
| `POST /api/v1/materials/{id}/stock-count` | Physische Zählung | `expectedOnHand` muss dem aktuellen Buchbestand am angegebenen Lagerort entsprechen; `countedOnHand` ist der **physisch gezählte Gesamtbestand**, kein Delta. Die Differenz wird als signierter `Adjustment` gebucht; es darf kein Bestand unterhalb offener Reservierungen entstehen. Positiver/negativer Zählbeleg und Grund sind erforderlich. Kein Null-Delta-Ereignis. |
| `POST /api/v1/materials/{id}/transfer` | Lagerortwechsel | Quelle und Ziel verschieden; Menge positiv; Quellbestand aktuell und **frei verfügbar**. Es entstehen exakt zwei unveränderliche Bewegungssätze `TransferOut` und `TransferIn` in einer Transaktion mit gleicher Korrelationsreferenz. |
| `POST /api/v1/material-reservations/{id}/return` | Restmaterial aus Projekt | `If-Match` der Reservierung, ID der unveränderten **ursprünglichen Verbrauchsbuchung**, verfügbare Rückgabemenge und Grund. Rückgabe ist `Return` mit identischer Originalbewertung und mit der ursprünglichen Buchungs-ID als Referenz. Gebuchte Verbrauchs- und Freigabemengen der Reservierung werden atomar korrigiert. Eine zweite Rückgabe auf denselben Originalbeleg kann nicht mehr als den ursprünglichen Verbrauch kreditieren. |
| `POST /api/v1/purchase-orders/{id}/supplier-return` | Retour an Lieferant | `If-Match` des unveränderten Auftrags, bestehende Bestellposition, physisch verfügbarer Bestand und noch nicht retournierte Eingangsmenge erforderlich. Neues Bewegungskennzeichen `SupplierReturn` mit negativer Menge, getrennt von positiver Projekt-`Return`. Netto-Wareneingänge werden reduziert; zuvor als `Received` geführte Bestellungen gehen ggf. nach `PartiallyReceived` zurück. Referenz zu RMA/Gutschrift und Grund sind Pflicht. |

## Integrität, Rollen und Nachweise

- **Funktioniert grundsätzlich:** Doppelte Requests mit identischem Schlüssel werden vom vorhandenen Command-Receipt wiedergegeben; abweichende Wiederverwendung derselben ID wird verworfen. Falsche Versionen, Mandanten und fehlende Lagerberechtigung werden abgewiesen.
- **Offiziell unterstützt:** Dokumentiert sind die vier obigen API-Befehle und der Angular-Arbeitsplatz `/inventory-controls` für ManagingDirector, Office und SiteManager im aktivierten Einkaufsmodul. Diese Einschränkungen sind ein Anwendungs-Vertrag; keine produkt- oder branchenübergreifende Konformitätsbestätigung.
- **Best Practice:** Vor jeder physischen Korrektur müssen Zähl- und Schadensbelege sowie Inventurverantwortung außerhalb des UI geprüft sein. Externe Einkaufs- und Steuerdokumente, tatsächliche Lieferantenannahme, zusätzliche Lagerorte und Bewertungsmethoden müssen fachlich abgestimmt werden. Inventurposten sind als einzelne Gegenbuchungen nachvollziehbar, nie als nachträgliche Änderung des alten Zugangs.
- **Sicherheitsseitig empfehlenswert:** Keine anonyme oder generische Lagerbuchung, keine stille Lagerwertschätzung, keine Umgehung offener Reservierungen, keine automatischen Provideraktionen. Mobile-/Tablet-Ansichten werden im Browser-Viewport getestet; das ersetzt keine Prüfung realer Hardware oder Offline-Buchungen. Lagerkorrekturen werden bewusst **nicht** offline vorgemerkt.

### Buchhalterische und fachliche Grenzen

Eine `SupplierReturn` erzeugt **keine** Lieferantengutschrift, Kontokorrentbuchung,
Steuerkorrektur, Zahlung oder Finanzabschluss. Diese Dokumente müssen über die vorhandenen
Beleg- und Prüfworkflows separat geführt und mit der Retoure abgeglichen werden.
Ein `Return` aus einem Projekt führt den Originalkostenwert gegen den verbrauchten Materialanteil zurück.
Eine Inventurdifferenz und eine Lagerumlagerung sind **keine** projektbezogenen Verbrauchskosten.
Weitere offene Fälle: vollständige Mehrlagerpraxis, inventarische Bewertungsverfahren,
verfolgte Serien-/Chargennummern, physische Scanner/Sensorik, dokumentierte reale Inventurprüfung
und betriebliche Freigabe der Materialkostenzuordnung.

### Testpyramide

- Core: Mengenbilanz, Reservierung, Stammdaten-/Mandantengrenze, veraltete Bestände, Null-Differenzen,
  Mengenüberschreitungen, doppelte Projekt-Rückgaben und Bestellung nach Lieferantenretoure.
- HTTP/PostgreSQL: echte API-Kommandos mit Identity- und CSRF-Cookies, Idempotenz-Replay,
  `If-Match`-Revisionen, Rollensperren, Mandantenisolation, Audit- und Ledgerpersistenz.
- Angular/Playwright: die vier sichtbaren Workflows und die Sperre für die Mitarbeiterrolle auf
  den konfigurierten Chromium-Viewports. Das Browser-Backend verwendet kontrollierte Fixtures;
  die reale PostgreSQL-Kette wird separat von den API-Tests abgedeckt.

**Roadmap-Bewertung:** Die bis dahin dokumentierten Restlücken in DP-07-03 (Bestandskorrektur
und Rücklieferung) und DP-07-04 (Projektmaterial-Rückgabe, kontrollierter Lagerortwechsel)
werden technisch adressiert. **DP-07-03 und DP-07-04 sind damit nicht als ganzheitlich oder
betrieblich abgeschlossen einzustufen.** Die ursprünglich dokumentierten übrigen 47 K-Aufgaben
ändern ihren Status durch diesen Pull Request nicht.
