# Wartungsfälligkeit als Kalenderdatei

## Bedienung und Umfang

Unter **Werkzeuge → Wartungsplanung** lässt sich ein geöffneter, gespeicherter
Wartungstermin über **Kalenderdatei herunterladen** als `.ics` exportieren.
Der Eintrag enthält seinen eingefrorenen Titel, die vertragliche Fälligkeit und
den aktuellen Abschlussstatus. Er gilt als ganztägiger Hinweis und blockiert
keine Kalenderzeit. Die Disposition tatsächlicher Mitarbeitereinsätze bleibt
im bestehenden Planungsbereich.

Checklisten, Arbeitsanweisungen, Abschlussberichte, Kundennamen/-adressen,
Personenkennungen und interne Projektlinks werden nicht in die Datei geschrieben.
Ein vom Betrieb selbst im Titel erfasster Name bleibt Bestandteil dieses Titels.
Vor dem Download weist die Oberfläche auf die enthaltenen Daten hin.

Der Export betrifft genau einen vorhandenen Termin. Ein berechneter Folgetermin
wird erst exportierbar, nachdem er ausdrücklich oder durch die aktivierte
Wartungsplanung als eigener Vorgang angelegt wurde. Es entstehen keine erfundenen
Wiederholungsreihen. Auch ein abgeschlossener Termin lässt sich weiterhin als
entsprechend gekennzeichneter historischer Eintrag herunterladen.

## Formatvertrag

Die Formatentscheidungen folgen dem [Originaltext von RFC 5545](https://www.rfc-editor.org/rfc/rfc5545.html):

| Regel | Umsetzung und Quelle |
|---|---|
| Zeichencodierung und Zeilen | UTF-8 ohne BOM, CRLF, maximal 75 Oktette je physischer Zeile; Faltung an Unicode-Zeichengrenzen. [§3.1](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.1) |
| Freitext | Backslash, Komma, Semikolon und Zeilenumbrüche werden maskiert; unzulässige Steuerzeichen werden abgewiesen. [§3.3.11](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.3.11) |
| Fälligkeit | `DTSTART;VALUE=DATE` ist die Fälligkeit; `DTEND;VALUE=DATE` der folgende Kalendertag als exklusives Ende. [§3.6.1](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.6.1) |
| Identität | Stabile `UID` aus Betriebs- und Terminkennung; Änderungen behalten diese Identität. [§3.8.4.7](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.8.4.7) |
| Änderungsstand | `SEQUENCE = Revision − 1`; UTC-Zeitpunkte für `CREATED`, `LAST-MODIFIED` und `DTSTAMP`. Ohne `METHOD` entspricht `DTSTAMP` dem letzten Änderungszeitpunkt. [§3.8.7](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.8.7) |
| Abschluss | `VEVENT` unterstützt keinen Status `COMPLETED`. Der Eintrag bleibt `CONFIRMED`; Titel und `X-FIRSTBLICK-MAINTENANCE-STATUS` kennzeichnen den Abschluss. [§3.8.1.11](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.8.1.11) |

Es gibt kein `METHOD`, keine Teilnehmer und keine Organisatoren: Der Download
transportiert eine Momentaufnahme, keine Einladung. Diese Abgrenzung entspricht
[RFC 5545 §3.7.2](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.7.2).

Die UID bleibt bei erneutem Download desselben Vorgangs gleich. Die Anwendung
erzeugt für unveränderte Daten identische Bytes. Jede gespeicherte Revision des
Vorgangs erhöht die Sequenz, auch wenn nur ein nicht exportierter Prüfschritt
geändert wurde. Dadurch bleibt die Zuordnung zum vollständigen Datenbankstand
eindeutig. Eine Revision außerhalb des RFC-Integerbereichs oder eine Fälligkeit
ohne darstellbaren Folgetag wird ausdrücklich zurückgewiesen.

## HTTP- und Berechtigungsvertrag

`GET /api/v1/maintenance-visits/{id}/calendar?revision={revision}` liefert die
geöffnete Revision mit `Content-Type: text/calendar; charset=utf-8`, sicherem
Dateinamen, `ETag` und `X-Content-SHA256`. Eine zwischenzeitliche Änderung führt
zu `409 revision_conflict`; die Oberfläche fordert zum erneuten Öffnen auf.
Der Export erstellt keine neue Entität und verändert den Termin nicht.

Für jeden Abruf werden die aktuellen Betriebs-/Leserechte geprüft.
Mitarbeitende erhalten nur Termine ihrer aktuell sichtbaren Projektzuordnungen.
Widerrufene Zuordnungen, fremde Betriebe und entzogene Mitgliedschaften geben
keine Datei heraus. Entsprechend der bestehenden Leseregel bleiben gespeicherte
Termine bei deaktiviertem Wartungsmodul lesbar und exportierbar.
`Cache-Control: private, no-store` verhindert die vorgesehene Speicherung in
HTTP-Caches; eine bewusst heruntergeladene Datei liegt danach außerhalb der
Zugriffskontrolle von FirstBlick.

Die Angular-Oberfläche prüft MIME-Typ, Größe, Revision, SHA-256 und die Bindung
von UID, Fälligkeit und Status an den geöffneten Termin. Vor dem Erzeugen des
Downloads prüft sie den aktuellen Betrieb, die Berechtigungen und die Auswahl
erneut, auch nach dem asynchronen Lesen und Hashen der Antwort.

## Verifikation und verbleibende Grenzen

Die Regressionen stehen in:

- `tests/FirstBlick.Core.Tests/Business/MaintenanceCalendarTests.cs`: Monats-/Jahresende,
  Schaltjahr, Zeitumstellung, stabile Identität, Abschluss, Maskierung,
  UTF-8-Faltung, Datenminimierung und ungültige Quellen.
- `tests/FirstBlick.Api.Tests/MaintenanceWorkflowApiTests.cs`: echte
  HTTP-/Identity-/PostgreSQL-Abrufe, Revision, Hash, Wiederholung, aktueller
  Abschluss, Mandanten-/Projektgrenzen und Rechteentzug.
- `web/tests/maintenance-calendar.test.ts`: Antwortbindung und Ablehnung
  widersprüchlicher oder beschädigter Kalenderdaten.
- `web/tests/browser/maintenance.spec.ts`: tatsächlicher Datei-Download mit
  kontrollierten HTTP-Antworten, deaktiviertes Modul, Fehler und Betriebswechsel
  während Antwort bzw. Hashprüfung auf Desktop, Tablet und Mobil.

Testquellen sind keine Behauptung eines bestandenen Laufs. Der veröffentlichte
Commit benötigt seine eigenen CI-/Operations-Nachweise.

Eine Kalenderanwendung kann die Datei importieren, sofern sie dieses
RFC-5545-Teilprofil unterstützt. Das Verhalten bei erneutem manuellen Import
hängt von der Kalenderanwendung ab; gleiche UIDs garantieren bei solchen
Importfunktionen keine automatische Dublettenvermeidung. Ein bestimmter
Google-/Microsoft-/Apple-Client wurde durch diese Tests nicht bestätigt.
Bidirektionale Synchronisierung, Abonnements, Providerzustellung,
Einladungsantworten und automatische Aktualisierung bereits importierter
Dateien bleiben außerhalb dieses Teilumfangs von **DP-09-02**.
