# Projekt- und Aufgabenaktivität

Die Seite `/business-activity` ergänzt **DP-14-01** um drei definierte Zeitvergleiche und **DP-14-02** um einen nach Zeitraum, Projekt und Archivzustand gefilterten Bericht mit CSV-/JSON-Download. Beide Roadmap-Aufgaben behalten den Status **T**. Ein umfassender KPI-Katalog und frei konfigurierbare Analysen bleiben offen.

## Definitionen

| Kennzahl | Quelle und Zählregel | Aussagegrenze |
| --- | --- | --- |
| Projekte angelegt | Ein Projekt zählt, wenn sein gespeicherter `CreatedAt`-Zeitpunkt in der Betriebszeitzone im Zeitraum liegt. | Kein Auftragseingang und keine Aussage über Umsatz, Fortschritt oder Fertigstellung. |
| Aufgaben angelegt | Eine Aufgabe zählt nach ihrem gespeicherten `CreatedAt`-Zeitpunkt in der Betriebszeitzone. | Importierte Daten verwenden das übernommene Anlagedatum. Spätere Bearbeitung ändert diese Zählregel nicht. |
| Aufgaben mit Fälligkeit | Eine Aufgabe zählt, wenn ihr **aktuell gespeichertes** `DueDate` im Zeitraum liegt; ihr heutiger Status ist dafür unerheblich. | Kein historischer Rückstand und keine zeitliche Erledigungsquote. Eine heutige Terminänderung kann die Auswertung vergangener Zeiträume verändern. |

Der gewählte Zeitraum umfasst beide Grenztage und höchstens 366 Tage. Der Vorzeitraum ist unmittelbar davor gelegen und hat genau dieselbe Anzahl Kalendertage. Beide Zeiträume werden zusätzlich in ihre tatsächlich enthaltenen Monatsabschnitte aufgeteilt. Beispiel: 01.–22.10.2026 wird mit 09.–30.09.2026 verglichen, nicht mit einem vollständigen Kalendermonat. Monatsgrenzen, Schaltjahre und unterschiedliche Monatslängen werden kalenderbasiert behandelt.

Jede Quelle erscheint einmal in der Quellenliste, kann aber mehrere Kennzahlen betreffen. Deshalb ist die Zahl der Quellen nicht zwingend die Summe der Kennzahlen. Quellen, die nur den Vorzeitraum betreffen, bleiben ebenfalls enthalten. Aktuelle Statuswerte dienen zur Einordnung und werden nicht als historische Statusstände dargestellt.

## Bedienung und Zugriffsgrenzen

Geschäftsführung, Büro, Buchhaltung und Bauleitung können die betriebsweite Ansicht mit den aktuellen Leserechten für Projekte und Aufgaben öffnen. Persönliche Mitarbeiter- und Portalrollen erhalten diesen Bericht nicht. Die API prüft die Berechtigung nach dem Warten auf die betriebsbezogene Transaktionssperre erneut.

Die Ansicht bietet Datumsfelder, Projektsuche, einen ausdrücklich gewählten Projektfilter und eine Option für archivierte Datensätze. Standardmäßig sind archivierte Quellen ausgeschlossen. Die Filter gelten für den aktuellen Zeitraum und den Vorzeitraum gleichermaßen. Ein Projektfilter ist immer an denselben Betrieb gebunden. Notizen, Ansprechpartner und andere nicht benötigte Projekt- oder Aufgabenfelder sind kein Bestandteil der SQL-Projektion oder der Exporte.

Quellen verlinken auf die konkrete Projekt- beziehungsweise Aufgabenkennung. Die UI zeigt jeweils 25 Quellen und ermöglicht weitere Seiten. Der Server akzeptiert insgesamt höchstens **5.000 Quellen für beide Zeiträume zusammen**. Bei Überschreitung wird der Bericht abgewiesen und eine Eingrenzung verlangt; es gibt keine still abgeschnittenen Summen.

Geänderte Filter sperren den Export bis zum erneuten Laden. Antworten und Downloads werden nach Betriebs-, Rechte- oder Seitenwechsel verworfen, auch wenn die asynchrone Hashprüfung erst später endet.

## HTTP-Vertrag

`GET /api/v1/reports/business-activity`

| Parameter | Bedeutung |
| --- | --- |
| `from` | Erster Kalendertag, erforderlich, `YYYY-MM-DD`. |
| `to` | Letzter Kalendertag einschließlich, erforderlich, `YYYY-MM-DD`. |
| `projectId` | Optional: vollständige Projekt-UUID desselben Betriebs. |
| `includeArchived` | Optional: `true` oder `false`, standardmäßig `false`. |

Unbekannte oder wiederholte Parameter werden zurückgewiesen. Die Antwort enthält Schema `firstblick.business-activity/1.0.0`, Betrieb, normalisierte IANA-Betriebszeitzone, Erfassungszeitpunkt, angewandte Filter, beide Perioden mit Monatsabschnitten, sämtliche Quellen und einen Berichts-Digest. Unterstützte Windows-Zeitzonen werden vor SQL-Auswertung und Browserausgabe nach IANA übersetzt.

