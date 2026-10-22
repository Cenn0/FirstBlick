# Wartung nach betrieblichen Arbeitstagen

## Umfang

Diese Erweiterung ergänzt **DP-09-02** um ausdrücklich konfigurierte Arbeitstage und
betriebliche Sperrtage. Der vorhandene automatische Planungsablauf aus **DP-09-06**
verwendet dieselbe Regel. Eine vertragliche Fälligkeit und der geplante Tag der
Ausführung sind zwei getrennte Angaben.

Ein Betrieb kann pro Wartungsvertrag eine bis sieben erlaubte Wochentage und bis
zu 366 unterschiedliche Sperrtage hinterlegen. Die Regel ist standardmäßig aus.
Es gibt keine automatisch angenommenen gesetzlichen Feiertage, Tarifregeln oder
branchenspezifischen Fristen. Die hinterlegten Daten gelten nur für diesen Vertrag.

Beispiel: Ein monatlicher Vertrag beginnt am Sonntag, **31.01.2027**. Montag bis
Freitag sind erlaubt; **01.02.2027** wurde ausdrücklich gesperrt. Der Rechenkern
plant die Ausführung am **02.02.2027**. Die vertragliche Fälligkeit bleibt
**31.01.2027**, und der nächste Intervalltermin nach Abschluss bleibt
**28.02.2027**. Das ursprüngliche Monatsenddatum wird durch den Ausführungstag
nicht ersetzt.

## Auswahl des Ausführungstags

Der Kern prüft den Fälligkeitstag und danach aufsteigend die Kalendertage bis
höchstens 366 Tage nach Fälligkeit. Er verwendet den ersten Tag, dessen Wochentag
erlaubt ist und der nicht als Sperrtag eingetragen wurde. Ein Vertragsende ist
eine zusätzliche inklusive Obergrenze. Wenn kein passender Tag innerhalb beider
Grenzen liegt, schlagen Vorschau und Anlage mit `maintenance_workday_window` fehl.
Dabei entstehen weder Wartungsprojekt noch Termin. Die Datumsgrenze des Typs
`DateOnly` wird ebenfalls berücksichtigt.

Die Berechnung erfolgt ausschließlich mit Kalenderdaten. Die Sommer-/Winterzeit
verschiebt deshalb keinen errechneten Ausführungstag. Der bestehende Worker
verwendet weiterhin die Betriebszeitzone, um zu entscheiden, ab welchem lokalen
Tag eine Fälligkeit innerhalb ihres Planungsvorlaufs liegt. Der Planungsvorlauf
bezieht sich auf die vertragliche Fälligkeit, nicht auf den verschobenen Tag.

## Bedienung

Unter **Wartungsplanung** wird zunächst der Vertrag geöffnet. Im Abschnitt
„Arbeitstage für neue Termine“ kann die Regel aktiviert werden. Sperrtage werden
als `JJJJ-MM-TT`, vorzugsweise ein Datum je Zeile, erfasst. Unmögliche Daten,
Dubletten und eine leere Wochentagsauswahl sind ungültig.

Nach dem Speichern zeigt „Ausführungsvorschau prüfen“ sowohl die vertragliche
Fälligkeit als auch den geplanten Ausführungstag. Bei aktiver Arbeitstagsregel
muss der Nutzer „Ausführungstag geprüft“ bestätigen, bevor die manuelle Anlage
freigeschaltet wird. Ungespeicherte Konfigurationsänderungen sperren die Anlage.
Nach dem Speichern einer Änderung ist eine neue Vorschau erforderlich.

Die Vorschau kennzeichnet einen bereits gespeicherten Termin. In diesem Fall
zeigt sie dessen ursprünglichen Ausführungstag und öffnet genau diesen Termin.
Spätere Änderungen oder das Abschalten der Vertragsregel verschieben bestehende
Termine nicht. Auch dann muss ein bereits mit Arbeitstagsregel erstellter Termin
über die aktuelle Vorschau bestätigt werden.

Im Wartungsprojekt werden `PlannedStart` und `PlannedEnd` bei der Anlage auf den
Ausführungstag gesetzt. Die Terminansicht und Terminliste zeigen den abweichenden
Tag neben der Fälligkeit. Personaleinteilung, Einsatzzeiten und Ressourcen bleiben
Aufgaben der Disposition. Der bestehende ICS-Download bleibt ein Hinweis auf die
**vertragliche Fälligkeit**; er ist keine Einladung für den Ausführungstag.

## Vertrag und unveränderliche Nachweise

| Objekt | Neue Angabe | Bedeutung |
|---|---|---|
| `MaintenanceContract` | Optionales `Workdays` | Aktuell konfigurierte erlaubte Wochentage und Sperrtage für künftig angelegte Termine |
| `MaintenanceVisit` | Optionales `ExecutionPlan` | Eingefrorene Fälligkeit, Ausführungstag, damaliges Vertragsende und Kopie der verwendeten Regel |
| `MaintenancePlanningPreview` | `Digest`, Vertragskennung/-revision, optional vorhandene Terminkennung | Bindung der bestätigten Vorschau an den aktuellen Vertragsstand und die tatsächlich geplante beziehungsweise bereits gespeicherte Ausführung |

