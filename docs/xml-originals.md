# Originale XML-Akten: Aufbewahrung, Wiederaufnahme und Herkunft

Diese Erweiterung setzt einen zusammenhängenden Teil von **DP-08-03**, **DP-12-02** und **DP-02-05** um. Aus der bisherigen reinen Dateivorschau wird eine wiederauffindbare Originalakte mit konkreter Speicherversion, Eingangsbeleg, nachvollziehbarer Herkunft und überprüftem Download. Das ist keine vollständige E-Rechnungs- oder GAEB-Implementierung und kein zertifiziertes Langzeitarchiv.

## Bedienablauf

Auf **GAEB & E-Rechnungen** die Originaldatei auswählen und zunächst prüfen. **Geprüftes XML-Original ablegen** speichert bewusst die unveränderten Bytes. Dieser Schritt bucht keine Eingangsrechnung und erzeugt keine Katalogposition. Eine unter anderem Namen erneut vorgelegte, byteidentische Datei desselben Verfahrens und Betriebs verweist auf die bereits vorhandene Akte; ihr erster Eingangsbeleg bleibt erhalten.

**Originalakten öffnen** lädt eine paginierte Übersicht. **Originalakte … prüfen** liest die konkrete gespeicherte Version vollständig zurück, kontrolliert Größe und SHA-256 und führt die vorhandene strukturelle Sichtung erneut aus. Die Oberfläche zeigt Eingangszeit, erfassendes Konto, Dateiversion, Originalprüfsumme und Belegdigest. Listenmetadaten behaupten ausdrücklich keine erneute Byteprüfung.

**Originaldatei herunterladen** überprüft zusätzlich im Browser die tatsächlich empfangenen Bytes; ein passender HTTP-Header allein reicht nicht. **Original im Prüfbereich öffnen** bringt genau diese verifizierte Datei zurück in den vorhandenen GAEB-/E-Rechnungsablauf. Ungespeicherte GAEB-Kalkulationswerte werden nur nach bestätigter Rückfrage verworfen. Es wird kein HTML oder XML im Anwendungsursprung ausgeführt.

Bei GAEB zeigt die Akte die tatsächlich übernommenen Katalogpositionen mit demselben ursprünglichen Quell-SHA und ihrer Positionskennung. Die Anzeige verwendet aktuelle Katalognamen und Archivzustände, **keinen eingefrorenen historischen Preisstand**. Ein Originaldownload ist auch kein GAEB-Neuexport oder garantierter Angebotsabgabe-Roundtrip.

## Schnittstelle und Datenvertrag

Basis: `/api/v1/integrations/v1/trade-sources/{format}`, `format` ist ausschließlich `gaeb` oder `e-invoice`.

| Methode | Vertrag |
|---|---|
| `POST /{format}` | `fileName`, `sourceBase64`, `expectedSha256`; stabiler `Idempotency-Key`; ausdrückliche Ablage |
| `GET /{format}?page=1&pageSize=20` | Tenantgebundene Metadaten; maximal 50 Einträge pro Seite; `contentVerified=false` |
| `GET /{format}/{id}` | Verifizierte Originalquelle, erneute strukturelle Vorschau und GAEB-Katalogbezüge |
| `GET /{format}/{id}/original` | Byteidentischer Anhang, `application/octet-stream`, `nosniff`, `no-store`, `X-Content-SHA256` |

Belegvertrag `firstblick.trade-source/1.0.0`. Gebunden werden Betrieb, Quell-ID, Dokument-ID, Dateiname, Bytezahl, SHA-256, konkrete Objektspeicherversion, erfassendes Konto und UTC-Zeit. Die Registrierung verwendet `gaeb-sources` bzw. `e-invoice-sources`. Der existierende tenant-/kindbezogene Datenbank-Unique-Key schützt identische SHA-256 gegen Doppelanlage. Hierfür ist keine zusätzliche EF-Schemaänderung erforderlich.

## Transaktion, Integrität und Zugriff

Die Originaldatei ist auf 1 MiB begrenzt. Dateipfade, falsche Endungen, ungültige Base64-Werte, abweichende erwartete Hashes, nicht unterstützte XML-Syntax und DTDs werden vor dem Speichern abgewiesen. Die bestehenden XML-Budgets und die abgeschaltete externe Auflösung bleiben wirksam.

Das System verlangt versionierten Speicher mit einer konkreten, nicht `null` lautenden Versionskennung. Nach dem Schreiben werden genau diese Version und alle Bytes zurückgelesen. Erst danach werden Quellbeleg, Dokumentmetadaten, Audit und Idempotenzbeleg in einer gemeinsamen PostgreSQL-Transaktion veröffentlicht. Eine fehlgeschlagene Datenbanktransaktion kann einen **nicht referenzierten Speicherinhalt** hinterlassen; es wird keine verteilte Transaktion zwischen PostgreSQL und S3 behauptet. Eine spätere, gesondert abgesicherte Bereinigung solcher verwaisten Objekte bleibt offen.

