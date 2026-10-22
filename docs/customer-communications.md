# Kundenbezogene Kommunikationsakte

Stand: 22.10.2026 · FirstBlick · **DP-04-01 und DP-09-03**, technischer Teilumfang.

Unter **Kunden → Kunde öffnen → Kommunikationsakte öffnen** lassen sich geführte
Telefonate, E-Mails, Besprechungen und interne Notizen dokumentieren. Jeder Eintrag
enthält Zeitpunkt, Richtung, Betreff und Inhalt. Optional werden ein aktiver
[Ansprechpartner](customer-contacts.md) dieses Kunden und ein zugehöriges Projekt
ausgewählt. Die Projektwahl bietet Suche und Seitenwechsel.

Die Erfassung dokumentiert eine bereits geführte Kommunikation. Sie versendet keine
E-Mail, liest kein Postfach und behauptet keinen Zustellnachweis. Eine interne Notiz
hat stets die Richtung **Intern**. Vergangene Gespräche dürfen auch vor der Anlage
des Kunden, Kontakts oder Projekts stattgefunden haben; die gewählten Stammdaten
müssen bei der Erfassung bereits existieren.

## Original und Korrektur

Ein gespeicherter Eintrag hält die damaligen Kunden-, Kontakt- und Projektbezeichnungen
fest. Spätere Umbenennungen oder die Archivierung eines Kontakts ändern diese Werte
nicht. Eine historisch fehlende Projektnummer wird nur in der neuen Momentaufnahme
als leere Zeichenfolge dargestellt; das Projekt selbst bleibt unverändert.

Inhalt, Kennung, erfassende Person und Erfassungszeitpunkt sind durch einen SHA-256-
Digest gebunden. Ein Original kann weder über einen allgemeinen Bearbeitungsendpunkt
überschrieben noch archiviert werden. Eine Korrektur erfolgt durch **Stornierung mit
Grund** und gegebenenfalls einen neuen Eintrag. Die Stornierung ist ein eigener
unveränderlicher Datensatz mit Originalkennung, Originaldigest, Person, Zeitpunkt
und Grund. Das ursprüngliche Gespräch bleibt vollständig lesbar. Für jedes Original
ist höchstens eine Stornierung zulässig.

Ein referenziertes Projekt kann umbenannt, aber keinem anderen Kunden zugeordnet
werden. Allgemeine Archivierung eines referenzierten Kunden oder Projekts wird
abgewiesen. Das Lesen der Akte verändert keine Originale, Kundendaten oder Revisionen.
Bereits archivierte Kunden aus einer geprüften Datenübernahme behalten eine lesbare
Historie; ihre Akte weist den Archivstatus aus und erlaubt keine neuen Einträge,
Stornierungen oder Schreib-Replays.

## Rechte, Revision und Zeit

Geschäftsführung und Büro dürfen erfassen und stornieren. Bauleitung und Buchhaltung
können entsprechend den Kundenrechten lesen. Mitarbeitende und externe Partner
erhalten keinen Zugriff. Die Funktion gehört zum Modul `core`.

Jede Änderung benötigt die aktuelle Kundenrevision in `If-Match` und einen
`Idempotency-Key`. Original beziehungsweise Stornierung, nächste Kundenrevision,
Audit, Outbox und Idempotenzbeleg werden gemeinsam gespeichert. Der Server prüft
aktuelle Mitgliedschaft und Rolle vor der Wiederholung eines erfolgreichen Befehls.
Konkurrierende Änderungen mit derselben Revision können nicht beide erfolgreich sein.

Die Oberfläche verwendet die Betriebszeitzone und überträgt UTC-Zeitpunkte mit ganzen
Sekunden. Nicht vorhandene Ortszeiten während einer Zeitumstellung werden abgewiesen;
bei einer doppelten Ortsstunde muss der Zeitversatz ausgewählt werden. Gespräche in
der Zukunft sind unzulässig. Betreff und Inhalt sind auf 240 beziehungsweise 20.000
Zeichen, Stornierungsgründe auf 2.000 Zeichen begrenzt.

