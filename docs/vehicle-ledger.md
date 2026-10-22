# Fahrten und Fahrzeugkosten

Stand: 22.10.2026 · FirstBlick · **DP-07-05**, technischer Teilumfang.

Unter **Material & Geräte → Fahrten & Fahrzeugkosten** stehen Fahrten und betriebliche
Kosten für Ressourcen vom Typ **Fahrzeug** zur Verfügung. Der Arbeitsplatz bietet
Fahrzeugsuche, Monatswahl, getrennte Fahrten- und Kostenlisten und eine Monatsübersicht.
Der Gerätebereich verlinkt ebenfalls die Fahrzeugakte.

## Fahrten erfassen

Eine Fahrt benötigt einen aktiven Mitarbeiter als Fahrer, Abfahrt und Ankunft,
Kilometerstand vor und nach der Fahrt, Start, Ziel und Fahrtzweck. Projekt und Hinweise
sind optional. Die Fahrt muss abgeschlossen sein, darf höchstens 24 Stunden dauern
und verwendet UTC-Zeitpunkte mit ganzen Sekunden. Die Oberfläche zeigt und erfasst
die Betriebszeitzone; doppelte Ortsstunden verlangen die ausdrückliche Wahl des
Zeitversatzes. Kilometerstände unterstützen bis zu drei Nachkommastellen und müssen
zwischen 0 und 9.999.999,999 km liegen. Der Endstand muss größer als der Anfangsstand sein.

Der Server prüft die **gesamte aktive Fahrthistorie**, einschließlich anderer Monate:

- Ein Fahrzeug darf keine zeitlich überlappenden Fahrten haben.
- Ein Fahrer darf auch in verschiedenen Fahrzeugen nicht gleichzeitig fahren.
- Kilometerstände müssen zu früheren und späteren Fahrten desselben Fahrzeugs passen.
  Rückwirkende Einträge dürfen diese Reihenfolge nicht verletzen.

Lücken zwischen Kilometerständen sind zulässig und werden nicht als dokumentierte
Strecke mitgezählt. Das Register ist eine betriebliche Aufzeichnung; es behauptet
weder vollständige Fahrzeugnutzung noch ein steuerlich anerkanntes elektronisches
Fahrtenbuch. Einsatzfreigabe und Prüfungsfälligkeit bleiben Aufgabe des
[Geräteprüfungsablaufs](equipment-inspections.md). Eine vergangene Fahrt kann auch bei
aktuell gesperrtem Fahrzeug dokumentiert werden.

## Kosten erfassen und auswerten

Ein Kostenposten enthält Belegdatum, Kostenart, positiven EUR-Betrag mit höchstens zwei
Nachkommastellen, Belegreferenz sowie optional Projekt und Hinweise. Das Belegdatum
darf nicht nach dem aktuellen Betriebstag liegen. Kostenarten sind Kraftstoff,
Wartung, Reparatur, Versicherung, Kfz-Steuer, Maut, Parken und Sonstiges.

Der Betrag ist der ausdrücklich eingegebene Kostenwert. Es gibt keine automatische
Umsatzsteuerermittlung, Verknüpfung mit einer Lieferantenrechnung, Zahlungsbuchung oder
Übernahme in die bestehende Nachkalkulation. Die Belegreferenz ist Text; zusätzlich
lassen sich [Originaldateien](ledger-originals.md) als PDF, PNG oder JPEG am
Kostenoriginal speichern und verifiziert herunterladen. Derselbe Referenztext kann mehrfach
vorkommen; er ist keine betriebsweite Dublettenkennung.

Die Monatsübersicht zählt alle aktiven Fahrten und Kosten des Monats sowie getrennt
die stornierten Originale. Fahrten werden vollständig dem Startmonat in der
Betriebszeitzone zugeordnet, Kosten ihrem Belegdatum. Eine Fahrt über eine Monatsgrenze
wird nicht auf zwei Monate verteilt. Die Summen berücksichtigen alle nicht stornierten
Originale des Monats, unabhängig von der angezeigten Listenseite. Die Listen liefern
jeweils 20 Einträge pro Seite.

**Erfasste Kosten / Strecke** ist die Summe der aktiven Kosten geteilt durch die
dokumentierte Strecke, auf vier Nachkommastellen kaufmännisch gerundet. Bei null
Kilometern gibt es keinen Quotienten. Dieser Wert ist keine Vollkostenrechnung und
kein verbindlicher Kilometerpreis; fehlende Fahrten, Abschreibungen oder Kosten werden
nicht geschätzt.

## Originale, Stornierung und historische Stammdaten

Jeder Eintrag erhält einen Digest über Kennung, Betrieb, Inhalt, Originalbezüge und
Erfassungsdaten. Fahrzeugbezeichnung, Inventarnummer, Kennzeichen, Fahrername und
optionaler Projektname werden zum Erfassungszeitpunkt festgehalten. Spätere
Umbenennungen oder eine Inaktivierung des Fahrers verändern diese Momentaufnahmen nicht.