Erneute Ablage und Lesen prüfen die Tenant-, Revisions-, Dokument-, Versions- und Hashbindungen. Finanzoriginale sind auch über generische Dokumentlisten und Downloads nicht für Bauleitung oder Mitarbeiter sichtbar. Selbst Geschäftsführung kann Quellbelege nicht über generische Erstellen-/Ändern-/Löschen-Endpunkte umschreiben. Moduldeaktivierung sperrt neue Ablagen, lässt zulässige Historienlesung bestehen; entzogenes Mitgliedsrecht sperrt auch Historie und Idempotenzwiederholung. Kontextwechsel und verspätete Antworten dürfen zuvor geladene Browserdaten nicht wiederherstellen.

**Wichtige Grenzen:** Versionsbindung und Hashes sind weder digitale Signatur noch WORM-Speicher, Aufbewahrungsfrist, gesetzliche Archivierungsbescheinigung oder Schutz gegen einen Administrator, der Datenbank und Speicher zugleich kontrolliert. Die erste Erfassung beweist nicht die Identität des ursprünglichen XML-Absenders. Malware-/Signaturprüfung, Uploadquoten je Betrieb, Lebenszyklus-/Löschkonzept und externes revisionssicheres Archiv sind getrennte Aufgaben.

## Tests und Nachweisarten

- `TradeSourceTests.cs`: Belegbindung, sichere Dateinamen, Versionspflicht, allgemeine Mutationssperre.
- `TradeSourceApiTests.cs`: echte HTTP-/PostgreSQL-Tests, parallele Dubletten, getrennte Betriebe, Berechtigungen, Idempotenz, Modulwechsel, Rollback, JSONB-Manipulation und importierte GAEB-Referenzen. Speicherfehler werden hier **gezielt durch einen versionierten Testadapter** injiziert.
- `trade-source.test.ts`: Laufzeitvalidierung von Antworten und tatsächliche, begrenzte Downloadbyteprüfung.
- `trade-archive.spec.ts`: kontrollierte Browser-HTTP-Fixtures auf den drei bestehenden Viewports, einschließlich unsicherer Antworten, Verbindungsabbruch, Fremdmandant, beschädigter Bytes, verspätetem Download und abgebrochenem Verwerfen ungespeicherter Werte.
- `trade_sources_smoke.py`, aufgerufen von `container-smoke.py`: **tatsächlich laufende API, PostgreSQL und versioniertes S3**. Beide synthetischen XML-Formate werden abgelegt, unverändert heruntergeladen und dedupliziert; GAEB wird ausdrücklich geprüft in den Katalog übernommen. Der erneute Aufruf nach API-/Worker-Neustart verlangt dieselben Quell-/Dokument-/Versionskennungen und Katalogbezüge. Der vorhandene Operations-Workflow sichert und verifiziert diese Anwendungsdaten vor dem Neustart zusätzlich.

Ein vorhandener Test ist kein erfolgreicher Ausführungsnachweis. Ausschlaggebend sind die tatsächlich abgeschlossenen CI-/Operations-Läufe des jeweiligen veröffentlichten Commits; sie werden im zugehörigen Pull Request verlinkt. Fixture-Browsertests werden nicht als echte End-to-End-Browserintegration mit S3 ausgegeben.

## Verbleibender Roadmap-Umfang

| Aufgabe | Jetzt ergänzt | Weiter nicht durch diesen Teilumfang erfüllt |
|---|---|---|
| DP-08-03 | Original-Eingangsakte, genaue Byteherkunft, Wiederaufnahme, geschützter Download, Dubletten | Vollständige profilversionierte XSD-/Schematron-Prüfung, ZUGFeRD-PDF/A-3, geprüfte Buchung und strukturierte Ausgabe |
| DP-12-02 | Aufbewahrtes GAEB-Original und nachvollziehbare Zuordnung zu übernommenen Katalogpositionen | Vollständiger GAEB-Standard, originalgetreue Fachstruktur, geprüfter Rückexport, lizenzierte externe Kataloge |
| DP-02-05 | Versionierte Originaldateien mit atomar veröffentlichten Metadaten und UI | Malwareprüfung, Lebenszyklus, verwaiste Objekte, gesetzliche Archivanforderungen |

Die laufende parallele Arbeit an offiziellen Validierungsressourcen ist davon getrennt. Ein erfolgreicher Ressourcen-Download oder ein gestartetes Validator-JAR ändert die Konformitätskennzeichnung dieser strukturellen Vorschau **nicht**.
