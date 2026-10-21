# Wartungsvertrag, Arbeitsprojekt und Checkliste

## Implementierter Ablauf für DP-09-02

Die Wartungsplanung verbindet den bestehenden `MaintenanceContract` mit einem eigenen
Wartungsprojekt und einer konkreten, gespeicherten Checkliste. Der Ablauf ist unter
**Werkzeuge → Wartungsplanung** erreichbar:

1. Einen Wartungsvertrag aus den Stammdaten auswählen.
2. Seine individuellen Prüfschritte mit optionalen Arbeitsanweisungen hinterlegen.
3. Den aktuellen Termin ausdrücklich anlegen oder die automatische Anlage aktivieren.
4. Das dabei erzeugte Wartungsprojekt in der Einsatzplanung Mitarbeitenden zuordnen.
5. Prüfschritte als erledigt oder mit Begründung als nicht anwendbar dokumentieren.
6. Einen Abschlussbericht erfassen und Wartung sowie Projekt gemeinsam abschließen.
7. Den vom ursprünglichen Vertragsdatum berechneten Folgetermin weiterplanen.

Ein angelegter Termin ist eine `maintenance-visits`-Entität. Er referenziert den Vertrag
und genau das neu erzeugte Projekt. Sein Umfang, die ursprüngliche Vertragsrevision,
die Fälligkeit, der Wiederholungsrhythmus und die Prüfschritte werden eingefroren.
Änderungen an der Vertragsvorlage gelten für anschließend angelegte Termine.

Das Wartungsprojekt ist ein `Project` mit Typ `Maintenance`, Status `Planning`, eigener
`WA-JAHR-NUMMER` und anfänglich eintägigem Planungsfenster an der vertraglichen Fälligkeit.
Das Projektfenster kann anschließend bewusst für die tatsächliche Einsatzplanung
angepasst werden; die vertragliche Fälligkeit im Wartungsnachweis bleibt erhalten.
Es wird kein kaufmännischer Auftrag oder Rechnungsbeleg automatisch ausgegeben.

## Wiederkehrende Anlage

`AutomaticPlanning` ist standardmäßig ausgeschaltet. Die Konfiguration erlaubt einen
Vorlauf von 0 bis 365 Kalendertagen und 1 bis 50 ausdrücklich eingetragene Prüfschritte.
Ein laufender Worker prüft die Vertragsfälligkeiten einmal je Minute anhand des
aktuellen Datums in der Zeitzone des jeweiligen Betriebs. Er legt nur die aktuelle
offene Fälligkeit an; eine überfällige Wartung erzeugt keine unkontrollierte Reihe
übersprungener Arbeitspakete.

Die vorhandene Verankerung am Vertragsbeginn bleibt erhalten: aus dem 31. Januar
folgen bei monatlichem Intervall der 28./29. Februar und danach wieder der 31. März.
Es findet keine automatische Feiertags- oder Wochenendverschiebung statt.
Ein Folgetermin nach dem Vertragsende deaktiviert den Vertrag. Das berechnete Datum
bleibt als nachvollziehbare Abschlussinformation gespeichert.

Die automatische Anlage benötigt das aktive Wartungsmodul, einen aktiven Vertrag,
gültige Kunden-/Gebäudebeziehungen und eine freigegebene Betriebsinstanz ohne
Wiederherstellungssperre. Fehlerhafte Vertragsdaten werden ohne Teilerstellung
übersprungen; der Worker protokolliert die betroffene Vertragskennung, keine
Kunden- oder Checklisteninhalte.

## Integrität und Berechtigungen

- Die mandantenbezogene Transaktionssperre schützt manuelle und automatische Anlage
  gleichermaßen. Der zusätzliche eindeutige Schlüssel aus Vertragskennung und
  Fälligkeit verhindert doppelte Termine. Wiederholungen öffnen denselben Termin.
- Projekt, Termin, Verknüpfungen, Nummernvergabe, Audit und Outbox entstehen gemeinsam
  in einer PostgreSQL-Transaktion. Der Worker wurde mit zwei konkurrierenden
  Schedulerinstanzen gegen eine echte Testdatenbank geprüft.
- Geschäftsführung, Büro und Bauleitung können Verträge konfigurieren, Termine
  anlegen und Abschlüsse dokumentieren. Mitarbeitende dürfen nur Checklisten aktuell
  zugewiesener Projekte lesen und bearbeiten. Widerrufene Einsatzzuordnungen werden
  auch vor der Wiederholung eines bereits erfolgreichen Befehls erneut geprüft.
- Schreibbefehle benötigen CSRF-Schutz, `Idempotency-Key` und die passende Revision
  in `If-Match`. Ein veralteter Stand wird nicht überschrieben.
- Der eingefrorene Umfang ist durch einen Inhaltsdigest gebunden. Ergebnisse ändern
  diesen Umfang nicht. Jeder dokumentierte Prüfschritt erhält Benutzer und Zeitpunkt;
  „nicht anwendbar“ benötigt eine Begründung.
- Erst wenn alle Prüfschritte ein gespeichertes Ergebnis besitzen, werden Projekt,
  Termin und Vertragsfolgetermin atomar abgeschlossen. Eine endgültig abgeschlossene
  Checkliste lässt sich nicht wieder öffnen oder generisch bearbeiten/archivieren.
