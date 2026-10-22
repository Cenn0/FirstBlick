# Geräteprüfungen, Wartung und Einsatzfähigkeit

Stand: 21.10.2026 · FirstBlick · **DP-07-05**, mit Anbindung an **DP-06-01**.

Der Arbeitsplatz `/equipment-inspections` verbindet Geräte- und Fahrzeugstammdaten mit geplanten Prüfungen, Wartungen und Reparaturen. Er zeigt den manuell festgelegten nächsten Prüftermin, offene Termine, gesperrte Ressourcen und eine paginierte Nachweishistorie. Die Betriebszeitzone bestimmt das heutige Datum und die Datumsgrenzen der Einsatzplanung.

## Ablauf

1. Ein Gerät oder Fahrzeug in den bestehenden Stammdaten anlegen. Für vorhandene Ressourcen bleiben die bisherigen Daten und JSON-Darstellungen lesbar.
2. Einen Termin mit Art, Datum, Titel und optionaler Beschreibung planen. Es kann genau einen offenen Termin je Ressource geben. Die Planung hält Name, Inventarnummer, Geräteart und Kennzeichen als damalige Identität fest.
3. Nach der tatsächlichen Durchführung Datum, Ergebnis, prüfende Person und Belegreferenz erfassen. Die Anwendung lässt keine Durchführung in der Zukunft oder vor dem zuletzt dokumentierten Durchführungstag zu.
4. Ein negatives Ergebnis benötigt eine Beschreibung. Es sperrt die Ressource sofort für neue Zuordnungen. Bestehende Einsätze bleiben erhalten; die Oberfläche nennt die Zahl der betroffenen noch nicht beendeten Einsätze.
5. Eine dokumentierte positive Einsatzbeurteilung benötigt einen ausdrücklich angegebenen nächsten Termin. Die zusätzliche Auswahl **wieder verfügbar** ist erforderlich, um eine zuvor nicht verfügbare Ressource wieder freizugeben. Ohne diese Auswahl bleibt eine manuelle Nichtverfügbarkeit erhalten.
6. Einen nicht durchgeführten Termin mit Begründung absagen. Bereits abgeschlossene oder abgesagte Vorgänge bleiben unveränderlich erhalten. Ein neuer Termin erzeugt einen neuen Vorgang.
7. Zum abgeschlossenen Ergebnis bei Bedarf [Originaldateien](equipment-originals.md) als PDF, PNG oder JPEG ergänzen. Diese werden mit dem Abschlussdigest und einer konkreten Speicherversion verbunden; Ergebnis und Gerätestand bleiben unverändert.

`Passed` bedeutet in allen drei Vorgangsarten eine dokumentierte positive Einsatzbeurteilung. Eine Reparatur kann damit nach einer solchen Beurteilung die Sperre aufheben. Die Software bestimmt weder gesetzliche Prüffristen noch die Qualifikation der prüfenden Person. Die Belegreferenz bleibt ein Textfeld; separat hochgeladene Originaldateien erhalten ihre eigene prüfungsgebundene Akte. Ihre unveränderten Bytes werden geprüft, ihre fachliche Richtigkeit wird nicht automatisch bestätigt. Für dieselbe Ressource ist derzeit ein gemeinsamer Prüfkalender vorgesehen; parallele unterschiedliche Prüfarten haben noch keine unabhängigen Fristen.

## Disposition und Stammdaten

Neue oder bearbeitete Einsätze können nur verfügbare, nicht gesperrte Geräte verwenden. Wenn ein Prüftermin gesetzt ist, darf der letzte berührte lokale Einsatztag diesen Termin nicht überschreiten. Ein Einsatz endet exklusiv: Das Ende genau um Mitternacht gehört zum vorherigen Tag. Die Prüfung erfolgt mit der Betriebszeitzone, einschließlich Zeitumstellung.

Ein negatives Prüfergebnis wird auch dann gespeichert, wenn bereits zukünftige Einsätze existieren. Eine reale Feststellung wird nicht wegen eines älteren Plans abgewiesen. Die bestehenden Einsätze werden auch nicht still abgesagt oder umverteilt. Ihre Klärung erfolgt in der vorhandenen Disposition.

Generische Stammdatenänderungen können keine Prüfungen erfinden, Nachweise entfernen, Ergebnissperren aufheben oder den Folgetermin eines bereits geprüften Geräts ersetzen. Eine Identitätsänderung bei offenem Termin erfordert zunächst dessen Absage. Nicht betroffene Stammdaten bleiben bearbeitbar. Historische abgeschlossene Vorgänge behalten ihre damalige Geräteidentität.

## Schnittstellen und Berechtigungen