Der Scope-Digest neuer Termine mit Arbeitstagsregel enthält den vollständigen
`ExecutionPlan`. Die Validierung berechnet den ersten zulässigen Tag aus den
eingefrorenen Eingaben erneut. Änderungen von Fälligkeit, Ergebnis oder Regel
werden daher erkannt. Die Regel wird kopiert; spätere Änderungen an der
Vertragskonfiguration können die gespeicherte Terminkopie nicht verändern.

Bei vorhandenen Terminen ohne `ExecutionPlan` wird genau die bisherige
Digeststruktur verwendet. Fehlende optionale Felder erhalten keinen erfundenen
historischen Inhalt und werden bei der Serialisierung weiterhin ausgelassen.
Solche Termine bleiben lesbar, abschließbar und exportierbar.

## HTTP-Verträge

- `POST /api/v1/maintenance-contracts/{id}/configure` übernimmt neue Regeln nur
  bei `updateWorkdays: true`. `workdays: null` schaltet die Regel ausdrücklich
  aus. Alte Clients, die diese Felder nicht senden, erhalten eine vorhandene
  Regel bei Änderungen an Checkliste oder Planungsvorlauf.
- `GET /api/v1/maintenance-contracts/{id}/planning-preview?revision={revision}`
  liefert die aktuelle Vorschau mit `private, no-store` und der Vertragsrevision
  als ETag. Eine veraltete Revision wird zurückgewiesen. Der Endpunkt benötigt
  den vorhandenen Lesezugriff auf Wartungsverträge.
- `POST /api/v1/maintenance-contracts/{id}/schedule` nimmt zusätzlich zu
  `expectedDueDate` den `reviewedPreviewDigest` entgegen. Wenn die aktuelle Regel
  oder der bereits vorhandene Termin Arbeitstage verwendet, muss der Digest
  genau der neu berechneten Vorschau entsprechen. Die bestehenden Prüfungen von
  Revision, Idempotenz, Mandant, aktivierten Modulen und Projektschreibrecht
  bleiben verbindlich.

Generische Vertragsänderungen dürfen eine vorhandene Arbeitstagsregel weder
ersetzen noch durch das Weglassen des Felds entfernen. Die ausdrückliche
Konfiguration erfolgt über den Wartungsbefehl. Neue generische Verträge starten
ohne Arbeitstagsregel.

Der automatische Worker benötigt keine individuelle interaktive Bestätigung.
Er verwendet die ausdrücklich gespeicherte Regel nur bei bereits aktivierter
automatischer Planung. Die vorhandene Sperre pro Mandant und der eindeutige
Schlüssel aus Vertrag plus **Fälligkeit** verhindern doppelte Termine auch bei
mehreren Workern. Ein nicht planbarer Vertrag erzeugt keine Teilanlage und
blockiert nicht die übrigen Verträge.

## Prüfungen

- Der [Migrationsdrill](../tools/FirstBlick.Operations/MigrationWorkdays.cs)
  erhält vertragliche Fälligkeit, getrennten Ausführungstag und die damalige
  Regelkopie auch nach einer späteren Vertragsänderung. Manipulierte
  Berechnungen, Prüfsummen und Quellzuordnungen werden abgelehnt.
- [Core-Fälle](../tests/FirstBlick.Core.Tests/Business/MaintenanceWorkdaysTests.cs)
  prüfen Wochenende, Sperrtage, Monatsendanker, inklusive Vertragsgrenzen,
  begrenzte Suche, Datumsobergrenze, Sommer-/Winterzeitdaten, Schaltjahr,
  manipulierte Nachweise und die alte JSON-/Digeststruktur.
- [HTTP-/Identity-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/MaintenanceWorkdaysApiTests.cs)
  prüfen Vorschau und Anlage, Replay, Projekt-/Terminpersistenz, unveränderte
  Fälligkeit im ICS, Monatsfortschreibung, historische Regelkopien, alte Clients,
  generische Schreibversuche, ungültige Konfiguration, Mandantentrennung,
  entzogene Module sowie automatische Planung um den Zeitumstellungstag.
- [Clientfälle](../web/tests/maintenance-workdays.test.ts) prüfen gültige und
  manipulierte Datums-/Regelantworten sowie deren Bindung an die Auswahl.
- [Browser-Fixtures](../web/tests/browser/maintenance.spec.ts) ergänzen den
  vorhandenen Wartungsablauf um ausdrückliches Aktivieren, ungültige Sperrtage,
  Vorschau, Bestätigung, veränderte Eingaben und verspätete Antworten nach einem
  Betriebswechsel. Diese Tests verwenden kontrollierte HTTP-Antworten.

Die Existenz dieser Tests ist noch kein Ausführungsnachweis des abschließend
veröffentlichten Commits. CI- und Operations-Ergebnisse sind jeweils an dessen
SHA zu prüfen. Gesetzliche Fristbewertung, betriebliche Kalenderabnahme,
automatische Feiertagsdaten, Kalenderzustellung und verbindliche
Personaleinteilung sind nicht Bestandteil dieser Erweiterung.