- Bereits eingeplante Verträge behalten Kunde, Gebäude und Wiederholungsanker.
  Ein nachweislich abgeschlossenes Wartungsprojekt lässt sich nicht durch generisches
  Bearbeiten wieder in einen offenen Zustand versetzen.
- Der bisherige Abschlussbefehl für manuell zugeordnete Wartungsprojekte bleibt für
  Altvorgänge nutzbar. Er kann weder die Checkliste eines geplanten Termins umgehen
  noch dessen Projekt zum Abschluss eines anderen Vertrags verwenden.

Die Angular-Oberfläche verwirft ausgewählte Daten bei Betriebs- oder Rechtewechseln
und ignoriert verspätete Antworten aus einem früheren Kontext. Beim Speichern eines
Prüfschritts bleiben andere noch ungespeicherte Bemerkungen und der Abschlussentwurf
erhalten. Ein Abschluss wird erst angeboten, wenn auch lokal geänderte Prüfergebnisse
gespeichert wurden. Die Projektverknüpfung öffnet den konkreten Datensatz.

## Schnittstellen

| Methode und Pfad | Zweck / Revision |
|---|---|
| `POST /api/v1/maintenance-contracts/{id}/configure` | Aktivität, automatische Planung, Vorlauf und Checkliste speichern; Vertragsrevision |
| `POST /api/v1/maintenance-contracts/{id}/schedule` | Aktuelle Fälligkeit anlegen oder vorhandenen Termin zurückgeben; Vertragsrevision |
| `GET /api/v1/maintenance-visits?contractId=…&page=…` | Sichtbare Termine, 25 pro Seite; optional nach Vertrag gefiltert |
| `GET /api/v1/maintenance-visits/{id}` | Vollständigen eingefrorenen Umfang und Prüfergebnisse lesen |
| `POST /api/v1/maintenance-visits/{id}/check` | Einzelnes Prüfergebnis dokumentieren; Terminrevision |
| `POST /api/v1/maintenance-visits/{id}/complete` | Abschlussbericht, Projektabschluss und Folgetermin; Terminrevision |

Alte Vertragsdatensätze erhalten bei der Deserialisierung im Backend und beim Öffnen
in der Wartungsoberfläche die sicheren Standardwerte: keine automatische Planung,
leere Checkliste, 14 Tage Vorlauf. Der generische GET liefert weiterhin das gespeicherte
JSON; die Oberfläche ergänzt ausschließlich fehlende neue Felder, keine expliziten
Nullwerte oder falschen Datentypen. Bestehende Verträge können dadurch ihre erste
bewusst eingetragene Checkliste erhalten. Sie werden nicht nachträglich automatisch
eingeplant. Die generische JSONB-Persistenz und vorhandenen
eindeutigen Schlüssel und mandantenbezogenen Fremdschlüssel reichen aus;
eine Änderung des relationalen Schemas ist nicht erforderlich.

## Verifikation dieses Arbeitsstands

Die Tests stehen in
[`MaintenanceWorkflowTests.cs`](../tests/FirstBlick.Core.Tests/Business/MaintenanceWorkflowTests.cs),
[`MaintenanceWorkflowApiTests.cs`](../tests/FirstBlick.Api.Tests/MaintenanceWorkflowApiTests.cs) und
[`maintenance.spec.ts`](../web/tests/browser/maintenance.spec.ts).

- **Core:** 17 neue Fälle bestanden. Die vollständige Core-Suite dieses Zweigs
  bestand mit 372 Fällen, keinem Fehler und keinem übersprungenen Fall.
- **HTTP/PostgreSQL:** zehn neue Wartungsfälle und der bestehende
  `CommercialApiTests.Maintenance_completion…`-Regressionsfall bestanden in einem
  gemeinsamen Lauf: elf bestanden, null Fehler, null übersprungen.
- **Browser:** zehn Abläufe auf Desktop, Tablet und Mobil: 30 bestanden.
  Diese UI-Vertragstests verwenden kontrollierte HTTP-Antworten. Sie sind von den
  tatsächlich ausgeführten HTTP-/Identity-/PostgreSQL-Tests oben zu unterscheiden.
- **Build:** .NET-Solution und Angular-Produktionserstellung wurden lokal erfolgreich
  ausgeführt. Der zusammengeführte finale GitHub-Commit braucht seine eigenen CI-
  und Operations-Ergebnisse; diese lokalen Läufe ersetzen diesen Nachweis nicht.

Die technischen Fälle belegen unter anderem Monatsende, Vertragsende, doppelte
Planung, zwei Scheduler, Zeitzonengrenze, Wiederherstellungssperre, Rechte-/Mandantengrenzen,
Widerruf vor Replay, gesperrten Legacy-Bypass, unveränderliche Abschlussbelege,
echtes Bestands-JSON ohne neue Felder, dessen ausdrückliche Checklistenmigration,
fehlerhafte Antworten und verlorene Aktualisierungen. Die Checklisten werden vom
jeweiligen Betrieb definiert; ihre fachliche Eignung wurde nicht durch einen realen
Wartungsbetrieb oder eine externe Prüfstelle bestätigt. Eine unbeaufsichtigte
produktive Zustellung, externe Kalenderintegration oder reale Betriebsabnahme
wird durch diese Implementierung nicht behauptet.