Originale werden nicht überschrieben. Eine begründete Stornierung ist ein separater,
unveränderlicher Datensatz mit exakter Originalkennung und Originaldigest. Sie nimmt
die Strecke beziehungsweise den Betrag aus der aktiven Monatsauswertung; Original
und Grund bleiben sichtbar. Ein Original kann nur einmal storniert werden. Eine
korrigierte Fahrt wird danach neu erfasst und erneut gegen die aktive Historie geprüft.

Vorhandene Originale verhindern über die allgemeinen Referenzregeln eine Archivierung
der verwendeten Fahrzeug-/Fahrer-/Projektstammdaten. Bereits archivierte historische
Quellen aus einer geprüften Migration bleiben lesbar; eine Stornierung bleibt auch
bei einem später archivierten Fahrer oder Projekt möglich. Ein Fahrzeug mit
aufbewahrten Einträgen kann nicht in einen anderen Gerätetyp umgewandelt werden.

Geschäftsführung, Büro und Bauleitung dürfen erfassen und stornieren. Buchhaltung darf
lesen. Für neue Einträge und Stornierungen muss `purchasing` aktiv sein; vorhandene
Akten bleiben mit Leserecht lesbar. Aktuelle Rechte werden auch vor einem
Idempotenz-Replay geprüft. Jede Änderung benötigt `If-Match` mit der aktuellen
Fahrzeugrevision und einen `Idempotency-Key`; sie erhöht die Fahrzeugrevision gemeinsam
mit Original/Storno, Audit, Outbox und Befehlsbeleg. Fremde Betriebsdaten werden weder
gelesen noch als Quellen verwendet.

## Schnittstellen

| Methode und Pfad | Vertrag |
|---|---|
| `GET /api/v1/vehicle-ledger/vehicles?search=&page=1` | Aktive Fahrzeugstammdaten, 25 Treffer je Seite. |
| `GET /api/v1/equipment/{id}/vehicle-ledger?month=2026-10&tripPage=1&costPage=1` | Fahrzeugrevision, Betriebstag/-zeitzone, Monatsübersicht sowie getrennte Original-/Stornolisten. Der Monat wird ausdrücklich übergeben. |
| `POST /api/v1/equipment/{id}/vehicle-trips` | Fahrer, optionales Projekt, Start/Ende, Kilometerstände, Zweck, Start/Ziel und Hinweise; liefert Original und neue Fahrzeugrevision. |
| `POST /api/v1/equipment/{id}/vehicle-costs` | Optionales Projekt, Datum, Kostenart, Betrag, `currency: "EUR"`, Referenz und Hinweise. |
| `POST /api/v1/equipment/{id}/vehicle-trips/{entryId}/reverse` | `{sourceDigest,reason}`; erhält das Fahrtoriginal und ergänzt einen Stornobeleg. |
| `POST /api/v1/equipment/{id}/vehicle-costs/{entryId}/reverse` | `{sourceDigest,reason}`; erhält das Kostenoriginal und ergänzt einen Stornobeleg. |

Die Oberfläche prüft Betrieb, Fahrzeug, Revision, Originalidentität, Quellenbindung
und Dezimalwerte. Antworten nach Betriebs-, Rechte-, Fahrzeug-, Monats- oder
Seitenwechsel werden verworfen. Ein Konflikt lässt Eingaben zur Klärung stehen und
verlangt vor dem nächsten Schreibvorgang ein Neuladen.

## Technische Nachweise und weitere Roadmap

- [Core](../src/FirstBlick.Core/Business/VehicleLedger.cs),
  [Coretests](../tests/FirstBlick.Core.Tests/Business/VehicleLedgerTests.cs),
  [API](../src/FirstBlick.Api/VehicleLedgerEndpoints.cs) und
  [HTTP-/Identity-/PostgreSQL-Tests](../tests/FirstBlick.Api.Tests/VehicleLedgerApiTests.cs).
- [Clientverträge](../web/src/app/core/vehicle-ledger.models.ts),
  [Clienttests](../web/tests/vehicle-ledger.test.ts),
  [Angular-Arbeitsplatz](../web/src/app/pages/vehicle-ledger.component.ts) und
  [responsive Browserfälle](../web/tests/browser/vehicle-ledger.spec.ts).
- [Migrationsgraph](../tools/FirstBlick.Operations/MigrationGraph.cs),
  [Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs) und
  [Container-/Neustartcheck](../scripts/customer_vehicle_ledgers_smoke.py) erhalten
  Originale, Stornierungen und historische Stammdaten. Der synthetische Betriebsfall
  prüft unter anderem 50 aktive Kilometer und 85 EUR Kosten nach Stornierungen.

Browserfälle verwenden kontrollierte HTTP-Antworten; API-Tests eine echte
PostgreSQL-Datenbank. Der Operations-Workflow führt den Container-, Sicherungs-,
Wiederherstellungs-, Migrations- und Neustartablauf aus. Ausführungsnachweise müssen
zum jeweiligen veröffentlichten Commit gehören.

Offen bleiben automatische Fahrzeug-/Telematikübernahme, steuerliche Fahrtenbuchabnahme,
automatische gesetzliche Prüffristen, unabhängige Kalender je
Prüfart, umfassende Kosten-/Buchhaltungsintegration sowie reale betriebliche Abnahmen.