Der Verlauf liefert 20 Originale pro Seite, sortiert nach Kommunikationszeitpunkt,
Erfassungszeitpunkt und Kennung. Stornierte Originale bleiben Teil des Verlaufs. Die
Antwort enthält den aktuellen Kundennamen, die Kundenrevision, Betriebskennung und
browsergeeignete Zeitzone. Der Client prüft diese Zuordnung, Originalinhalte und
Stornierungsquellen, verwirft verspätete Antworten nach Kontextwechseln und verlangt
bei einem Revisionskonflikt ein bewusstes Neuladen. Beim Konflikt bleiben die Eingaben
zur Klärung sichtbar; bewusstes Neuladen verwirft das ungespeicherte Formular.

## Schnittstellen

| Methode und Pfad | Vertrag |
|---|---|
| `GET /api/v1/customers/{id}/communications?page=1` | Begrenzter Verlauf einschließlich Stornierungen, Gesamtzahl und ETag mit Kundenrevision. |
| `POST /api/v1/customers/{id}/communications` | `{channel,direction,occurredAt,subject,text,contactId?,projectId?}`; liefert Original und neue Kundenrevision. |
| `POST /api/v1/customers/{id}/communications/{entryId}/void` | `{sourceDigest,reason}`; liefert unverändertes Original, separaten Stornobeleg und neue Kundenrevision. |

`channel` verwendet `Telephone`, `Email`, `Meeting` oder `Note`; `direction` verwendet
`Incoming`, `Outgoing` oder `Internal`. Benutzerkennungen, Erfassungszeitpunkte,
Originalkennungen und Digests entstehen auf dem Server.

## Nachweise und verbleibender Umfang

- [Core und Digestregeln](../src/FirstBlick.Core/Business/CustomerCommunications.cs)
  und [Coretests](../tests/FirstBlick.Core.Tests/Business/CustomerCommunicationTests.cs).
- [API](../src/FirstBlick.Api/CustomerCommunicationEndpoints.cs) und
  [HTTP-/Identity-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/CustomerCommunicationApiTests.cs)
  prüfen unter anderem Originalerhalt, historische Stammdaten, Mandantengrenzen,
  konkurrierende Revisionen, aktuelle Rechte vor Replay und Projektzuordnung.
- [Clientverträge](../web/src/app/core/customer-journal.models.ts),
  [Clienttests](../web/tests/customer-journal.test.ts) und
  [responsive Browserfälle](../web/tests/browser/customer-journal.spec.ts).
- [Kanonische Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs),
  [Drill](../tools/FirstBlick.Operations/MigrationDrill.cs) und
  [Container-/Neustartablauf](../scripts/customer_vehicle_ledgers_smoke.py)
  prüfen historische Akteure, später geänderte Stammdaten, Originaldigests und separate
  Stornierungen. Der Neustartvergleich liest die gespeicherten Originale erneut.

Die Browserfälle verwenden kontrollierte HTTP-Antworten. Der Containerablauf verwendet
die tatsächliche API und Datenbank. Erfolgreiche Ausführung gilt jeweils für den
geprüften Commit und ist über dessen CI-/Operations-Ergebnisse nachzuweisen.

[Originalanhänge](ledger-originals.md) ergänzen die Gesprächseinträge um versionierte
PDF-/Bilddateien, deren Quellenbindung auch nach einer Stornierung erhalten bleibt.

Automatische Postfach-/Chatübernahme, externe Kontaktsynchronisierung,
Kundenzusammenführung, vollständige Feldänderungshistorien und eine fachliche Abnahme
realer CRM-Abläufe bleiben offen. Der Digest belegt die interne Inhaltsbindung; er
ist keine digitale Unterschrift oder Zusage rechtssicherer Langzeitarchivierung.