Der Bericht verwendet gespeicherte Daten unter derselben betrieblichen Transaktionssperre wie die schreibenden Kommandos. Dadurch gehören Quellen, Revisionen und Summen zu einem konsistent gelesenen Stand. Die HTTP-Antwort ist `no-store, private`.

`GET /api/v1/reports/business-activity/export` benötigt dieselben Filter sowie:

| Parameter | Bedeutung |
| --- | --- |
| `format` | `csv` oder `json`. |
| `expectedDigest` | Digest des zuvor geladenen und angezeigten Berichts. |

Der Server berechnet den Bericht für den Download erneut. Ändern sich relevante Quellen, Revisionen, Filter oder Zeitzone, liefert er **HTTP 409 / `activity_report_changed`**. Ein erneutes Laden ist dann erforderlich. Der spätere Erfassungszeitpunkt allein verändert den Digest nicht. Der Digest ist ein Integritätsbezug innerhalb dieses Vertrags, keine externe Signatur oder beweiskräftige Zeitstempelung.

## Exportformate und Integritätsprüfung

JSON enthält den vollständigen Bericht einschließlich Summen, Monatsabschnitten und unveränderten Quellbezeichnungen. CSV enthält eine Quellenzeile je Datensatz mit Kennung, Revision, Projekt, Bezeichnung, aktuellem Status, Archivzustand, Anlage-/Fälligkeitsdatum, beiden Zeiträumen, Zeitzone, Betrieb und Berichts-Digest. Die CSV ist UTF-8 ohne BOM, verwendet Semikolon, doppelt zitierte Datenfelder und CRLF-Zeilenenden. Bei einem leeren Ergebnis bleibt die Kopfzeile erhalten; der vollständige Nullbericht ist als JSON verfügbar.

Der Download liefert `X-Content-Sha256`, `X-Report-Digest` und `X-Report-Tenant`. Die UI prüft MIME-Typ, Größe, Betrieb, Berichtsdigest und SHA-256 der tatsächlichen Bytes. Sie prüft JSON erneut gegen Filter, Quellen und nachgerechnete Summen; CSV muss vollständig mit den angezeigten Quellen und dem festgelegten Format übereinstimmen. Eine passende Kopfzeile allein genügt nicht.

CSV-Felder mit einem führenden Formelzeichen (`=`, `+`, `-`, `@` und deren erfassten Vollbreitenvarianten, auch nach führendem Leerraum) beziehungsweise führendem Tabulator/Zeilenumbruch erhalten zusätzlich ein Apostroph. Doppelte Anführungszeichen werden verdoppelt. Diese Behandlung verringert das Risiko unerwarteter Formelauswertung beim Import; Verhalten beim Öffnen und erneuten Speichern hängt vom Tabellenprogramm ab. Die [OWASP-Erläuterung zu CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection) beschreibt diese Grenze. Für unveränderte Originaltexte und maschinelle Weiterverarbeitung dient JSON. Eine allgemeine Sicherheitsgarantie für beliebige Tabellenprogramme wird nicht behauptet.

## Umsetzung und Nachweise

- [Rechenkern und Exportformat](../src/FirstBlick.Core/Business/BusinessActivity.cs)
- [Betriebsgebundene API und begrenzte SQL-Projektion](../src/FirstBlick.Api/BusinessActivityEndpoints.cs)
- [Clientvertrag, unabhängige Summenprüfung und Downloadprüfung](../web/src/app/core/business-activity.models.ts)
- [Angular-Bedienung](../web/src/app/pages/business-activity.component.ts)
- [Corefälle](../tests/FirstBlick.Core.Tests/Business/BusinessActivityTests.cs): Kalendergrenzen, Kennzahlendefinitionen, Quellen, Digest und CSV-Zellen.
- [Echte HTTP-/Identity-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/BusinessActivityApiTests.cs): Betriebsgrenzen, Zeitzonen, Filter, Archive, Exporte, geänderte Quellen, Rollen, Quellenbudget und entzogene Rechte während einer beobachteten Sperrwartezeit.
- [Clientfälle](../web/tests/business-activity.test.ts) und [responsive Browser-Fixtures](../web/tests/browser/business-activity.spec.ts): unplausible Antworten, Summen, vollständige Exportinhalte, Hashfehler, Filter- und Kontextwechsel.
- [Container-/Neustartablauf](../scripts/roadmap_activity_smoke.py): tatsächliche API-Quellen, Berichte und Exportbytes vor und nach Neustart.

Testdateien dokumentieren Prüfungen; ein erfolgreicher Lauf muss am konkreten veröffentlichten Commit durch CI und Operations belegt sein. Browser-Fixtures verwenden kontrollierte Antworten und ersetzen den API-/PostgreSQL- oder Container-Nachweis nicht. Die Berichte sind flüchtige, nachladbare Projektionen und erzeugen keine zusätzlichen zu migrierenden Berichtsdatensätze.
