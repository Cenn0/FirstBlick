# Mehrere Ansprechpartner je Kunde

Stand: 21.10.2026 · FirstBlick · **DP-04-01**, technischer Teilumfang.

Unter **Kunden → Kunde öffnen → Ansprechpartner** lassen sich mehrere Personen mit
Name, Funktion/Zuständigkeit, E-Mail, Telefon und Hinweisen verwalten. Die Liste lässt
sich nach diesen Kontaktdaten filtern. Ein ausdrücklich gewählter Hauptkontakt ersetzt
die bisherige Hauptkontaktzuordnung desselben Kunden; alle weiteren Einträge bleiben
erhalten. Ein Hauptkontakt ist optional.

## Bestehende Kunden und Archivierung

Die bisherigen Felder Ansprechpartner, E-Mail und Telefon in den Kundenstammdaten
bleiben unverändert. Sie werden nicht automatisch kopiert, als neue Person ausgegeben
oder durch die Wahl eines Hauptkontakts ersetzt. Historische Datensätze ohne die neue
Liste behalten ihre bisherige JSON-Darstellung und Prüfsumme, solange sie nicht
bearbeitet werden. Das Lesen der neuen Ansicht allein verändert weder Daten noch
Revision.

Ein Kontakt kann archiviert und später ausdrücklich wiederhergestellt werden.
Archivierung bewahrt Kennung, Namen, Kommunikationsdaten, Hinweise und
Erstellungsinformationen. Ein archivierter Kontakt ist kein Hauptkontakt und lässt
sich erst nach Wiederherstellung bearbeiten. Wiederherstellung setzt den Eintrag
nicht automatisch als Hauptkontakt ein. Daten werden durch diese Aktionen nicht
physisch gelöscht.

Pro Kunde können bis zu 100 Einträge einschließlich archivierter Kontakte aufbewahrt
werden. Jeder Eintrag hält Erstellungs- und letzten Änderungszeitpunkt sowie die
zugehörigen Benutzerkennungen fest. Archivierte Einträge halten zusätzlich den
Archivierungszeitpunkt und Benutzer fest. Die vorhandenen Audit-Einträge dokumentieren
Aktion, Benutzer, Kundenrevision und Inhaltsdigest. Sie sind **keine vollständige
Feldänderungshistorie mit allen früheren Kontaktwerten**. Nach Wiederherstellung
beschreibt der Eintrag den aktuellen Zustand; frühere Archivaktionen bleiben im Audit.

## Serverseitige Regeln

- Kontakte sind Teil genau eines mandantengebundenen Kunden. Kennungen werden beim
  Anlegen vom Server erzeugt; ein Kontakt kann nicht durch die Kennung eines anderen
  Kunden oder Betriebs bearbeitet werden.
- Es gibt höchstens einen aktiven Hauptkontakt pro Kunde. Ein ausdrücklicher Wechsel
  wird zusammen mit der Änderung des Zielkontakts atomar gespeichert.
- Alle Schreibvorgänge verlangen aktuelle Kunden-Schreibrechte, `If-Match` mit der
  Kundenrevision und einen `Idempotency-Key`. Die vorhandene Betriebssperre schützt
  konkurrierende Änderungen. Kunde, Audit, Outbox und Idempotenzbeleg werden in einer
  gemeinsamen PostgreSQL-Transaktion gespeichert.
- Aktuelle Mitgliedschaft und Rolle werden auch vor der Wiederholung eines schon
  erfolgreichen Befehls geprüft. Geschäftsführung und Büro dürfen Kundenkontakte
  bearbeiten. Buchhaltung und Bauleitung können gemäß bestehenden Kundenrechten
  lesen; Mitarbeitende und externe Partner erhalten keinen Kundenzugriff.
