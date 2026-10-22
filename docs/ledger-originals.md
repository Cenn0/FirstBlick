# Originaldateien an Kommunikation und Fahrzeugkosten

Stand: 22.10.2026 · FirstBlick · **DP-04-01 / DP-09-03 / DP-07-05**.

Gespeicherte Einträge in der Kommunikationsakte und im Fahrzeugkostenregister können
mehrere **PDF-, PNG- oder JPEG-Dateien** erhalten. Die Originaldateien werden direkt
am jeweiligen Eintrag angezeigt. Die Liste enthält verfügbare, archivierte und noch
nicht fertig hochgeladene Dateien mit Seitenwechsel, 20 Dateien pro Seite.

## Speicherung und Wiederaufnahme

Die Dateiauswahl berechnet zunächst SHA-256 und legt Metadaten mit Dateiname, Typ,
Größe und Hash an. Danach überträgt der Client die unveränderten Bytes. Unterbrochene
Uploads lassen sich mit derselben Originaldatei wieder aufnehmen. Pro Datei gilt
eine Obergrenze von **25 MiB**.

Auch die allgemeine Dokumentenliste öffnet die zugehörige Originalakte. Bei einem
unvollständigen Upload führt „In der Originalakte fortsetzen“ zur gleichen
quellengebundenen Dateiverwaltung. Der Server liefert dafür den aktuellen, auf
Leserechte geprüften Quellkontext einschließlich Titel und Elternobjekt.

Der Server prüft die Revision des Originaleintrags beim Anlegen der Datei sowie die
Dokumentrevision beim Upload. Größe, MIME-Typ, Dateikennung und SHA-256 müssen zu den
Metadaten passen. Nach dem Schreiben in den versionierten Objektspeicher wird genau
die zurückgegebene Version erneut gelesen und geprüft. Erst dann erhält die Datei
den Zustand `Ready`. Idempotente Wiederholungen erzeugen keine weiteren Dateien oder
Speicherversionen.

Jede Datei ist an `SourceKind`, `SourceId` und den unveränderlichen Digest des
Gesprächs- beziehungsweise Kosteneintrags gebunden. Die Marker
`customer-communication-original-v1` und `vehicle-cost-original-v1` unterscheiden
die beiden Quellen. Ein nachträglicher Upload ändert weder den Originaleintrag noch
seinen Digest, die Kunden-/Fahrzeugrevision oder die wirksamen Kostenbeträge.

## Lesen nach Storno und Archivierung

Bereits gespeicherte Dateien bleiben nach einem Gesprächsstorno, Kostenstorno oder
der historischen Archivierung der zugehörigen Stammdaten lesbar. Ein archiviertes
Dokument bleibt am Originaleintrag auffindbar und herunterladbar. Neue Dateien und
Upload-Wiederholungen sind nach einem Storno beziehungsweise einer Elternarchivierung
gesperrt. Der Server prüft diesen Zustand unter dem Schreib-Lock erneut.

Downloads lesen die konkrete Speicherversion und prüfen Größe und Hash. Die Antwort
enthält `X-Content-SHA256`, einen Dateianhang-Header und eine Vorgabe gegen Caching.
Der Client vergleicht die Bytes ebenfalls mit dem erwarteten Hash, bevor er sie
anbietet. Ein Betriebs-, Rechte- oder Quellwechsel verwirft verspätete Antworten und
auch verspätete Ergebnisse einer asynchronen Hashprüfung.

## Rechte und Quellenbindung

Die Rechte des **Originaleintrags** gelten auch für seine Dateien. Bei Gesprächen
dürfen Geschäftsführung und Büro hochladen; bei Fahrzeugkosten zusätzlich die
Bauleitung. Die jeweiligen Leserollen können vorhandene Belege abrufen. Der Server
prüft aktuelle Rechte und das aktivierte Quellmodul auch vor einem Schreib-Replay.

Die Dokumente besitzen absichtlich keinen eigenen `ProjectId`-Zugriffspfad. Ein
Projektbezug des Gesprächs oder Kosteneintrags erweitert damit nicht automatisch
die Rechte von Mitarbeitenden auf diese Akten. Die allgemeinen Dokumentendpunkte
prüfen ebenfalls die Quellrechte. Ein Upload über deren generische Route kann die
spezifische Quellenbindung nicht umgehen.

## Schnittstellen

`{source}` steht für `customer-communications` oder `vehicle-costs`:

| Methode und Pfad | Zweck |
|---|---|
| `GET /api/v1/{source}/{id}/document-context` | Auf Quellrechte geprüfter Titel, Elternbezug und Originalidentität für die allgemeine Dokumentenliste; `private, no-store`. |
| `GET /api/v1/{source}/{id}/documents?page=1` | Quellenkennung, Originaldigest, aktuelle Anhangberechtigung und paginierte Dateiliste. |
| `POST /api/v1/{source}/{id}/documents` | `{name,contentType,size,sha256,projectId:null}`; `If-Match` mit Originalrevision und Idempotenzschlüssel. |
| `PUT /api/v1/{source}/{id}/documents/{documentId}/content` | Unveränderte Bytes; Dokumentrevision, Content-Type, Content-Length und Idempotenzschlüssel. |
| `GET /api/v1/{source}/{id}/documents/{documentId}/download` | Verifizierte Originalbytes einschließlich archivierter Dateien. |

## Verifikation und Grenzen

- [API](../src/FirstBlick.Api/LedgerDocumentEndpoints.cs) und
  [HTTP-/PostgreSQL-Tests](../tests/FirstBlick.Api.Tests/LedgerDocumentApiTests.cs)
  prüfen beide Quellarten, Originalerhalt, Dateiintegrität, Wiederaufnahme,
  Quellvertauschung, Archivierung, Rechte und Idempotenz.
- Client- und responsive Browserfälle prüfen die direkte Bedienung in beiden Akten
  sowie Datei-, Kontext- und Antwortbindung.
- [Migrationsgraph](../tools/FirstBlick.Operations/MigrationGraph.cs) und
  [Migrationsdrill](../tools/FirstBlick.Operations/MigrationLedgerOriginals.cs) erhalten
  aktive und archivierte Belege einschließlich Quelle, Version, Hash und historischem
  Uploader. Veränderte Quellen, Digests, Formate, Projektbindungen, Zeitfolgen,
  unzulässige Dateinamen oder fehlende Speicherversionen werden abgewiesen. Die
  Importprüfung verlangt außerdem eine abgeschlossene Uploadrevision und dieselbe
  Dateikennung wie die Leserouten.
- Der [echte Container-/S3-/Neustartablauf](../scripts/project_templates_originals_smoke.py)
  lädt mehrere Originale je Quellart hoch, archiviert einen Beleg, storniert die
  Originaleinträge und prüft ihre Dateien danach erneut bytegenau.

Die Dateiprüfung bestätigt die gespeicherten Bytes und deren Herkunft. Sie umfasst
keine vollständige PDF-Inhalts-/Schadsoftwareprüfung, OCR, elektronische Signatur
oder Rechnungsprofilvalidierung. Ein Fahrzeugkostenbeleg bucht keine Lieferanten-
rechnung und ändert keine steuerliche Bewertung. Postfachübernahme, Telematik und
eine weitergehende Kosten-/Buchhaltungsintegration bleiben gesonderte Roadmap-Themen.