| Schnittstelle | Inhalt |
|---|---|
| `GET /api/v1/equipment/inspection-worklist` | Paginierte Geräteauswahl; Filter `all`, `due`, `blocked`, `planned`, Suche, Betriebsdatum und Zeitzone. |
| `GET /api/v1/equipment/{id}/inspection-history` | Aktueller Gerätestand, separat geladener offener Termin, paginierte Historie und Anzahl betroffener Einsätze. Der offene Termin bleibt auch auf späteren Historienseiten verfügbar. |
| `GET /api/v1/equipment-inspections/{id}` | Einzelner unveränderlicher beziehungsweise noch offener Vorgang mit ETag. |
| `POST /api/v1/equipment/{id}/inspection-plan` | `{kind, scheduledOn, title, notes}`. |
| `POST /api/v1/equipment/{id}/inspection-complete` | `{inspectionId, inspectionRevision, performedOn, result, inspector, evidenceReference, notes, nextInspectionOn, makeAvailable}`. |
| `POST /api/v1/equipment/{id}/inspection-cancel` | `{inspectionId, inspectionRevision, reason}`. |

Alle Schreibvorgänge verlangen `If-Match` mit der aktuellen Geräterevision und einen `Idempotency-Key`. Durchführung und Absage verlangen zusätzlich die aktuelle Vorgangsrevision. Gerät, Vorgang, Referenzen, Audit, Outbox und Idempotenzbeleg werden in einer gemeinsamen PostgreSQL-Transaktion unter der Betriebssperre geschrieben. Eine Wiederholung verleiht keine inzwischen entzogene Berechtigung.

GF, Büro und Bauleitung dürfen Prüfungen bearbeiten. Die Buchhaltung kann die Akte lesen. Neue Schreibvorgänge verlangen das aktive Betriebsmodul `purchasing`; vorhandene Akten bleiben bei deaktiviertem Modul für leseberechtigte Personen zugänglich. Die jeweiligen serverseitigen Rechte werden für Lesen und Schreiben geprüft. Mitarbeitende und externe Partner erhalten keinen Zugriff auf den Prüfungsarbeitsplatz. Der Server erzwingt diese Regeln unabhängig von der Oberfläche.

Der Client verwirft verspätete Antworten nach Betriebs-, Berechtigungs-, Geräte- oder Seitenwechsel. Eingaben werden nicht als erfolgreicher Nachweis angezeigt, solange keine zum ausgewählten Gerät und Vorgang passende Serverantwort vorliegt.

## Migration, Neustart und Prüfbelege

Der kanonische Import prüft die Zuordnung des offenen Termins, das zuletzt dokumentierte Ergebnis, seine Auswirkung auf Sperre/Folgetermin und die historische Betriebsmitgliedschaft der erfassenden Personen. Er rekonstruiert keine Ergebnisse aus heutigen Stammdaten. Ein importiertes negatives Ergebnis kann nicht als verfügbare Ressource erscheinen.

Quell- und Testbelege:

- [Fachmodell und Zustandsübergänge](../src/FirstBlick.Core/Business/EquipmentInspectionWorkflow.cs), [API](../src/FirstBlick.Api/EquipmentInspectionEndpoints.cs), [Stammdatenschutz](../src/FirstBlick.Api/EditRules.cs).
- [Core-Regressionen](../tests/FirstBlick.Core.Tests/Business/EquipmentInspectionWorkflowTests.cs) und [HTTP-/Identity-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/EquipmentInspectionApiTests.cs).
- [Client-Verträge](../web/tests/equipment-workflow.test.ts) und [responsive Browser-Fixtures](../web/tests/browser/equipment-workflow.spec.ts). Browser-Fixtures verwenden kontrollierte HTTP-Antworten.
- [Echter Containerablauf](../scripts/equipment_inventory_smoke.py): negative Prüfung trotz bestehendem Einsatz, abgewiesene neue Zuordnung, dokumentierte Reparatur, Nachweishistorie und erneute Leseprüfung nach dem API-/Worker-Neustart.
- [Kanonischer Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs): erhaltene Prüfakte und gezielt abgewiesene falsche Ergebnisspiegel, fehlende Terminlinks, rückläufige Durchführungstage und unbekannte historische Akteure.

Die Existenz dieser Tests ist kein Ausführungsnachweis. Die CI- und Operations-Ergebnisse gelten für den jeweiligen veröffentlichten Commit.

Offen innerhalb DP-07-05 bleiben insbesondere Fahrtenbuch, Kilometer- und Fahrzeugkosten, automatisch ermittelte Prüffristen sowie unabhängige Prüfkalender je Prüfart. Die [Originalakten-Dokumentation](equipment-originals.md) beschreibt den ergänzten Upload, seine Berechtigungen und verbleibenden Grenzen.
