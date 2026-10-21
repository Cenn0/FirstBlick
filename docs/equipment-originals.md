# Originaldateien zu abgeschlossenen Geräteprüfungen

Stand: 21.10.2026 · FirstBlick · **DP-07-05**, technischer Teilumfang.

Im [Geräteprüfungsarbeitsplatz](equipment-inspections.md) besitzt jedes abgeschlossene
Prüf-, Wartungs- oder Reparaturergebnis einen Bereich **Originalprüfberichte**. Dort lassen
sich mehrere PDF-, PNG- oder JPEG-Dateien bis jeweils 25 MiB ergänzen und später
herunterladen. Die Datei bleibt an genau diesen historischen Abschluss gebunden,
auch wenn das Gerät inzwischen weitere Prüfungen durchlaufen hat.

## Ablauf und erhaltene Nachweise

1. Eine abgeschlossene Prüfung öffnen und ihre Originaldateien laden.
2. Die Originaldatei auswählen. Dateiname, Typ, Größe und Dateikennung werden geprüft;
   der Browser berechnet SHA-256 aus den tatsächlichen Bytes.
3. **Originalbeleg speichern** legt zunächst einen vorgemerkten Datensatz an und
   überträgt anschließend die Datei. Die API prüft die Bytes, schreibt eine konkrete
   Objektspeicherversion und liest diese vor der Erfolgsbestätigung erneut vollständig.
4. Erst nach erfolgreicher Prüfung erscheint die Datei als verfügbar. Ein unterbrochener
   Upload bleibt wiederaufnehmbar: Dafür muss dieselbe Originaldatei mit gleichem Namen,
   Typ, Länge und SHA-256 erneut ausgewählt werden.
5. Jeder Download liest die gespeicherte Version und überprüft Größe, Dateikennung und
   SHA-256 erneut. Auch der Browser prüft die Antwort vor dem lokalen Download.

Das Anhängen einer Datei verändert weder den bereits abgeschlossenen Prüfvorgang noch
dessen Ergebnisdigest oder den Gerätestand. Gespeichert werden Originalbytes sowie
Dateimetadaten, Uploadbenutzer, Zeitpunkt, Betrieb, Prüfungskennung und Abschlussdigest.
Eine neue Datei erhält eine eigene Dokumentkennung; eine bereits verfügbare Datei
kann nicht über den Uploadbefehl ersetzt werden.

Die bestehende Dokumentverwaltung kann eine verfügbare Datei archivieren. Ihr Original
bleibt danach in der Prüfungshistorie lesbar. Ein direkter Link mit
`/equipment-inspections?inspectionId=…` öffnet auch ältere Prüfungen außerhalb der
aktuellen Historienseite. Vorgemerkte Originale verweisen aus der Dokumentverwaltung
auf diesen Arbeitsplatz zur Wiederaufnahme.

## Rechte und Schnittstellen

Geschäftsführung, Büro und Bauleitung dürfen bei aktivem Einkaufs-/Gerätemodul Dateien
ergänzen. Die Buchhaltung besitzt entsprechend den bestehenden Prüfungsrechten
Lesezugriff. Mitarbeitende und externe Partner erhalten keinen Zugriff. Bereits
vorhandene Akten bleiben für berechtigte Personen lesbar, wenn das Modul deaktiviert
wird. Einen vorgemerkten Upload dürfen seine erfassende Person sowie Geschäftsführung
und Büro fortsetzen.

| Methode und Pfad | Zweck und Revision |
|---|---|
| `GET /api/v1/equipment-inspections/{id}/documents?page=…` | Originale der abgeschlossenen Prüfung, 20 pro Seite, mit Quellenbezug. |
| `POST /api/v1/equipment-inspections/{id}/documents` | `{name, contentType, size, sha256, projectId: null}` vormerken; `If-Match` mit Prüfungsrevision. |
| `PUT /api/v1/equipment-inspections/{id}/documents/{documentId}/content` | Originalbytes übertragen; `If-Match` mit Dokumentrevision. |
| `GET /api/v1/equipment-inspections/{id}/documents/{documentId}/download` | Verifizierte Originalbytes mit Dateiname und `X-Content-SHA256` herunterladen. |

Schreibbefehle verlangen einen `Idempotency-Key`. Aktuelle Mitgliedschaft, Rolle,
Modul, Uploadberechtigung und Prüfungsbezug werden auch vor einem Befehls-Replay
geprüft. Eine Wiederholung erzeugt keine weitere Dokument- oder Speicherversion.
Der allgemeine Dokument-Upload akzeptiert keine prüfungsgebundenen Originale;
deren Schreibrechte werden über die dargestellten Endpunkte geprüft.

Ein konfigurierter, versionierter Objektspeicher ist erforderlich. Eine fehlende
Version, abweichende Länge, falsche Signatur oder falsche Prüfsumme führt zu einem
Fehler. Eine fehlgeschlagene Prüfung erzeugt keinen verfügbaren Dokumentdatensatz.
Eventuell bereits übertragene, aber nicht bestätigte Speicherobjekte sind kein
gültiger Fachnachweis und werden durch einen erneuten Upload nicht als erfolgreich
übernommen. Eine automatische Bereinigung solcher Speicherreste ist nicht enthalten.

Der Client bindet Antworten an Betrieb, Rechte, Prüfung, Seite und Dokumentrevision.
Er verwirft verspätete Antworten auch nach dem asynchronen Lesen und Hashen von Dateien.
Ein Fehler lässt die konkrete Wiederaufnahme beziehungsweise das erneute Laden zu;
er wird nicht als erfolgreich archivierte Datei angezeigt.

## Migration und Verifikation

Der kanonische Import prüft abgeschlossenen Quellvorgang, Betrieb, Digest,
Dokumentformat, Versionsmarker und zeitliche Reihenfolge. Er erhält verfügbare und
archivierte Originale samt ihren Bytes. Die Prüffälle umfassen falsche Quelle,
offene oder abgesagte Vorgänge, vertauschte Digests, falsche Versionen und unbekannte
historische Benutzer.

- [API und Quellenbindung](../src/FirstBlick.Api/EquipmentInspectionDocumentEndpoints.cs),
  [gemeinsamer verifizierter Dateitransfer](../src/FirstBlick.Api/DocumentEndpoints.cs).
- [HTTP-/Identity-/PostgreSQL-Regressionen](../tests/FirstBlick.Api.Tests/EquipmentInspectionDocumentApiTests.cs)
  verwenden einen versionierten Objektspeicher mit gezielter Fehlerinjektion.
- [Clientverträge](../web/tests/equipment-documents.test.ts) und
  [responsive Browserfälle](../web/tests/browser/equipment-documents.spec.ts)
  prüfen Antworten, echte Browserdownloads und Kontextwechsel mit kontrollierten HTTP-Antworten.
- [Containerablauf](../scripts/contacts_calendar_originals_smoke.py) prüft die echte
  Identity/API/S3-Kette und vergleicht dieselben Identitäten, Bytes und Versionen nach Neustart.
- [Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) und
  [Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs) ergänzen positive
  und abzuweisende Importfälle.

Ausführungsnachweise gelten für den jeweils geprüften Commit. Die Dateiprüfung
bestätigt Herkunftsbindung und unveränderte Bytes, keine inhaltliche Richtigkeit,
digitale Unterschrift oder gesetzliche Prüfberechtigung. Automatische Prüffristen,
unabhängige Fristen je Prüfart, Fahrtenbuch und Fahrzeugkosten bleiben offen.