- Generische Kundenänderungen können Kontakte nicht ersetzen oder löschen. Eine
  ausgelassene oder explizit `null` gesetzte Kontaktliste bewahrt den gespeicherten
  Stand. Eine mitgesendete Liste muss unverändert sein. Kontaktanlage beim generischen
  Anlegen eines Kunden ist ausgeschlossen; sie erfolgt anschließend über die
  vorgesehenen Kommandos.
- Listenansichten der Kunden laden keine kompletten Kontaktlisten. Die Kontaktliste
  wird erst für den ausgewählten Kunden abgerufen. Die bestehende übergreifende Suche
  bleibt eine Suche in den bisherigen Stammdaten; der Kontaktfilter arbeitet innerhalb
  des ausgewählten Kunden.

## Schnittstellen

| Methode und Pfad | Zweck |
|---|---|
| `GET /api/v1/customers/{id}/contacts` | Kontakte einschließlich archivierter Einträge, Kundenkennung, Betrieb und Revision; ETag mit Kundenrevision. |
| `POST /api/v1/customers/{id}/contacts` | Kontakt anlegen. |
| `POST /api/v1/customers/{id}/contacts/{contactId}/update` | Aktiven Kontakt bearbeiten und optional Hauptkontakt zuordnen. |
| `POST /api/v1/customers/{id}/contacts/{contactId}/archive` | Kontakt unter Erhalt seiner Daten archivieren. |
| `POST /api/v1/customers/{id}/contacts/{contactId}/restore` | Archivierten Kontakt als gewöhnlichen aktiven Kontakt wiederherstellen. |

Anlage und Bearbeitung verwenden `{name, function, email, phone, notes, isPrimary}`.
Optionale Textfelder werden als leere Zeichenfolge übertragen. Die anderen beiden
Kommandos haben keinen fachlichen Nutzinhalt. Jede erfolgreiche Änderung liefert die
vollständige neue Kontaktansicht mit der nächsten Kundenrevision zurück.

## Bedienung und Verifikation

Der Client bindet geladene Daten und Mutationsergebnisse an Betrieb, Kunde und Revision.
Er verwirft verspätete Antworten nach Kunden-, Betriebs-, Rechte- oder Seitenwechseln.
Unvollständige Antworten, mehrere Hauptkontakte, ersetzte Kontaktkennungen und vom
Auftrag abweichende Änderungen führen zu einer Fehlermeldung. Bei Revisionskonflikten
bleibt der ungespeicherte Formularinhalt zur Klärung erhalten.

Die technischen Nachweise stehen in:

- [Core-Modell und Regeln](../src/FirstBlick.Core/Business/CustomerContacts.cs),
  [Core-Regressionen](../tests/FirstBlick.Core.Tests/Business/CustomerContactTests.cs).
- [HTTP-Kommandos](../src/FirstBlick.Api/CustomerContactEndpoints.cs),
  [HTTP-/Identity-/PostgreSQL-Regressionen](../tests/FirstBlick.Api.Tests/CustomerContactApiTests.cs).
- [Antwortverträge](../web/src/app/core/customer-contacts.models.ts),
  [Clienttests](../web/tests/customer-contacts.test.ts),
  [responsive Browserfälle](../web/tests/browser/customer-contacts.spec.ts).
- [Kanonische Importprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) und
  [Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs) prüfen erhaltene
  Kontaktstände und historische Benutzerzugehörigkeit beim Import.

Browserfälle verwenden kontrollierte HTTP-Antworten. Ausführungsnachweise und
GitHub-Prüfungen gelten für den jeweiligen veröffentlichten Commit; die Existenz
einer Testdatei ist noch kein bestandener Prüflauf.

Innerhalb DP-04-01 bleiben insbesondere eine vollständige kundenbezogene
Kommunikationshistorie, automatische Dublettenbereinigung, die bewusste Zusammenführung
von Kunden und externe Kontaktsynchronisierung offen. Der Hauptkontakt löst keinen
Versand aus und ändert keine bereits eingefrorenen Angebote, Aufträge oder Rechnungen.
