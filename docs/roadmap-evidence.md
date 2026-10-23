# Nachweismatrix für alle 100 Roadmap-Aufgaben

Letzte begrenzte Fortschreibung: **23.10.2026** für **DP-02-06**: begrenzte Quellenleser, selektive Finanz-/Berechtigungsquellen und ein gemeinsames Quellenbudget. Die früheren Ergänzungen bleiben unten erhalten. Kategorien und ursprüngliche Abnahmekriterien bleiben unverändert; keine Gesamtabnahme wird behauptet.

## Betriebsanlage und acht fachliche Herstellerreferenzen (23.10.2026)

**DP-03-01 bleibt K.** [Betriebsanlage](business-onboarding.md) ergänzt nach dem einmaligen Betreiber-Bootstrap eine angemeldete, geschäftsführungsgebundene Anlage weiterer leerer Mandanten. Transaktion, aktuelle Rolle, CSRF, Idempotenz, Personenlimit, Audit und ausschließlich neue Geschäftsführungsmitgliedschaft sind implementiert. Der gezielte echte HTTP-/PostgreSQL-Test benötigt noch den CI-Lauf am finalen Commit; responsive Chromium-Fixtures decken Bedienung und simulierten HTTP-Vertrag ab. Die vollständige Plattformverwaltung und reale Konto-/Datenübernahme bleiben offen.

**DP-00-02 und DP-11-01 bleiben T.** Das [Acht-Dateien-Inventar](evidence/attachment-inventory-2026-10-23-eight.json) bindet die vom Nutzer genannten Installer an beobachtete Größe, SHA-256 und gemeinsame PE-Version. Die [Funktionsmatrix](vendor-functional-reference.md) verwendet nur Herstellerinformationen für einen fachlichen Vergleich und nennt die nicht implementierten Kopplungen. Sie belegt weder den Funktionsumfang exakt dieser Builds noch ISO-/DIN-Konformität. Echte D1-/R2-Altbestände, vollständige Normtexte, Datenrechte und unabhängige Referenzfälle fehlen weiterhin.

## Dezimal-Wire-Dokument und Anfragekennung (23.10.2026)

`DP-02-02/DP-12-04/DP-02-07` erhalten [authentifizierte OpenAPI-Wire-Schemas](api-calculation-contracts.md) für Dezimalwerte und `requestId`, dazu die [Angular-Fehlerdiagnose](observability.md) mit begrenzter Kennung. TestServer prüft Zahl-/Stringeingaben, Stringausgaben, Nullwerte, Listen, Dictionaries, Matrizen und wiederverwendete DTOs. Ein echter PostgreSQL-/Identity-Fall prüft Operationsmenge, Referenzen, sechs Abrufe pro Person/Minute und die Dokumentgröße **nach** Erzeugung auf höchstens 8 MiB. Die Größe ist eine CI-Prüfung, keine Laufzeitgrenze für Antwortbytes oder CPU-Zeit. Die Laufzeit begrenzt zusätzlich auf zwei gleichzeitige Dokumenterzeugungen ohne Warteschlange. Bis zum finalen CI-Lauf sind dies Quell- und lokale Testbelege. Alle drei Aufgaben bleiben T; 49 K / 49 T / 2 O bleibt unverändert.

## HTTPS-Hostingvoraussetzung DP-02-07 / DP-13-05 (23.10.2026)

Das [explizite Proxyhosting](proxy-hosting.md) ergänzt einzeln gewählte Proxyadressen, genau einen symmetrischen Forwarded-Header-Hop und echte Middleware-Negativfälle für unbekannte Gegenstellen, fehlende Headerpaare und gefälschte Host-/Präfixwerte. Ein ausdrücklich gewählter verifizierter HTTPS-Ursprung erweitert den vorhandenen synthetischen Containerablauf. Beide Aufgaben bleiben T; externe Grundlagen und reale Umschaltung bleiben offen. Lokale Proxy-Regression: 13 bestanden, 0 übersprungen; gesamthafte Linux-/PostgreSQL-/Containerprüfung folgt am finalen Commit.

## Fachliche P0-Gates (23.10.2026)

Die [synthetischen unabhängigen Zahlenorakel](evidence/domain-acceptance-cases.md) prüfen ausdrücklich begrenzte Dachlast-, Entwässerungs-, Wärmedurchgangs- und Rechnungsketten, einschließlich einer Gutschrift und ihrer Cent-Gesamtbilanz. Die Suite hat einen separat sichtbaren CI-Schritt im Serverjob. Ergebnisse gelten nur für den Commit mit erfolgreich ausgeführtem Workflow; es findet **keine** neue Klassifikation der 100 Aufgaben oder fachliche Freigabe statt.

## Technische API-Verträge und echte Dezimal-Ausgaben (23.10.2026)

**DP-02-02/DP-12-04 bleiben T.** [18 technische Rechenvorschauen](api-calculation-contracts.md) erhalten explizite OpenAPI-Ergebnis-/Fehlerschemas und einen HTTP-geprüften versionierten Mindestvertrag. Fehlerhafte Minimal-API-Bindung verwendet jetzt auch außerhalb der Entwicklung den vorhandenen generischen Problem-Details-Pfad. Dies ersetzt keine vollständige API-Kompatibilitätsprüfung oder Webhookzustellung.

Der zusätzliche echte HTTP-/Projektablagenvergleich für sechs synthetische Fachfälle sichert **DP-11-04/05** gegen eine zuvor in reinen Core-Fixtures unsichtbare Grenze: Vorschau-Geldwerte werden als Dezimalstrings übertragen, eingebettete technische Ergebnis-JSON kann sie als Zahlen enthalten. Ausschließlich diese benannten Geldfelder werden centgenau normalisiert; Maße und Quellen bleiben streng gebunden. Alle vier Aufgaben bleiben Teilumfänge. Endgültige CI-/Operations-/Standards-Ergebnisse sind in [PR #20](https://github.com/Cenn0/FirstBlick/pull/20) nachzuvollziehen.

## Erweiterte Fachmasken und fixierte 46er-Arbeitsmenge (23.10.2026)

**DP-11-04/05 bleiben T.** [Die erweiterten Clientverträge](advanced-technical-client.md) ergänzen Mengen-/Wasserbilanzen, Eingabe-/Quellenbindung, unveränderte Vorschau bei Projektablage, Grenzflächenhöchstmengen und eigenständige UI-Regressionen. Sie sind keine normative oder Herstellerabnahme. Die [fixierten ursprünglichen 46 Teilaufgaben](roadmap-closeout.md) erhalten ihren Nenner und benennen Software-Restarbeit sowie benötigte externe Grundlagen. Die aktuelle Zählung bleibt **49 K / 49 T / 2 O**; die ursprünglichen Abnahmekriterien werden nicht abgehakt. Tatsächliche Laufnachweise stehen am endgültigen Commit in [PR #20](https://github.com/Cenn0/FirstBlick/pull/20).

## Bedeutung der Statusangaben

| Status | Bedeutung |
|---|---|
| **K – Kern belegt** | Der in der Zeile präzise genannte technische Kern ist implementiert und durch Quell-/Testbelege gestützt. Der Rest in der rechten Spalte bleibt offen. Das bedeutet ausdrücklich keine vollständige ursprüngliche Fach- oder Produktivabnahme. |
| **T – Teilumfang** | Es gibt eine funktionierende Grundlage oder begrenzte Nachweise; wesentliche Funktionen, Integration oder der passende Gesamtnachweis fehlen noch. |
| **O – Offen** | Für die verlangte Fachfunktion ist keine akzeptierbare Implementierung vorhanden. Beschreibende Felder, Schnittstellennamen oder Ablehnung unsupported Inputs zählen nicht als diese Funktion. |

| Inventarstand | K | T | O | Aussage |
|---|---:|---:|---:|---|
| Historische Klassifikation vom 20.10.2026 | 49 | 41 | 10 | Als historische Metadaten erhalten; keine aktuelle Gesamtbewertung. |
| Inventar nach begrenzter Neubewertung vom 21.10.2026 | 49 | 45 | 6 | Acht benannte Zeilen aktualisiert; 92 historische Zeilen übernommen. |
| Inventar nach PV-Teilumfang vom 22.10.2026 | 49 | 46 | 5 | Sechs Aufgaben mit begrenzten Teilfunktionen ergänzt; DP-11-09 O → T. |
| Inventar nach Kontakt-/Aufgaben-/Berichtsfortschreibung vom 22.10.2026 | 49 | 46 | 5 | Sieben Aufgaben um fünf Teilfunktionen ergänzt; ihre Kategorien bleiben unverändert. |
| Aktuell nach Wind-/Schnee-/Abflussteilumfang vom 22.10.2026 | 49 | 49 | 2 | DP-11-06, DP-11-07 und DP-11-08 wechseln O → T; keine vollständigen Fachnachweise. |

Gegenüber der historischen JSON-Inventur werden **DP-08-03, DP-11-04, DP-11-05 und DP-12-02 von O auf T** berichtigt, weil jeweils begrenzte Fachfunktionen implementiert sind. DP-11-05 war in der Markdown-Fassung bereits als T geführt; diese Abweichung ist jetzt aufgelöst. Die vier anderen neu bewerteten Zeilen bleiben T. DP-11-09 wechselt wegen einer neuen geometrischen Teilfunktion ebenfalls von O zu T. Die aktuelle Zählung **49 K / 49 T / 2 O** beschreibt nur die jetzt gelisteten Statuswerte und ist **kein Fertigstellungsprozentsatz, keine erneute Bewertung aller 100 Aufgaben und keine vollständige Abnahme**. Kein Eintrag behauptet eine normative Zertifizierung, einen Produktionswechsel oder eine externe Anbieterabnahme. Die JSON-Fassung bewahrt die ursprüngliche Zählung und ihren Zeitstempel unter `historicalClassification`; `counts` entspricht den aktuellen Tabellenzeilen.

## Aktuelle Fortsetzung – vorgegebene Wind-, Schnee- und Abflussfälle (22.10.2026)

- **DP-11-06:** [Lokale Winddruckfälle](roof-actions.md) führen getrennte äußere/innere Bezugsdrücke und vorgegebene Beiwerte zu richtungsgebundenem Nettodruck, Sog und lokaler Normalkraft. Standort-/Zonenwahl, dynamische Antwort, Lastkombinationen, Befestiger und Standsicherheitsprüfung bleiben offen.
- **DP-11-07:** [Entwässerungsbilanzen](roof-actions.md) rechnen 1 bis 32 unabhängige Einzugsflächen mit belegten Kapazitäten, getrenntem Berechnungs-/Starkregen und ausdrücklich gewähltem Restabfluss- oder Hauptausfallszenario. Hydraulische Dimensionierung, Wasserstände, Fließweg-/Rückstauprüfung und Auswahl des anzuwendenden Regelwerks bleiben offen.
- **DP-11-08:** [Vorgegebene Schneelastfälle](roof-actions.md) rechnen explizite Form-/Expositions-/Temperaturbeiwerte und Bodenschneelast auf horizontalen Zonen eines gleichzeitigen Falls. Verwehung, Standort-/Dachformwahl, Kombinationen und tragende Konstruktion bleiben offen.

Die gemeinsame Angular-Maske bietet Quellen, bestätigte Eingaben, geprüfte Zahlen, JSON-Export und bewusste Projektablage; bestehende technische Originalakten bieten PDF-Erstellung. Core-/Client-, echte HTTP-/PostgreSQL- und Browser-Vertragstests sowie der tatsächliche API-/S3-/PDF-/Neustartablauf sind verlinkt. Deren erfolgreiche Ausführung muss am final veröffentlichten Commit belegt werden.

Die drei Aufgaben wechseln **O → T**. Aktuelle Kategorien: **49 K / 49 T / 2 O**, keine Fertigstellungsquote. DP-11-10 und DP-14-03 bleiben O. Die übrigen 97 Kategorien und alle ursprünglichen Abnahmekriterien bleiben erhalten. **Alle 100 Aufgaben sind weiterhin nicht vollständig umgesetzt oder abgenommen.** Reale Altquellen, Providerzugänge, Norm-/Herstellerdaten und unabhängige fachliche Prüfung sind nicht durch Softwaretests ersetzbar.

## Aktuelle Fortsetzung – Kontaktverlauf, Aufgabenfolge, Wartungsarbeitstage und Berichte (22.10.2026)

- **DP-04-01:** Die [Kontaktwerte-Historie][contact-history-docs] bewahrt frühere und neue Ansprechpartnerwerte, Aktion, Kundenrevision und damaligen Akteursnamen. Sie ergänzt die bestehende Kontaktbedienung um paginierte, auch archiviert lesbare Originale; Bestandskontakte erhalten keine erfundene Vorgeschichte.
- **DP-04-03 / DP-04-04:** [Aufgabenabhängigkeiten][task-dependencies-docs] bieten explizite Vorgänger, Blockeranzeige und Statusaktionen. Projekt-/Mandantenbindung, Zyklen, Start-/Abschlussbedingungen sowie Rücknahme- und Archivierungsschutz gelten auf dem Server einschließlich generischer Aufgabenänderungen und konkurrierender Kommandos.
- **DP-09-02 / DP-09-06:** [Wartungsarbeitstage][maintenance-workdays-docs] trennen unveränderte vertragliche Fälligkeit und ausdrücklich geprüften Ausführungstag. Wochentage, Sperrtage, Vertragsende, gespeicherte Regelkopie und automatische Planung verwenden denselben begrenzten Vertrag.
- **DP-14-01:** Die [Projekt- und Aufgabenstatistik][business-activity-docs] definiert drei Kennzahlen aus Anlagedaten und aktuell gespeicherten Fälligkeiten, mit gleich langem Vorzeitraum, Monatsabschnitten, Betriebszeitzone und nachvollziehbaren Quellen. Historische Abschluss- oder Rückstandsverläufe werden nicht behauptet.
- **DP-14-02:** [CSV-/JSON-Berichte][business-activity-docs] verwenden dieselben Zeitraum-, Projekt- und Archivfilter wie die geprüfte Ansicht. Quellenrevisionen, Berichtsprüfsumme, Betrieb und Dateihash werden geprüft; geänderte Quellen verlangen eine neue Ansicht. CSV enthält Quellen, JSON zusätzlich Summen und Monatsabschnitte.

[Kontaktvarianten][contact-history-migration], [Aufgabenvarianten][task-dependencies-migration] und [Arbeitstagsvarianten][maintenance-workdays-migration] ergänzen den kanonischen Migrationsdrill. Der [gemeinsame API-/Neustartablauf][roadmap-activity-smoke] prüft die fünf Abläufe und vergleicht nach Neustart Kontaktoriginale, Graph, Ausführungsplan, Berichtsquellen und stabile CSV-Bytes. Core-/Clientfälle, echte HTTP-/Identity-/PostgreSQL-Fälle, Browser-Fixtures mit kontrollierten Antworten sowie Container-/Migrationsprüfungen sind verschiedene Prüfpfade. Ihre Existenz belegt keinen erfolgreichen Lauf; Ausführungsergebnisse gehören zum selben final veröffentlichten Commit und werden nicht zu historischen Testzahlen addiert.

**DP-04-04 bleibt K, die sechs anderen genannten Aufgaben bleiben T; 49 K / 46 T / 5 O bleibt unverändert.** Die übrigen 93 Zeilen werden nicht neu bewertet. Dies ist keine Gesamtabnahme der ursprünglichen Anforderungen. Die folgenden Fortschreibungen bleiben als Beschreibung ihres damaligen Stands erhalten.

## PV-Dachgruppen – weiterer begrenzter Teilumfang DP-11-09 (22.10.2026)

- [Mehrere unabhängig gemessene Dachflächen](pv-roof-set.md): gemeinsamer Rechenlauf mit überprüfbaren Einzel-/Gesamtsummen, separaten Hindernissen, Rechte-/Projektablage und Clientprüfungen. Das frühere Einzelraster bleibt unverändert; fachliche Begrenzungen werden nicht aufgehoben.
- **Status T unverändert**, **49 K / 46 T / 5 O**. Die Matrix wird nur für diese Zeile fortgeschrieben, nicht für die anderen 99 Aufgaben. Neu geschriebene Tests zählen erst nach erfolgreicher Ausführung auf dem jeweiligen Commit.

## PV-Geometrie – begrenzter Teilumfang DP-11-09 (22.10.2026)

- [Geometrieentwurf und Grenzen](pv-module-layout.md): deterministische Modulpositionen auf einer rechteckigen gemessenen Dachfläche, Nennleistung statt Ertragsprognose, lokale UI-Eingabe und Export, technischer Projekt-Originalstand über die vorhandene Ergebnisakte.
- Fehlende Norm-/Montage-/Elektro-/Lastnachweise bleiben ausdrücklich offen. CI-Ergebnisse zählen nur für den geprüften Commit.

## Aktuelle Fortsetzung – Projektvorlagen und Originalbelege (22.10.2026)

- **DP-04-03 / DP-04-04:** [Projektvorlagen][project-templates-docs] mit geordneten Aufgaben, relativen Kalendertagen, konkreter revisionsgebundener Vorschau und atomarer Übernahme. Unveränderliche Anwendungen bewahren Quellen und Aufgabenherkunft, auch wenn Aufgaben später bearbeitet oder Vorlagen archiviert werden.
- **DP-04-01 / DP-09-03 / DP-07-05:** [Originalbelege][ledger-originals-docs] an Gesprächen und Fahrzeugkosten mit mehreren Dateien, versioniertem Speicher, wiederaufnehmbarem Upload und verifiziertem Download. Quellrechte gelten auch für generische Dokumentrouten; Stornos erhalten vorhandene Dateien und sperren weitere Uploads.
- **Betriebsnachweise:** [Migrationsgraph][migration-graph-source], [Vorlagenvarianten][project-templates-migration], [Belegvarianten][ledger-originals-migration] und [API-/S3-/Neustartablauf][project-templates-originals-smoke] prüfen historische Quellen, bearbeitete Aufgaben, erhaltene Dateien und widersprüchliche Importdaten.

Die fünf Aufgaben behalten ihre bisherigen Einstufungen; DP-04-04 bleibt K, die übrigen vier T. Die Ergänzung ersetzt keine Neubewertung aller 100 ursprünglichen Kriterien. Erfolgreiche Tests und Betriebsprüfungen sind am veröffentlichten Commit zu belegen.

## Vorherige Fortsetzung – Kommunikationsakte, Fahrten und Fahrzeugkosten (22.10.2026)

- **DP-04-01 / DP-09-03:** [Manuelle Kommunikationsakte][customer-journal-docs] mit Kontakt-/Projektzuordnung, eingefrorenen Originalen und begründeten separaten Stornos. Historisch archivierte Kundenakten bleiben lesbar. Neue Einträge verwenden aktuelle Rechte und Kundenrevisionen; eine Erfassung versendet keine Nachricht.
- **DP-07-05:** [Fahrten und Fahrzeugkosten][vehicle-ledger-docs] mit vollständiger aktiver Kilometer-/Fahrerchronologie, belegreferenzierten EUR-Beträgen, separaten Stornos und Monatsübersicht. Die Auswertung bleibt eine betriebliche Aufzeichnung ohne steuerliche Fahrtenbuchzusage oder automatische Finanzbuchung.
- **Betriebsnachweise:** [Migrationsprüfung][migration-graph-source] und [Drill][migration-drill-source] erhalten historische Quellen, Personen und Originaldigests. Der [Container-/Neustartablauf][customer-vehicle-ledgers-smoke] erstellt die Daten über die tatsächliche API und vergleicht nach Neustart dieselben Originale, Korrekturen und wirksamen Summen.

Core-/Clienttests, echte HTTP-/Identity-/PostgreSQL-Fälle und Browser-Fixtures mit kontrollierten HTTP-Antworten sind getrennte Prüfarten. Die drei Aufgaben bleiben **T**; andere Statuswerte werden nicht neu bewertet. Erfolgreiche CI-/Standards-/Operations-Läufe müssen für denselben veröffentlichten Commit vorliegen. Die Quellverweise allein ersetzen diesen Ausführungsnachweis nicht.

## Historische Ergänzung – Ansprechpartner, Prüfungsoriginale und Kalenderdateien (21.10.2026)

- **DP-04-01:** [Mehrere Ansprechpartner][customer-contacts-docs] mit stabiler Identität, ausdrücklichem Hauptkontakt, Archivierung und Wiederherstellung; Kundenrevision, Rechte und bestehende Stammkontaktdaten bleiben Teil des Vertrags.
- **DP-07-05:** [Originaldateien abgeschlossener Geräteprüfungen][equipment-originals-docs] mit Prüfung von Dateityp, Länge, SHA-256 und konkreter Speicherversion, wiederaufnehmbarem Upload und unverändertem Abschlussdigest.
- **DP-09-02:** [ICS-Download eines gespeicherten Wartungstermins][maintenance-calendar-docs] als ganztägige Fälligkeit mit stabiler UID, Revision und minimiertem Inhalt; kein Providerkonto oder Versand erforderlich.

Diese drei Teilumfänge enthalten Core-/API-/Client-Regressionen und responsive Browser-Fixtures. Die API-Fälle verwenden echte HTTP-/Identity-/PostgreSQL-Abläufe; die Originaldatei-API-Tests verwenden einen deterministischen versionierten Objektspeicher als **Testdouble**. Browser-Fixtures verwenden kontrollierte HTTP-Antworten. Der in [container-smoke.py][container-smoke] eingebundene [neue Containercheck][contacts-calendar-originals-smoke] enthält die echte API-/S3-Kette und einen Vergleich von Kontaktidentitäten, Kalenderbytes, Prüfungsquellen und Originalversionen nach Neustart. [Migrationsprüfung][migration-graph-source] und [Migrationsdrill][migration-drill-source] ergänzen Kontakt- und Originalaktenfälle. **Diese Quellverweise behaupten keinen bereits bestandenen Container-, Migrations-, CI- oder Standards-Lauf des zusammengeführten Commits.** Dessen Ausführungsergebnisse sind gesondert zu belegen; ältere Checkpoints werden dafür nicht angerechnet.

Zusätzlich berichtigen fünf Zeilen schon zuvor implementierte, bisher unvollständig nachgeführte Funktionen: [E-Rechnungsvalidierung und Ein-/Ausgabe][e-invoice-docs] (**DP-08-03**), [inhomogene thermische Aufbauten][inhomogeneous-docs] (**DP-11-03**), [Tauwasser-/Austrocknung und mehrfeldrige Gefälleplattenplanung][advanced-exchange-docs] (**DP-11-04/05**) sowie [GAEB-Kette und Preisarbeitslisten][gaeb-worksheet-docs] (**DP-12-02**). Die neuen Einstufungen T halten deren technische Grenzen und fehlende externe Fachabnahmen ausdrücklich sichtbar.

FirstBlick ist das Entwicklungsziel. DachPlaner bleibt ausschließlich Referenz. Bei dieser damaligen Ergänzung wurde außer den acht genannten Aufgaben keine fachliche Neubewertung vorgenommen. Bereits vorhandene historische Markdown-Texte für DP-00-02, DP-02-06, DP-05-06 und DP-08-02 sowie sieben vorhandene Quellverweise wurden lediglich in die JSON-Fassung übernommen, um frühere Darstellungsabweichungen zu beseitigen; daraus folgt keine neue Einstufung oder Ausführungsevidenz.

## Historische Ergänzung – Geräteprüfungen und mehrstufige Lagerketten (21.10.2026)

Die Zeilen **DP-07-03, DP-07-05 und DP-07-06** enthalten den neuen [Geräteprüfungsablauf][equipment-docs], explizit [mehrstufige Lagerherkunft][transfer-chain] und deren Migrations-/Neustartprüfungen. Die maschinenlesbare Matrix führt dieselben Quell- und Testbelege. Die bisherige Begrenzung auf direkte Umlagerungen gilt nur noch für historische ungebundene Transferbelege. Neue beleggebundene Ketten unterstützen bis zu 32 Umlagerungen.

FirstBlick ist das Entwicklungsziel. DachPlaner bleibt ausschließlich Referenz. Diese Erweiterung stuft die 100 ursprünglichen Anforderungen nicht pauschal neu ein; insbesondere Fahrtenbuch, physische Chargenidentität und externe Lieferantenbelege bleiben offen.

## Historische Ergänzung – Wochenplanung, Rapporte und Wartungsfolge (21.10.2026)

Diese damalige Ergänzung aktualisierte **DP-06-01, DP-06-03, DP-07-03, DP-07-04, DP-07-06, DP-09-02, DP-09-06 und DP-10-03** um den [Wochenarbeitsplatz][planning-week-docs], die [geprüften Tagesrapporte][diary-docs], die [Lagerkorrekturen][warehouse-docs], den verifizierten Aufmaß-PDF-Download und die [Wartungsfolge][maintenance-docs]. Sie beschreiben jeweils den technischen Umfang und seine verbleibenden Grenzen. Projekt-Rücklieferungen aktualisieren jetzt auch die zugehörigen Reservierungszähler; deshalb ist **DP-07-04** ausdrücklich Bestandteil dieser Ergänzung. Lieferantenretouren nach einem Lagerwechsel waren in dieser damaligen Ergänzung auf eine nachgewiesene direkte Umlagerung begrenzt; die oben beschriebene spätere Lagerketten-Erweiterung erweitert diesen Umfang.

Die verlinkten Testdateien unterscheiden Core-/Clientprüfungen, echte HTTP-/Identity-/PostgreSQL-Fälle und responsive Browser-Fixtures. Die Browser-Fixtures verwenden kontrollierte HTTP-Antworten; die Rapport-API-Tests verwenden einen deterministischen, versionierten Objektspeicher als Testdouble. Diese Nachweise dürfen nicht als zusätzlicher echter Browser-/S3-Betriebsdurchlauf gezählt werden. Vorhandene Testfälle und getrennte lokale Ergebnisse behaupten keinen gemeinsamen erfolgreichen CI-/Standards-/Operations-Lauf des final zusammengeführten Commits. Dafür ist dessen eigener Ausführungsnachweis erforderlich; eine neue Gesamtzahl wird hier nicht gebildet.

## Historische gelesene Ausführungsnachweise

- [Checkpoint 11](checkpoint11.md): Neuere geprüfte Commits für den beleggebundenen Null-Euro-Mengenabschluss sowie mandantenbegrenzte, metadaten- und referenzgeprüfte Nachkalkulationsquellen, mit unmittelbar verlinkten CI-/Operations-Ergebnissen. Die dortigen neuen Testfälle werden nicht zu älteren Checkpoint-Zahlen addiert.
- [Checkpoint 9](evidence/checkpoint9.json) und [Einordnung](checkpoint9.md): 45 Unitfälle, 216 unterschiedliche Clientfixtures sowie 30 unterschiedliche echte Browserkombinationen auf zehn Abläufen × drei Viewports. Die getrennten Ausführungen, zwei abschließenden UI-Anpassungen, zusätzliche Collections-Prüfung, Produktions-CSP-Korrektur und früheren Fehlversuche sind ausdrücklich dokumentiert.
- [GitHub-Prüfungen von Checkpoint 8](evidence/checkpoint8-ci.json): CI und Operations für Commit `2ac13105c78d077101350fc61f05c541a3728506` sind vollständig erfolgreich; einschließlich tatsächlichem Containerstart, Backup/Restore, Migration und Neustart. Spätere Änderungen besitzen gesonderte CI-/Operations-Nachweise (siehe [Checkpoint 11](checkpoint11.md)); Prüfungen älterer Commits gelten nicht automatisch für neuere.
- [Backend-Checkpoint 8](evidence/checkpoint8-backend.json): aktueller einzelner vollständiger Lauf mit **251 Core- und 126 echten API-/PostgreSQL-/Worker-Fällen**, null Fehlern und null übersprungenen Fällen. Enthalten sind kumulative Rechnungsarithmetik, Forderungsbearbeitung und Betriebsmetriken. Neue UI-/Live-Läufe werden separat festgehalten.
- [Veröffentlichter Checkpoint 6](evidence/checkpoint6.json): Commit `06b58921f206bd216f6c23408b735da091ef1e8e` ist mit 190 Core- und 78 echten API-/Worker-PG-Fällen belegt. Neuere Arbeitsverzeichnisfunktionen erhalten eigene Nachweise.
- [Backend-Checkpoint 7](evidence/checkpoint7.json): vollständiger Lauf mit 212 Core- und 98 echten API-/Worker-PG-Fällen; danach fünf finale Kosten-/Archivfälle mit zwei zusätzlichen Testidentitäten bestanden. Dies belegt 100 unterschiedliche API-Fälle über die benannten Läufe, keinen einzelnen 100er-Lauf. Die finalen Quell-SHA stehen im Bericht.
- [Discovery-Checkpoint 7](evidence/discovery-checkpoint7.json): 11 echte HTTP-/PG16.15-Fälle bestanden, keine übersprungen. Dazu gehören nach beobachtetem Lock-Warten entzogene Mitgliedschaft und die Ablehnung von 5.001 eigenen Datensätzen ohne Teilarchiv. Die Quell-SHA identifizieren den geprüften Arbeitsstand.
- [Discovery-Browsernachweis](evidence/live-discovery-checkpoint7.json): 18 kontrollierte Browser-Fixtures und drei echte HTTP-/PG-Browserabläufe jeweils auf Desktop/Tablet/Mobil bestanden. Echte Suche, generischer Projektverlauf und eigener ZIP-Download mit unabhängig geprüften Manifest-SHA sind getrennt von den simulierten 403-/Race-/Hashfehlerfällen dokumentiert.
- [Backend-Checkpoint 4](evidence/backend-checkpoint-4.json): 139 Core- und 60 API-/Worker-Fälle bestanden. Die API-/Worker-Fälle verwenden echtes PostgreSQL; Dokumentkorruption wird mit kontrollierter Speicher-Fehlerinjektion geprüft. Das ist ein damaliger Quellstand, keine neue Gesamtausführung nach allen folgenden Änderungen.
- [Integrations-Checkpoint](evidence/integrations-checkpoint.json): 51 fokussierte Core- und 7 echte HTTP-/PostgreSQL-Fälle bestanden, keine übersprungen. Der Bericht enthält SHA-256 der dazu geprüften Adapter-/Testdateien.
- [Sanierungsbrowser-Checkpoint](evidence/live-workflow-checkpoint4.json): 25 Unit- und 33 Browser-Fixture-Fälle sowie drei tatsächliche HTTP-/PostgreSQL-/S3-Sanierungsabläufe, je Desktop/Tablet/Mobil. Fixture-Tests benutzen API-Interception; die drei Live-Fälle benutzen sie ausdrücklich nicht.
- [Portalbrowser-Checkpoint](evidence/live-portal-checkpoint5.json): 28 Unit- und 45 Browser-Fixture-Fälle sowie drei tatsächliche persönliche Portalabläufe, je Desktop/Tablet/Mobil. Annahme/Feedback/Download/Widerruf sind echte Serveraktionen mit synthetischen Daten.
- [Offline-Checkpoint 7](evidence/live-offline-checkpoint7.json): drei echte Desktop-/Tablet-/Mobil-Arbeitstage mit produktivem Angular-Service-Worker, deaktiviertem Browsernetz, vollständigem Chromium-Neustart und persönlicher Wiederanmeldung bestanden. Der Bericht trennt 39 Unit-Fälle und frühere bzw. zusätzliche Browser-Fixture-Läufe ausdrücklich.
- [Kanonischer Migrationsdrill](evidence/migration-drill.json): zwei Mandanten, 19 Entitäten und zwei versionierte Dateien gegen echte PostgreSQL-/S3-Ziele geprüft; 11 fehlerhafte Eingangsvarianten abgewiesen und Wiederholung ohne Schreibvorgänge belegt.
- [Container-Restore](evidence/container-restore-drill.json): früherer Containerstand mit PostgreSQL 18.6, 20 Tabellen/36 Zeilen und zwei S3-Versionen bestanden; [Image-IDs](evidence/container-images.json) beschreiben die tatsächlich gebauten Artefakte. Neuere Migrationsänderungen sind darin noch nicht enthalten.
- [Restore-Drill](evidence/restore-drill.json): echtes PostgreSQL 16.15 mit SeaweedFS 4.48, 20 Tabellen/36 Zeilen und zwei referenzierte historische S3-Versionen. Korrupte Sicherung, falsches PFX-Passwort und vorhandenes Ziel wurden abgewiesen. Der neuere Lauf belegt außerdem ungültige alte Passwörter/Rollen, erhaltene anstehende Kontoübernahme, widerrufene Portalgrants, persistierte Recovery-Sperre und beleggebundenes Resume. Die geringe gemessene Laufzeit beschreibt nur diese synthetische Datenmenge.

Diese Zahlen gehören zu unterschiedlichen Quellständen und teilweise überlappenden Tests. Sie werden nicht zu einer erfundenen aktuellen Gesamttestzahl addiert. Die genannten abgeschlossenen Container-/Migrations-/Offline-Nachweise gelten nur für ihren jeweiligen Quellstand; weitere Änderungen benötigen neue passende Prüfungen. Der native Recovery-Drill ist separat belegt und keine automatische Aussage über den Containerlauf. Insbesondere belegt ein vorhandenes CI-YAML keinen erfolgreichen Lauf. Der finale veröffentlichte Commit muss mit seinen tatsächlichen CI-Ergebnissen verknüpft werden.

## Aufgabenmatrix


### DP-00 – Ausgangsbasis und Produktumfang (P0)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-00-01** Aktuellen Quellstand zusammenführen | **T** | [Baseline][baseline] hält den verglichenen Legacy-Commit und die unabhängige Neuimplementierung fest. | Gleichheit mit dem zuletzt gehosteten Altstand ist nicht nachgewiesen; kein realer Produktionsstand wurde zusammengeführt. |
| **DP-00-02** Daten und Dateien inventarisieren | **T** | [Baseline][baseline] und [Methodeninventar][methods] halten Quellgrenzen fest. Das [historische Sechserinventar][attachments-eight] bleibt an seinen damaligen Lauf gebunden; das [neue Acht-Dateien-Inventar](evidence/attachment-inventory-2026-10-23-eight.json) prüft jetzt alle vom Nutzer benannten Installer auf Größe, SHA-256 und gemeinsame PE-Produktmetadaten. Die [fachliche Herstellerreferenz](vendor-functional-reference.md) trennt veröffentlichte Produktfunktionen vom Inhalt exakt dieser Builds. | Produktive D1-/R2-Exporte, reale Datensatz-/Dokumentvollständigkeit und eine versionsgenaue fachliche Auswertung der Demo-Builds fehlen. Datei-Hash oder Produktname sind kein Methoden- oder Rechennachweis. |
| **DP-00-03** Verbindliche Anforderungsmatrix erstellen | **T** | [ROADMAP][roadmap] enthält alle 100 Kennungen; diese Matrix verknüpft sie mit Implementierung, Tests und Restlücken. | Verbindliche Bestätigung der individuellen Berichtskriterien und Prioritäten durch den Betrieb fehlt. |
| **DP-00-04** Drei reale Betriebsabläufe modellieren (Sanierung, Reparatur, Wartung) | **T** | [Baseline][baseline] beschreibt Sanierung/Reparatur/Wartung; [Core-Gesamtfall][operations-tests] und [echter Sanierungsbrowserlauf][live-renovation] decken einen synthetischen Sanierungspfad ab. | Drei reale Betriebsabläufe samt Ausnahmen wurden nicht fachlich abgenommen; Reparatur/Wartung sind nicht als vollständige echte Browserketten belegt. |
| **DP-00-05** Projektregeln und Entscheidungen dokumentieren | **K** | [Architekturentscheidungen][architecture] fixieren Stack, Mandanten, Offline, Audit und Nachweisgrenzen; [README][readme] legt Entwicklungsregeln offen. | Rechteinhaberentscheidung zur Open-Source-Lizenz, Betreiberverantwortung und externe Datenrechte bleiben offen. |

### DP-01 – Nachgewiesene Fehler und verlässliche Entwicklung (P0/P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-01-01** Reproduzierbare Ausgangsbasis und neue CI herstellen | **K** | [Server-/Web-CI][ci], SDK-/Paket-Locks und [Backend-Checkpoint 6][backend-evidence] belegen automatisierte Tests mit verpflichtendem PostgreSQL; der [Container-Restore][container-restore] ist für einen früheren Containerstand grün. | Der aktuelle finale Commit muss erneut durch alle Server-/Web- und Operations-Jobs laufen; die native kanonische Migrationsweiterentwicklung braucht ihren eigenen Container-Nachweis. |
| **DP-01-02** Aktivierung und Kontowiederherstellung trennen | **K** | [Auth][auth], [Worker][worker], [Securitytests][security-tests] und [Regressionstests][regression-tests] trennen Einladung, Recovery und Passwortreset; parallele Recovery-Anfragen erzeugen einen Job. | Konfigurierter echter E-Mail-Zustellweg und produktiver Kontowiederherstellungsprozess sind nicht ausgeführt. |
| **DP-01-03** Angebotsstatus beim Versand erhalten | **K** | [CommercialWorkflow][commercial] trennt QuoteStatus von DeliveryState; [Commercialtests][commercial-tests] und [Worker-PG-Tests][worker-tests] prüfen akzeptierte Angebote während Versand/Uncertainty. | Providerzustellung ist synthetisch/fault-injiziert geprüft; keine tatsächliche Nachricht wurde verschickt. |
| **DP-01-04** Effektive Kalkulationswerte einfrieren | **K** | [Kalkulation][commercial] friert effektive Material-/Lohn-/Mengen-/Steuerwerte und Verkaufsdarstellung ein; [PDF-HTTP-Tests][document-tests] prüfen spätere Stammdatenänderung und Digestbindung. | Betriebliche Kalkulations-/Steuerfälle müssen anhand eigener Referenzen abgenommen werden. |
| **DP-01-05** Eingabe- und Speicherbudgets abstimmen | **K** | [API-Grenzen][api-program], [EntityStore][store], [Dokumentgrenzen][documents] und [CSV-Profil][csv] begrenzen JSON, Snapshots, Dateien und Importmengen; [Securitytests][security-tests]/[CSV-Tests][csv-tests] prüfen Ablehnung. | Budgets sind technische Grenzen, keine repräsentative Last-/Speicherkostenmessung; große Fachdaten benötigen gesonderte Prüfung. |
| **DP-01-06** Unklare Versandvorgänge auflösbar machen | **K** | [Workerzustände][jobs], [Worker][worker], [Job-Klärungsbefehle][management] und [Crash-/Leasetests][worker-tests] machen mögliche externe Annahme dauerhaft Uncertain. | Die fachliche Klärung einer realen Providerannahme benötigt Providerbeleg und verantwortliche Person; kein automatisches Exactly-once-Versprechen. |
| **DP-01-07** Planungsinvarianten in beide Richtungen prüfen | **K** | [PlanningRules][planning] prüfen Abwesenheit/Zuordnung, Änderungen, Geräte und halb offene Zeitfenster; [Planungstests][planning-tests] testen beide Erstellungsreihenfolgen. | Praxisdaten, Fahrzeiten und betriebliche Sonderregeln sind nicht abgenommen. |
| **DP-01-08** Finale Sicherheitsheader prüfen | **K** | [Finale Header-Middleware][api-program] und [Antiforgery-/Headertest][security-tests] decken Erfolgs- und Fehlerantworten ab. | Produktive TLS-/Proxy-/Domain-Konfiguration und Browserpolitik am späteren öffentlichen Ziel bleiben zu prüfen. |

### DP-02 – Modularer Kern, Daten und Hintergrundarbeit (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-02-01** C#-/Angular-Serverbasis aufsetzen | **K** | [API-Start][api-program], [Angular-Routen][routes], Projektdateien und [echter Browserlauf][live-renovation] verbinden Angular 22 und ASP.NET Core 10 mit PostgreSQL/S3. | Die lokale Entwicklungsinstanz ist keine öffentliche Produktivbereitstellung. |
| **DP-02-02** C#-Fachmodule und OpenAPI-Vertrag einführen | **T** | [Core-Fachmodule][foundation] sind von HTTP/Persistenz getrennt; [API-Start][api-program] bietet authentifiziertes OpenAPI v1; [Adaptervertrag][integrations] dokumentiert Binär-/CSV-Semantik. [18 Rechenvorschauen][technical-contract-docs] ergänzen konkrete Ergebnis-/Problem-Schemas, stabile Operationsnamen und einen [versionierten Mindestvertrag][technical-openapi-minimum], der gegen das tatsächlich authentifiziert ausgelieferte [OpenAPI-Dokument geprüft wird][technical-openapi-tests]. Fehlerhafte JSON-/Medientypbindung verwendet den generischen Problem-Pfad auch außerhalb Development. | Vollständige maschinenlesbare Erfolgs-/Fehlerschemas und rückwärtskompatible OpenAPI-Vertragsprüfungen sind nicht für alle Endpunkte nachgewiesen. |
| **DP-02-03** PostgreSQL und EF-Core-Migrationen strukturieren | **K** | [DbContext][dbcontext], [Initialmigration][migration], tenantqualifizierte Fremdschlüssel und [Security-PG-Tests][security-tests] sichern Isolation auch unter Umgehung des EntityStore. | Große Upgrade-/Downgradepfade, Produktionsmigration und belastbare Optimierung des JSONB-Lesemodells bleiben offen. |
| **DP-02-04** .NET-Worker, Outbox und atomaren Audit aufbauen | **K** | [EntityStore][store], [CommandExecutor][api-infrastructure] und [Worker][worker] verbinden Mutation/Audit/Receipt/Outbox atomar; [Worker-PG-Tests][worker-tests] prüfen SKIP LOCKED, Token-CAS und Absturzfälle. | Audit ist Anwendungsevidenz, kein manipulationssicheres externes WORM-Archiv; nicht unterstützte Generierungsjobs schlagen ausdrücklich fehl. |
| **DP-02-05** C#-Dokumentendienst und Dateispeicher entwickeln | **K** | [Dokument-API][documents], [S3-Adapter][object-store], [PDF-Erzeugung][pdf], [Korruptionstests][document-tests] und [realer S3-Download][live-renovation] belegen Version/SHA/Größenbindung. | Produktive Speicherpolitik, Malware-Prüfung und professionelle Dokumentvorlagen sind nicht vollständig abgenommen. |
| **DP-02-06** API-Abfragen und Angular-Datenzugriff konsolidieren | **T** | [SQL-paginierte Suche][discovery-api], [zentrale Datensatzrechte][discovery-access] und [Angular-Kontextschutz][ui-nine] bündeln begrenzte, mandantengefilterte Lesezugriffe. Rechte-/Betriebswechsel entwerten alte Listen, Details und Downloads; die [aktuellen Fixture-/Live-Nachweise][checkpoint-nine] trennen Fehlerfälle und echte Datenzugriffe. [FinancialSources][financial-sources] begrenzt verknüpfte Rechnungs-/Korrekturhistorien mandanten- und auftragsbezogen; [Checkpoint 11][checkpoint-eleven] sichert Projektkosten-Quellmetadaten und Referenzen zusätzlich ab. [Quellenbudgets][bounded-sources] begrenzen EntityStore, Wartungsbeziehungen und Finanzquellen vor der JSON-Übertragung auf 10.000 Datensätze/12 MiB je Abfrage. Zahlungs-, Rücklastschrift- und Importpfade wählen explizite Beleg-/Bankbeziehungen oder Importschlüssel statt der gesamten Mandantenhistorie. PostgreSQL-Selektoren grenzen Mitarbeiter-, Ausbildungs-, Termin- und Projektbeziehungen vor Budgetierung ein; alle EntitySources-Aufrufe teilen ein DbContext-Budget. [Policy-/Budgetregressionen][policy-selection-tests] prüfen große fremde Mengen, Widerruf, Mandantengrenzen und kumulative Ablehnung. [Beziehungsgebundene Mutationsprüfungen][graph-source-tests] erhalten eigene Zeit-, Projekt-, Geräte-, Material- und Mängelinvarianten bei großen fremden Beständen. | Repräsentative Index-, Größen- und Latenzmessungen, ein vollständiger globaler Suchindex und gemeinsame Ressourcenbudgets für verbleibende direkte SQL-, Einzelbeleg-, Dokument- und Exportpfade bleiben offen. |
| **DP-02-07** Serverkonfiguration, Schlüssel und Beobachtbarkeit schaffen | **T** | [Konfiguration][service-registration], [Schlüssel][api-program] und [Betriebsbeobachtung][observability] ergänzen persistente Schlüssel um begrenzte Command-/Outbox-Metriken sowie einen mandantengebundenen Geschäftsführungs-Snapshot. [Vier Snapshot- und drei Metrikfälle][operations-api-tests] sind im [vollständigen Checkpoint-8-Lauf][backend-eight] enthalten. | Externer Metrikexport, verteiltes Tracing, Alarmzustellung, reale Secret-Rotation, repräsentative Last und vereinbarte Betreiber-SLOs bleiben offen. Der Snapshot behauptet keine Worker-/Providerverfügbarkeit. |

### DP-03 – Identität, Freigaben, Module und Bediengrundlage (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-03-01** ASP.NET-Identität und Betriebsmitgliedschaften trennen | **K** | [Identity-/Membership-Modelle][models], [Auth][auth], [Mandantenfilter][api-infrastructure] und [Securitytests][security-tests] trennen Person, bestätigtes Konto und aktive Betriebsmitgliedschaft. Die [zusätzliche Betriebsanlage](business-onboarding.md) ergänzt den ausdrücklichen Geschäftsführungsweg für neue leere Mandanten, mit transaktionaler Mitgliedschaft, Audit und Idempotenz; die neuen echten HTTP-/PostgreSQL-Fälle müssen am finalen Commit ausgeführt werden. | Produktive Kontoübernahme/Migration, eigenständige Plattformverwaltung und reale Betreiberabnahme bleiben offen; kein Kopieren alter Zugangsdaten wird als gelungen behauptet. |
| **DP-03-02** Funktionsrechte serverseitig ausdrücken | **K** | [AccessRules][access], [RecordPolicy][record-policy], [CommandExecutor][api-infrastructure] und [Security-/Replaytests][regression-tests] erzwingen Rolle, Mandant und aktuelle persönliche Sicht. | Neue Funktionen müssen diese serverseitigen Grenzen weiter explizit aufnehmen; Navigation allein ist kein Berechtigungsbeleg. |
| **DP-03-03** Mehrere Geschäftsführer und Delegation ermöglichen | **T** | [Mitgliederverwaltung][management] ermöglicht mehrere persönliche GF und Rollenänderungen; [Securitytests][security-tests] schützen den letzten aktiven GF. | Zeitlich begrenzte/feingranulare Vollmachten und betriebliche Delegationsabnahme sind nicht implementiert bzw. belegt. |
| **DP-03-04** Konfigurierbare Vieraugenfreigabe implementieren | **K** | [ApprovalService][approvals] bindet Aktion, Objekt, Revision, Inhalt, Policy, Antragsteller und zweiten aktiven GF; [Vieraugentest][security-tests] prüft veraltete/abweichende Vorschläge. | Unterstützt wird die dokumentierte Aktionsliste; ein alleiniger GF ist ausdrücklich ausgenommen. Beliebige Freigabeketten sind nicht implementiert. |
| **DP-03-05** Module pro Betrieb steuern | **K** | [Module und Abhängigkeiten][access], [Settings][management] und [Securitytests][security-tests] erlauben Historienlesen, sperren neue Aktionen und verbergen deaktivierte Kennzahlen. | Betriebliche Modulpakete/Preise/Lizenzabrechnung sind nicht Gegenstand dieser Implementierung. |
| **DP-03-06** Angular-Oberfläche mit Routen und Designsystem aufbauen | **K** | [Routen][routes], [Styles][styles], typisierte Seiten und [UI-Nachweise][live-portal] belegen responsive Bedienung auf drei Viewports. | Keine vollständige Barrierefreiheitszertifizierung oder reale Geräte-/Browserabdeckung aller Funktionen. |
| **DP-03-07** Berechtigungsmatrix als Prüfsuite verankern | **K** | [SecurityApiTests][security-tests], [Autorisierungsregressionen][regression-tests], [Export-Rollenmatrix][integration-tests] und [11 Discovery-PG-Fälle][discovery-evidence] prüfen direkte URLs, Tenant-FKs, persönliche Sicht und nach echtem Lock-Warten entzogene Mitgliedschaft. | Die Suite ist keine unabhängige vollständige Penetrationsprüfung; neue Funktionen brauchen passende Negativfälle. |
| **DP-03-08** Externe Partner gezielt beteiligen | **K** | [Portalmodell][portal-model], [Portal-API][portal-api], [Portal-PG-Tests][portal-tests] und [echter Portalbrowserlauf][live-portal] geben benannten Partnern einzelne Inhalte mit Ablauf/Widerruf frei. | Keine anonyme öffentliche Freigabe; tatsächliche Kundenorganisation und Praxisfreigabe fehlen. |

### DP-04 – Kunden, Gebäude, Dach und Projektakte (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-04-01** CRM und Ansprechpartner ausbauen | **T** | [Strukturierte Ansprechpartner][customer-contacts-docs], [Core-Regeln][customer-contacts-core], [HTTP-Kommandos][customer-contacts-api] und [Angular-Ansicht][customer-contacts-ui] führen bis zu 100 Kontakte je Kunde mit stabiler Kennung, ausdrücklich gewähltem Hauptkontakt, Archivierung und Wiederherstellung; bestehende Kontaktfelder bleiben erhalten. [Corefälle][customer-contacts-tests] und [HTTP-/Identity-/PostgreSQL-Fälle][customer-contacts-api-tests] prüfen Kunden-/Mandantenbindung, Revision, Replay und den Schutz vor generischem Überschreiben. [Clientverträge][customer-contacts-client], [Clienttests][customer-contacts-client-tests] und [responsive Browser-Fixtures][customer-contacts-browser] prüfen Antwortbindung, Änderungen und Kontextwechsel. [Migrationsprüfung][migration-graph-source], [Migrationsdrill][migration-drill-source] und [Container-/Neustartcheck][contacts-calendar-originals-smoke] enthalten Fälle zum Erhalt aktiver, primärer und archivierter Kontaktidentitäten. Die [Kommunikationsakte][customer-journal-docs], [Core][customer-journal-core], [API][customer-journal-api] und [Angular-Ansicht][customer-journal-ui] ergänzen manuelle Gesprächseinträge mit damaligen Kontakt-/Projektangaben und getrennten, originalgebundenen Stornierungen. [Coretests][customer-journal-tests], [HTTP-/PostgreSQL-Fälle][customer-journal-api-tests], [Clienttests][customer-journal-client-tests] und [Browser-Fixtures][customer-journal-browser] prüfen Originalerhalt, Zuordnung, Revision, Rechte, Zeitumstellung und Kontextwechsel. Der [Betriebs-/Neustartablauf][customer-vehicle-ledgers-smoke] und die Migrationsprüfung erhalten historische Quellen und Akteure. [Originalanhänge][ledger-originals-docs], [quellengebundene API][ledger-originals-api] und [gemeinsame Angular-Bedienung][ledger-originals-ui] ergänzen PDF/PNG/JPEG mit Speicherversion, Hash, Wiederaufnahme und weiter lesbaren Dateien nach Storno oder Archivierung. [HTTP-/PostgreSQL-Fälle][ledger-originals-api-tests], [Clienttests][ledger-originals-client-tests] und [Browser-Fixtures][ledger-originals-browser] prüfen Originalbindung, Integrität, Rollen und Kontextwechsel. [Migrationsvarianten][ledger-originals-migration] und [tatsächlicher API-/S3-/Neustartablauf][project-templates-originals-smoke] prüfen die erhaltenen Originaldateien. Die [Kontaktwerte-Historie][contact-history-docs] mit [eigenem Kern][contact-history-core] ergänzt die bestehenden Kontaktkommandos und die Angular-Ansicht um unveränderliche Vorher-/Nachherwerte, Hauptkontaktwechsel, Kundenrevision und damaligen Akteursnamen. [Corefälle][contact-history-tests], [HTTP-/Identity-/PostgreSQL-Fälle][contact-history-api-tests], [Clientfälle][contact-history-client-tests] und [Browser-Fixtures][contact-history-browser] prüfen Originalerhalt, Replay, Pagination, archivierte Quellen und Kontextwechsel. [Migrationsvarianten][contact-history-migration] prüfen durchgehende Kontaktfolgen; der [API-/Neustartablauf][roadmap-activity-smoke] vergleicht erhaltene Historien. | Automatische Mail-/Chatübernahme, vollständige Feldhistorien allgemeiner Kundenstammdaten, automatische Dublettenbereinigung, bewusste Kundenzusammenführung und externe Kontaktsynchronisierung fehlen. Der neue Werteverlauf umfasst strukturierte Ansprechpartner ab der ersten aufgezeichneten Änderung; frühere Änderungen werden nicht rekonstruiert. Manuelle Gesprächseinträge sind keine Versand-/Zustellbelege. Reale CRM-Abläufe sind nicht fachlich abgenommen. |
| **DP-04-02** Gebäude und dauerhafte Dachakte einführen | **K** | [Building/Project][projects], [RoofRevision][roof-model], [Roof-/Aufmaßtests][roof-tests] und [Sanierungsnachweis][live-renovation] verbinden dauerhaftes Gebäude mit unveränderlichen Dachversionen. | Reale Bestandsakten und Reparatur/Wartung über mehrere Projekte wurden nicht durchgängig abgenommen. |
| **DP-04-03** Projekte und Arbeitsstruktur modellieren | **T** | [Project/TaskItem][projects], Graphregeln in [Business API][business-api] und [echter Ablauf][live-renovation] bilden Projektbezüge, Typen/Status und Aufgaben ab. [Projektvorlagen][project-templates-docs], [Core][project-templates-core] und [API][project-templates-api] liefern geordnete Aufgaben mit relativen Kalendertagen, revisionsgebundener Vorschau, bewusster atomarer Übernahme und unveränderlichem Anwendungsnachweis. [Corefälle][project-templates-tests], [HTTP-/PostgreSQL-Fälle][project-templates-api-tests], [Clienttests][project-templates-client-tests] und [responsive Browser-Fixtures][project-templates-browser] prüfen Grenzen, Mehrfachübernahme, Herkunft und Rechte. Der [Migrationsdrill][project-templates-migration] und [Container-/Neustartablauf][project-templates-originals-smoke] erhalten auch später bearbeitete Aufgaben und archivierte Vorlagen. [Aufgabenabhängigkeiten][task-dependencies-docs], [Core-Regeln][task-dependencies-core], [eigene HTTP-Kommandos][task-dependencies-api] und [Angular-Arbeitsbereich][task-dependencies-ui] ergänzen bis zu 16 unmittelbare Vorgänger je Aufgabe und höchstens 1.000 Aufgaben je Projektgraph. Selbstbezüge, fremde oder archivierte Vorgänger, Kreise, verfrühter Start/Abschluss und ungültige Rücknahmen werden auch bei generischer Aufgabenbearbeitung und konkurrierenden Kommandos verhindert. [Corefälle][task-dependencies-tests], [HTTP-/Identity-/PostgreSQL-Fälle][task-dependencies-api-tests], [Clientvertrag][task-dependencies-client], [Clientfälle][task-dependencies-client-tests] und [Browser-Fixtures][task-dependencies-browser] prüfen Graph, Revision, Replay, Rechte nach Lock-Warten und Kontextwechsel. [Migrationsvarianten][task-dependencies-migration] und [API-/Neustartablauf][roadmap-activity-smoke] prüfen erhaltene Kanten und Status. | Mehrstufige Arbeitspakete, allgemeine Projektarbeitskalender, zeitliche Vor-/Nachläufe, kritischer Pfad und eine umfassende betrieblich bestätigte Projektorganisation fehlen. Abhängigkeiten planen Termine oder Ressourcen nicht automatisch um. Vorlagen erzeugen offene Aufgaben ohne Personenzuweisung, keine Einsätze, Materialien oder kaufmännischen Aufträge. |
| **DP-04-04** Aufgaben, Fristen und Verlauf integrieren | **K** | [TaskItem][projects], Fristen im [Dashboard][business-api], [Projektverlauf][discovery-api] und [11 echte HTTP-/PG-Tests][discovery-evidence] verbinden Aufgaben mit aktuell zugänglichen Projekt-Ereignissen; [18 Fixtures und 3 echte Browserabläufe][discovery-live] prüfen die [Bedienoberfläche][discovery-ui-docs]. [Projektvorlagen][project-templates-docs], [Core][project-templates-core] und [API][project-templates-api] liefern geordnete Aufgaben mit relativen Kalendertagen, revisionsgebundener Vorschau, bewusster atomarer Übernahme und unveränderlichem Anwendungsnachweis. [Corefälle][project-templates-tests], [HTTP-/PostgreSQL-Fälle][project-templates-api-tests], [Clienttests][project-templates-client-tests] und [responsive Browser-Fixtures][project-templates-browser] prüfen Grenzen, Mehrfachübernahme, Herkunft und Rechte. Der [Migrationsdrill][project-templates-migration] und [Container-/Neustartablauf][project-templates-originals-smoke] erhalten auch später bearbeitete Aufgaben und archivierte Vorlagen. [Aufgabenabhängigkeiten][task-dependencies-docs], [Core-Regeln][task-dependencies-core], [eigene HTTP-Kommandos][task-dependencies-api] und [Angular-Arbeitsbereich][task-dependencies-ui] ergänzen bis zu 16 unmittelbare Vorgänger je Aufgabe und höchstens 1.000 Aufgaben je Projektgraph. Selbstbezüge, fremde oder archivierte Vorgänger, Kreise, verfrühter Start/Abschluss und ungültige Rücknahmen werden auch bei generischer Aufgabenbearbeitung und konkurrierenden Kommandos verhindert. [Corefälle][task-dependencies-tests], [HTTP-/Identity-/PostgreSQL-Fälle][task-dependencies-api-tests], [Clientvertrag][task-dependencies-client], [Clientfälle][task-dependencies-client-tests] und [Browser-Fixtures][task-dependencies-browser] prüfen Graph, Revision, Replay, Rechte nach Lock-Warten und Kontextwechsel. [Migrationsvarianten][task-dependencies-migration] und [API-/Neustartablauf][roadmap-activity-smoke] prüfen erhaltene Kanten und Status. | Historische Projektverknüpfungen werden nicht rekonstruiert; automatische Erinnerungen, Termin-/Ressourcenplanung, Vor-/Nachlaufzeiten und umfassende betriebliche Abnahmeabläufe fehlen. Relative Vorlagenfristen zählen Kalendertage ohne Feiertags-/Arbeitszeitbewertung. Explizite Aufgabenabhängigkeiten ersetzen diese fehlenden Funktionen nicht. |
| **DP-04-05** Allgemeine Projektunterlagen bereitstellen | **K** | [Dokument-API][documents] unterstützt Projektdateien mit opaque S3-Key, Version und SHA; [Dokumenttests][document-tests] und [Upload-UI-Tests][browser-tests] prüfen Originalbytes und Zugriff. | Keine Dateikonvertierung, kooperative Planbearbeitung oder vollständige Schadsoftwareprüfung. |
| **DP-04-06** Übergreifende Suche und Archivierung ergänzen | **K** | [Übergreifende Suche][discovery-api], [Archivierung][business-api] und [PG-Tests][discovery-tests] belegen sichere Titel-/Nummernfelder und Pagination nach aktuellen Rechten; [Browsernachweis][discovery-live] prüft Bedienung, private Notiz-Nichttreffer und den konkreten Projektverweis. | Keine Inhalts-/OCR-Volltextsuche oder unveränderliche mehrseitige Suchmomentaufnahme; umfassende Aufbewahrungs-/Wiederherstellungsbedienung und betriebliche Fachabnahme bleiben offen. |

### DP-05 – Kalkulation, Angebot, Auftrag und Nachtrag (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-05-01** Kalkulations- und Leistungskatalog konsolidieren | **T** | [CatalogItem][projects] und [Pricing][commercial] verbinden Leistungsinputs mit einer einheitlichen Snapshotrechnung; [Kalkulationstests][commercial-tests] prüfen Rundung und Effektivwerte. | Externe Katalogversionen, Lieferantenpreise/Importmapping und fachliche Katalogpflege fehlen. |
| **DP-05-02** Angebots- und Vertragszustände trennen | **K** | [Quote/Order/Delivery][commercial], explizite [Kommandos][commercial-api] und [Commercial-/Worker-Tests][commercial-tests] trennen Entwurf, Ausgabe, Annahme, Vertrag und Versand. | Vertragliche Rechtswirksamkeit einer tatsächlichen Annahme ist nicht durch technische Statusprüfungen allein belegt. |
| **DP-05-03** Professionelle Angebotsbearbeitung ermöglichen | **T** | [Workflow-UI][workflow-ui], [frozen-party PDF-Tests][document-tests] und [echter Ablauf][live-renovation] erlauben Positionen, Ausgabe, Vorschau, Annahme und PDF. | Professionelle Layout-/Textbaustein-/Positionsbearbeitung und alle praxisüblichen Angebotsvarianten sind nicht vollständig umgesetzt. |
| **DP-05-04** Auftrag aus angenommener Version erzeugen | **K** | [CreateOrder][commercial], [Order-Kommando][commercial-api], [Parallel-/Snapshottests][commercial-api-tests] und [Live-Abfolge][live-renovation] erzeugen einen Auftrag aus genau der angenommenen Version. | Externe Vertragsabnahme und Sonderfälle außerhalb des festgelegten Zustandsmodells bleiben separat. |
| **DP-05-05** Nachträge und Leistungsänderungen führen | **K** | [ChangeOrder][commercial], [Annahme-Kommando][commercial-api] und [Rechnungsgrenztests][commercial-tests] frieren Nachträge ein und beziehen akzeptierte Änderungen in abrechenbare Mengen ein. | Komplette Änderungsverhandlung, Signatur und kundenseitige Nachtragsbedienung sind nicht als End-to-End-Ablauf belegt. |
| **DP-05-06** Soll, Ist und Prognose vergleichen | **K** | [Quellengebundene Nachkalkulation][cost-docs], [Core/API][cost-api] und [Bedienung][ui-nine] verbinden angenommene Vertragsquellen mit freigegebener Zeit, bewerteten Lagerbewegungen und explizit zugeordneten Eingangsbelegen. Drei echte Fälle im [Checkpoint 9][checkpoint-nine] prüfen 380 EUR Istkosten, 480 EUR Prognose, 520 EUR Überschuss und den originalgetreuen JSON-Bericht. [Checkpoint 10][checkpoint-ten] ergänzt [unveränderliche, verkettete Prognoseversionen][forecast-model], Digest-/Berechtigungs-/Mandantenprüfungen in [echten API-Tests][cost-api-tests] und [Desktop-/Tablet-/Mobil-Bedienung][cost-browser]. [Checkpoint 11][checkpoint-eleven] prüft zusätzlich gespeicherte Quelldaten gegen Metadaten/Referenzen. Das getrennte [Lieferantenkostenjournal][supplier-allocations] begrenzt ausdrücklich gebuchte Nettoanteile projektübergreifend. | Weder Forecasts noch Kostenjournal sind Finanzbuchungen oder eine vollständige betriebliche Kostenstellenrechnung. Manuelle ungebuchte Bewertungsvorschauen bleiben unverbindlich. Ohne belegte Vollständigkeit bleiben Ist/Prognose unbekannt; unabhängige Kosten-/Steuerabnahme und repräsentative Datenlast fehlen. |
| **DP-05-07** Kaufmännischen Referenzablauf absichern | **T** | [CommercialApiTests][commercial-api-tests], [LedgerTests][ledger-tests] und [echter Sanierungsbrowserlauf][live-renovation] prüfen wichtige kommerzielle Ketten und Fehlerfälle. | Kein realer, vollständig fachlich abgenommener Betriebsfall mit sämtlichen Planungs-, Material-, Abnahme- und Zahlungsvarianten. |

### DP-06 – Planung, Personal, Baustellenarbeit und Offline (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-06-01** Disposition mit Zeitfenstern und Ressourcen aufbauen | **K** | [Wochenarbeitsplatz][planning-week-docs], [Wochen-API][planning-week-api] und [Angular-Kalender][planning-week-ui] ergänzen Ressourcenfilter, Anlegen/Bearbeiten/Absagen und betriebliche Zeitzonen einschließlich Zeitumstellung. Die bestehenden [Planungsregeln][planning] prüfen Kollisionen, Abwesenheiten und Projektfenster. [Core-Tests][planning-week-core-tests], [HTTP-/PostgreSQL-Tests][planning-week-api-tests], [Clienttests][planning-week-client-tests] und [Browser-Fixtures][planning-week-browser] behandeln Grenzen, persönliche Sicht, Konflikte und verspätete Antworten. | Routenoptimierung, automatische Fahrzeit-/Tarifbewertung und ein realer Wochenplan-Abnahmefall bleiben offen. Die Projektion ist auf 1.000 aktive Einsätze begrenzt; Filter können den Umfang einschränken. Fehlende oder mehrdeutige Mitternachtsgrenzen werden ausdrücklich abgewiesen. |
| **DP-06-02** Zeiten, Pausen und Tätigkeiten erfassen | **K** | [TimeEntry][planning], [Feldkommandos][field-api], [Zeit-/DST-Tests][planning-tests] und [Lohnexporttests][payroll-tests] sichern UTC-Intervalle, Pausen und Freigabe. | Gesetzliche Arbeitszeitbewertung, Zuschläge und Lohnberechnung werden nicht behauptet. |
| **DP-06-03** Bautagebuch und Rapporte erstellen | **T** | [Tagesrapport-Ablauf][diary-docs], [eingefrorene Einreichung/Prüfung][diary-workflow] und [HTTP-Kommandos][diary-api] ergänzen das vorhandene offlinefähige Bautagebuch um unabhängige interne Freigabe, begründete Rückgabe, erneute Einreichung und mehrseitiges, quellengebundenes PDF. [Core-Tests][diary-core-tests], [HTTP-/PostgreSQL-Tests][diary-api-tests] und [Browser-Fixtures][diary-browser] prüfen Identität/Digest, Rollen und Replay, konkurrierende Prüfung, Downloadbytes und Kontextwechsel. | Ein tatsächlich im Betrieb abgenommener Baustellentag, Kundenabnahme, digitale Unterschrift und rechtssichere Langzeitarchivierung sind nicht nachgewiesen. Die interne Freigabe ersetzt diese Nachweise nicht; spätere Ergänzungen benötigen einen weiteren geprüften Bericht. |
| **DP-06-04** Abnahme, Mangel und Nacharbeit führen | **K** | [Defect/Acceptance][operations-model], [Feldkommandos][field-api] und [Abnahmetests][operations-tests] halten Mangelbehebung/-annahme und ausdrücklich offene Mängel fest. | Signaturen, Beweissicherung im realen Streitfall und Praxisabnahme sind nicht nachgewiesen. |
| **DP-06-05** Personal, Urlaub und Abwesenheit ergänzen | **T** | [Employee/Absence][planning], [Abwesenheitskommandos][field-api] und [Planungstests][planning-tests] bilden Personal, Anträge und Konflikte ab. | Urlaubskonten, Überträge, Feiertags-/Tarifregeln und produktive Personaldatenmigration fehlen. |
| **DP-06-06** Ausbildungsmodul umsetzen | **T** | [TrainingRecord][training], [Reviewkommandos][field-api], [RecordPolicy][record-policy] und [Form-/Trainingtests][operations-tests] binden aktive Auszubildende, Mentor und Wochenbericht. | Offizielles Berichtsheftformat, vollständiger Ausbildungs-/Schulablauf und tatsächliche Ausbildungsabnahme fehlen. |
| **DP-06-07** Angular-PWA und dauerhafte Offline-Synchronisierung bauen | **K** | [IndexedDB-Queue][offline], [Zugriffssignaturen][session-access] und [3 echte Offline-Arbeitstage][live-offline] belegen produktiven Shell-Service-Worker, kompletten Chromium-Neustart, erhaltene Mutation-Keys, persönliche Wiederanmeldung und genau einmalige Zeit-/Tagebuchübertragung. | Synthetischer Ablauf auf drei Chromium-Viewports; Hardwareverlust, Storage-Eviction, jede reale Geräteplattform und vollständige betriebliche Ausnahmeszenarien sind nicht nachgewiesen. |
| **DP-06-08** Mobile und Tablet-Abläufe praktisch prüfen | **K** | [Checkpoint 9][checkpoint-nine] belegt 30 unterschiedliche echte Browserkombinationen: zehn Abläufe jeweils Desktop/Tablet/Mobil, darunter Rechnungen, Kosten, Forderungen, Mengen, Exporte, Portal, Discovery, Sanierung, Operations und vollständiger Offline-Browserneustart. Der reale Produktionshost und sein CSS/CSP werden zusätzlich geprüft. | Viewports ersetzen keine reale Geräteflotte. Zusätzliche Fachrollen, reale Betriebsbedingungen und bisher nicht implementierte Roadmapmodule benötigen eigene Abnahmen. |
| **DP-06-09** Eigene Formulare konfigurieren | **K** | [FormDefinition/FormSubmission][training], [Feldkommandos][field-api], [Formtests][operations-tests] und [HTTP-Tests][commercial-api-tests] frieren Schemas und Antworten ein und verwerfen unbekannte/ungültige Felder. | Kein beliebiges Skripting, komplexe Verzweigungslogik oder kundenspezifischer Formularimport. |

### DP-07 – Einkauf, Lager, Material, Geräte und Fahrzeuge (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-07-01** Lieferanten und Einkaufsstammdaten modellieren | **K** | [Supplier/Material][inventory], generische [CRUD-API][business-api] und [Einkaufstests][planning-tests] bilden Lieferanten-/Materialbezüge mit validierten Einheiten ab. | Echte Stammdatenübernahme, externe Preislisten und Lieferantenabnahme fehlen. |
| **DP-07-02** Bedarf in geprüfte Bestellung überführen | **T** | [PurchaseOrder][inventory], [Issue-Kommando][field-api] und [ApprovalService][approvals] unterstützen prüfbare Bestellungen mit eingefrorenen Positionen. | Automatische Bedarfsableitung aus Aufmaß/LV, umfassende Freigabeketten und ein Anbieter-Bestelladapter fehlen. |
| **DP-07-03** Wareneingang und Bestandsbewegungen erfassen | **K** | [Lagerkorrekturen][warehouse-docs], [Bestandsregeln][inventory] und [Feldkommandos][field-api] verbinden Wareneingänge, Inventur, atomare Umlagerungspaare und originalgebundene Rückgaben. [Mehrstufige Herkunftsketten][transfer-chain] binden jede Umlagerung ausdrücklich an einen Wareneingang oder bereits gebundenen Transfer; sie prüfen bis zu 32 Schritte, Verzweigungen, belegte Restmengen, Wert, Zeitfolge und freie Lagerbestände. Die [Lagerprüfung][inventory-ui] zeigt Quelle, Pfad und Rest. [Core][transfer-chain-tests], [HTTP-/PostgreSQL][transfer-chain-api-tests] und [Browser-Fixtures][inventory-browser] prüfen neue und historische Verträge. Alte Bewegungsdigests bleiben unverändert. | Reale Inventur-/Mehrlager-Abnahmen, physische Chargen-/Serienzuordnung, weitergehende Bewertungsvorschriften und externe Retourenbelege bleiben offen. Die Mengen-/Bewertungskette identifiziert keine einzelne physische Liefereinheit. Historische ungebundene Transfers können nicht nachträglich als mehrstufige Herkunft ausgegeben werden. |
| **DP-07-04** Projektverbrauch und Reservierung verbinden | **K** | [Reserve/Consume/Release und ProjectReturn][inventory] binden die Baustellenrückgabe an Originalverbrauch und exakt passende Reservierung. In derselben Transaktion sinkt consumedQuantity und steigt releasedQuantity; der noch reservierte Rest bleibt unverändert. [Core-Regressionen][inventory-reconciliation-tests] und [konkurrierende HTTP-/PostgreSQL-Rückgaben][commercial-api-tests] prüfen atomare Zähler, Mengenrest und Replay. [QuantityEvidence/MaterialDemand][quantity-model] und [Mengentransfer-API][quantity-api] übernehmen weiterhin ausdrücklich geprüfte Bedarfe aus eingefrorenen Aufmaßen in einen Bestellentwurf. | Vollautomatische regelbasierte Materialdisposition einschließlich realer Packungsgrößen/Lieferantenangebote und repräsentative Mehrlagerpraxis fehlen. Eine bewusst freigegebene Aufmaßumrechnung ist kein allgemeines normatives Materialbedarfsverfahren. |
| **DP-07-05** Geräte und Fahrzeuge verwalten | **T** | Der [Geräteprüfungsarbeitsplatz][equipment-docs], [Core][equipment-workflow] und [API][equipment-api] verbinden geplante Prüfung/Wartung/Reparatur, unveränderliche Ergebnis-/Absagebelege, manuelle Folgetermine und Dispositionssperren. [Corefälle][equipment-tests] und [HTTP-/PostgreSQL-Fälle][equipment-api-tests] prüfen diese Regeln. Ergänzend binden [Originalakten][equipment-originals-docs], [Upload-API][equipment-originals-api] und [Angular-Bedienung][equipment-originals-ui] PDF/PNG/JPEG bis 25 MiB an einen abgeschlossenen Vorgang, dessen Digest und eine konkrete Speicherversion; Upload und Download prüfen die Originalbytes. [API-Regressionen][equipment-originals-api-tests], [Clientverträge][equipment-originals-client], [Clienttests][equipment-originals-client-tests] und [Browser-Fixtures][equipment-originals-browser] prüfen Quellenbindung, Hash, Wiederaufnahme, Rechte, Replay und Kontextwechsel. [Migrationsprüfung][migration-graph-source]/[Drill][migration-drill-source] enthalten verfügbare und archivierte Akten; [Container-/Neustartcheck][contacts-calendar-originals-smoke] enthält einen echten PNG-/S3-Rundlauf mit erhaltenen Bytes, Quellen und Versionen. Der [Fahrten-/Kostenarbeitsplatz][vehicle-ledger-docs] ergänzt [Originale und Stornos][vehicle-ledger-core], [API][vehicle-ledger-api] und [Angular-Bedienung][vehicle-ledger-ui] mit vollständiger aktiver Kilometer-/Fahrerchronologie, historischer Quellenbindung, EUR-Kosten und Monatsauswertung über alle Einträge. [Coretests][vehicle-ledger-tests], [HTTP-/PostgreSQL-Fälle][vehicle-ledger-api-tests], [Clienttests][vehicle-ledger-client-tests] und [Browser-Fixtures][vehicle-ledger-browser] prüfen Konflikte, exakte Dezimalwerte, Zeitumstellung, Rechte und Kontextwechsel. Der [Container-/Neustartcheck][customer-vehicle-ledgers-smoke] erhält Originale, Stornos und wirksame Summen; Migration und Restore prüfen auch archivierte Stammdaten. [Originalanhänge][ledger-originals-docs], [quellengebundene API][ledger-originals-api] und [gemeinsame Angular-Bedienung][ledger-originals-ui] ergänzen PDF/PNG/JPEG mit Speicherversion, Hash, Wiederaufnahme und weiter lesbaren Dateien nach Storno oder Archivierung. [HTTP-/PostgreSQL-Fälle][ledger-originals-api-tests], [Clienttests][ledger-originals-client-tests] und [Browser-Fixtures][ledger-originals-browser] prüfen Originalbindung, Integrität, Rollen und Kontextwechsel. [Migrationsvarianten][ledger-originals-migration] und [tatsächlicher API-/S3-/Neustartablauf][project-templates-originals-smoke] prüfen die erhaltenen Originaldateien. | Steuerliche Fahrtenbuchabnahme, Telematikübernahme, automatische Kosten-/Buchhaltungsintegration, automatisch ermittelte gesetzliche Fristen und unabhängige Kalender je Prüfart fehlen. Die Monatskennzahl bildet nur dokumentierte Kosten und Strecken ab. Prüfungsoriginale bestätigen keine inhaltliche Richtigkeit, digitale Unterschrift oder gesetzliche Prüfberechtigung. Vollständige Dateiinhalts-/Malware-Prüfung und automatische Bereinigung unbestätigter Speicherreste sind nicht enthalten; reale Betriebsfälle bleiben abzunehmen. |
| **DP-07-06** Einkaufsablauf und Differenzen absichern | **T** | [Lagerkorrekturen][warehouse-docs] und [Herkunftsketten][transfer-chain] begrenzen Retouren zugleich durch Originalbeleg, jeden zugeordneten Transferrest und freien Zielbestand. [HTTP-/PostgreSQL-Tests][transfer-chain-api-tests] prüfen konkurrierende Quellallokationen, atomare Ablehnung, aktuelle Rolle vor Replay, Audit und Mandantengrenzen. Lieferantenrückgaben rechnen den Bestellstatus zurück; [Containerablauf][equipment-inventory-smoke] und [Migrationsdrill][migration-drill-source] verbinden mehrstufige Lagerwechsel, Retoure und erhaltene Bestände. Der [aufmaßgebundene Einkaufsentwurf][quantity-docs] bleibt an den bestehenden Freigabeweg gebunden. | Lieferantengutschriften, Schadens-/Transportschadenabläufe, externe Bestell-/Retourenübermittlung und vollständige betriebliche Einkaufsabnahme fehlen. Die Lagerbuchung löst keine Zahlung, Gutschrift oder Lieferantenbenachrichtigung aus. Repräsentative Last großer Materialhistorien ist nicht nachgewiesen. |

### DP-08 – Rechnungen, offene Posten und lesendes Banking (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-08-01** Beleg- und Steuerregeln spezifizieren | **T** | [Money/Pricing][commercial], [Invoice][finance], [kumulative Rechnungsarithmetik][invoice-rounding] und [SupplierInvoice][supplier-finance] erzwingen Cent-, Positions- und Summenregeln. 21 neue Core-Fälle prüfen Originalpositionsrundung, tatsächliche Vorbeträge und Centkorrekturen. | Steuerfälle, Belegpflichtfelder und rechts-/anbieterbezogene Rechnungsprofile sind nicht vollständig spezifiziert oder extern abgenommen. |
| **DP-08-02** Ausgangsrechnungen und Korrekturen umsetzen | **K** | [InvoiceWorkflow][finance] verwendet für neue Belege [kumulative Originalpositionsbeträge und eingefrorene Herleitung][invoice-rounding]. Der ältere Checkpoint 8 belegt 21 Core-, fünf echte HTTP-/PG- und drei PDF-Fälle; [Checkpoint 11][checkpoint-eleven] ergänzt einen ausdrücklich begrenzten Null-Euro-Mengenabschluss mit eigenen Core-/API-Regressionsfällen und erfolgreichen Commit-bezogenen Prüfungen. [FinancialSources][financial-sources] lädt zugehörige Rechnungen und Korrekturen über begrenzte mandantenbezogene Links statt ganzer Mandantenhistorien. Legacy-Digests und Aufmaßherkunft bleiben prüfbar. | E-Rechnungsprofile, steuerliche/vertragliche Abnahme und repräsentative große Belegmengen sind offen; der Null-Euro-Mengenabschluss ist eine technische Sonderfallregel, keine normative Zusicherung. Andere Finanzendpunkte können weiterhin mandantenweite Historien laden. |
| **DP-08-03** Strukturierte E-Rechnungen verarbeiten | **T** | [E-Rechnungsablauf][e-invoice-docs], [versionsgebundener KoSIT-Validator][kosit-validator] und [v2-API][official-invoice-api] ergänzen die strukturelle v1-Vorschau um Original-XSD-/Regelprüfung und einen ausdrücklich bestätigten Import als Lieferantenrechnungsentwurf. Original, Prüfbericht und Freigabebeleg werden getrennt versioniert erhalten. [UBL-Ausgabe][e-invoice-output] erzeugt aus ausgestellten, eingefrorenen Rechnungen erneut validierte Ausgaben. [Originalregel-Fälle][official-invoice-acceptance-tests] und [HTTP-/PostgreSQL-Abläufe][official-invoice-workflow-tests] prüfen Quell-/Freigabedigest, Import, Archivierung, Rechte und Ausgabe; die [Prozessgrenzen][advanced-exchange-docs] trennen dies von Zahlung und Versand. | Vollständige EN-16931-Syntax-/Steuersonderfälle, beliebige ZUGFeRD-/Factur-X-PDF/A-3-Dateien, Peppol-Zustellung und DATEV-Verfahrensabnahme bleiben offen. Originalregelprüfung ersetzt keine steuerliche, rechtliche oder betriebliche Archivierungsfreigabe. Reale Geschäftsreferenzen und jede neue Validator-/Profilversion brauchen eigene Prüfung; Import und Export lösen keine Zahlung oder automatische Zustellung aus. |
| **DP-08-04** Offene Posten und Mahnwesen führen | **K** | [Forderungsfälle][collections-docs] verbinden offenen Arbeitsvorrat, geprüfte Salden, manuelle Stufen, Kontakte, Streit-/Pausenzustände und Wiederöffnung. 18 Core-/elf PG-Fälle, 27 Clientfixtures und drei echte 119→99-EUR-Vorschauabläufe sind in [Checkpoint 8][backend-eight] und [Checkpoint 9][checkpoint-nine] belegt. | Keine automatische Mahnzustellung, Gebühren-/Zinsberechnung, Rechtsprüfung oder historische as-of-Buchhaltung. Freigabe dokumentiert den internen Entwurf; ein tatsächlicher Kontakt wird separat mit Beleg erfasst. |
| **DP-08-05** Eingangsbelege prüfen und freigeben | **T** | [SupplierInvoice][supplier-finance], [Freigabekommandos][commercial-api] und [SupplierFinanceTests][supplier-tests] prüfen Eingangsrechnungen/-gutschriften und unabhängige Freigabe. | Vollständiger Beleg-/OCR-/Profilprüfungsprozess, echte Lieferantenbelege und betriebliche Fachabnahme fehlen. |
| **DP-08-06** Bankdaten ausschließlich lesend anbinden | **T** | [CSV-Bankadapter][bank-csv], [Integrations-API][integration-api] und [echte PG-Tests][integration-tests] importieren ausschließlich vorhandene Buchungen mit stabilen IDs und ohne Zahlungsauftrag. | Automatische lesende Anbindung an einen tatsächlichen Bankanbieter samt Vertrag/Tokenverwaltung ist nicht implementiert. |
| **DP-08-07** Zahlungsabgleich und Rücknahme implementieren | **K** | [PaymentWorkflow][finance], [PayablesWorkflow][supplier-finance], [Ledger-/Supplier-Tests][supplier-tests] und [PG-Konkurrenztests][commercial-api-tests] prüfen Teil-/Sammelzahlungen, Skonto, Einbehalt, Refund und Rücklastschrift. | Praktische Bankreferenzdaten und vollständige fachliche Freigabe sämtlicher Geschäftsfälle fehlen. |
| **DP-08-08** Finanzablauf und Exporte fachlich prüfen | **T** | [Finanzexport][ledger-export], kumulative Rechnungen/Korrekturen und saldengebundene Forderungen sind mit Core-/PG-Fällen sowie echten [Rechnungs-, Forderungs- und Exportketten][checkpoint-nine] geprüft. Drei CSV/ZIP-Abläufe verifizieren tatsächlich heruntergeladene Exportbytes gegen ihre Manifeste. | Kein Steuerberater-/Buchhaltungsanbieter-Abnahmetest oder produktiver Kontoabgleich; eigener CSV-Vertrag ist kein DATEV-Zertifikat. |

### DP-09 – Kundenportal, Wartung und persönliche Kommunikation (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-09-01** Kundenportal mit freigegebenen Inhalten aufbauen | **K** | [Portalmodell][portal-model], [API][portal-api], [Portaltests][portal-tests] und [echter Portalbrowserlauf][live-portal] beschränken persönliche Partner auf einzelne eingefrorene Verkaufsinhalte/Dokumente. | Realer Kundenbetrieb und alle kommerziellen Portalvarianten sind nicht abgenommen. |
| **DP-09-02** Wartungsverträge und wiederkehrende Termine führen | **T** | [Wartungsfolge][maintenance-docs], [Core][maintenance-workflow], [API][maintenance-api] und [Workerplanung][maintenance-planner] verbinden konfigurierte Checklisten, manuelle oder aktivierte automatische Projekt-/Terminanlage und atomaren Abschluss mit Folgetermin. [Corefälle][maintenance-core-tests] und [HTTP-/PostgreSQL-Fälle][maintenance-api-tests] prüfen Terminanker, Vertragsende, Doppelplanung und aktuelle Rechte. Der [Kalenderdownload][maintenance-calendar-docs] mit [RFC-5545-Generator][maintenance-calendar-core] und [Angular-Vertrag][maintenance-calendar-client] exportiert genau einen sichtbaren gespeicherten Termin als ganztägige Fälligkeit mit stabiler mandantenqualifizierter UID, Revision, exklusivem Ende und minimiertem Inhalt. [Formatfälle][maintenance-calendar-tests], [Clienttests][maintenance-calendar-client-tests], [responsive Browser-Fixtures][maintenance-browser] und [Container-/Neustartcheck][contacts-calendar-originals-smoke] enthalten Hash-/Quellenbindung, Abschluss, UTF-8-Faltung, Kontextwechsel und identische Bytes nach Neustart. [Vertragliche Arbeitstage][maintenance-workdays-docs], [Rechenkern][maintenance-workdays-core] und [Angular-Bedienung][maintenance-ui] ergänzen erlaubte Wochentage, bis zu 366 Sperrtage und eine revisionsgebundene Vorschau mit ausdrücklicher Bestätigung. Ein eingefrorener Ausführungsplan bewahrt Regelkopie und Ausführungstag getrennt von Fälligkeit und Monatsendanker; die aktivierte Workerplanung verwendet dieselben Regeln. [Corefälle][maintenance-workdays-tests], [HTTP-/Identity-/PostgreSQL-Fälle][maintenance-workdays-api-tests], [Clientvertrag][maintenance-workdays-client], [Clientfälle][maintenance-workdays-client-tests] und [Browser-Fixtures][maintenance-browser] prüfen Grenzen, Bestätigung, alte Clients, Replay, Zeitzonen und Kontextwechsel. [Migrationsvarianten][maintenance-workdays-migration] und [API-/Neustartablauf][roadmap-activity-smoke] prüfen historische Regelkopien nach späteren Vertragsänderungen. | Betriebsspezifische Checklisten und ein realer Wartungsfall sind nicht extern abgenommen. Automatische Feiertagsdaten, rechtliche Fristbewertung, kaufmännische Auftrag-/Rechnungserzeugung, Kalenderabonnements, bidirektionale Synchronisierung, Providerzustellung und automatische Aktualisierung importierter Dateien fehlen. Der ICS-Export bleibt ein Hinweis auf die vertragliche Fälligkeit; ein manueller Import garantiert keine Dublettenvermeidung und konkrete Kalenderclients sind nicht bestätigt. Automatische Terminanlage bleibt standardmäßig aus; Personaleinsätze werden in der bestehenden Disposition zugeordnet. |
| **DP-09-03** Kommunikation an Vorgänge binden | **T** | Die [kunden-/projektbezogene Kommunikationsakte][customer-journal-docs] und [API][customer-journal-api] binden manuelle Telefon-, E-Mail-, Besprechungs- und Notizeinträge an vorhandene Kunden, optional Kontakte und zugehörige Projekte. Unveränderliche Originale halten damalige Bezeichnungen, Inhalt, Person und Zeitpunkt fest; getrennte Stornos erhalten Originalkennung und Digest. [HTTP-/PostgreSQL-Fälle][customer-journal-api-tests], [Clientverträge][customer-journal-client] und [responsive Browserfälle][customer-journal-browser] prüfen Quellbindung, aktuelle Rechte, Revision und verspätete Antworten. [Migrationsdrill][migration-drill-source] und [Container-/Neustartcheck][customer-vehicle-ledgers-smoke] sichern historischen Originalerhalt. Bestehende [DeliveryAttempt][commercial], [Outbox][worker] und [PortalFeedback][portal-model] bleiben gesonderte Versand-/Rückfrageabläufe. [Originalanhänge][ledger-originals-docs], [quellengebundene API][ledger-originals-api] und [gemeinsame Angular-Bedienung][ledger-originals-ui] ergänzen PDF/PNG/JPEG mit Speicherversion, Hash, Wiederaufnahme und weiter lesbaren Dateien nach Storno oder Archivierung. [HTTP-/PostgreSQL-Fälle][ledger-originals-api-tests], [Clienttests][ledger-originals-client-tests] und [Browser-Fixtures][ledger-originals-browser] prüfen Originalbindung, Integrität, Rollen und Kontextwechsel. [Migrationsvarianten][ledger-originals-migration] und [tatsächlicher API-/S3-/Neustartablauf][project-templates-originals-smoke] prüfen die erhaltenen Originaldateien. | Automatische ein-/ausgehende Mail-/Chatübernahme, Postfachsynchronisierung und produktive Providerhistorie fehlen. Ein manuell dokumentierter E-Mail-Kontakt bestätigt weder Versand noch Zustellung. Die vollständige betriebliche Kommunikationskette bleibt fachlich abzunehmen. |
| **DP-09-04** Persönliche Benachrichtigungsregeln speichern | **K** | [NotificationPreference][operations-model], [Worker][worker] und [Benachrichtigungstests][worker-tests] prüfen persönliche Uhrzeit/Zeitzone/Wochentage sowie aktuelle Berechtigung. | Zustellung über reale externe Anbieter ist nicht belegt; ruhende lokale Geräte erhalten keinen garantierten Push. |
| **DP-09-05** Push, E-Mail und WhatsApp anbinden | **T** | [SMTP-Adapter][worker] unterstützt konfiguriertes STARTTLS; [Worker-Tests][worker-tests] prüfen fehlende Konfiguration, Fehler und Uncertainty ohne echte Nachricht. | Push- und WhatsApp-Anbindung sowie echter SMTP-Zustellnachweis fehlen; ungedeckte Kanäle melden keinen falschen Erfolg. |
| **DP-09-06** Morgenbriefing und Wartungsablauf absichern | **T** | [Briefingregeln][operations-model] und [Worker-PG-Tests][worker-tests] deduplizieren lokale Tage/DST und filtern aktuelle eigene Arbeit. Ergänzend verbindet die [Wartungsfolge][maintenance-docs] Vertrag, Arbeitsprojekt, dokumentierte Checkliste, Abschluss und nächsten Fälligkeitstermin. [HTTP-/PostgreSQL-Tests][maintenance-api-tests] prüfen zwei Scheduler, Betriebsdatum, Wiederherstellungssperre und entzogene Zuordnung vor Replay; [Browser-Fixtures][maintenance-browser] prüfen den bedienbaren Ablauf und Kontextwechsel. [Vertragliche Arbeitstage][maintenance-workdays-docs], [Rechenkern][maintenance-workdays-core] und [Angular-Bedienung][maintenance-ui] ergänzen erlaubte Wochentage, bis zu 366 Sperrtage und eine revisionsgebundene Vorschau mit ausdrücklicher Bestätigung. Ein eingefrorener Ausführungsplan bewahrt Regelkopie und Ausführungstag getrennt von Fälligkeit und Monatsendanker; die aktivierte Workerplanung verwendet dieselben Regeln. [Corefälle][maintenance-workdays-tests], [HTTP-/Identity-/PostgreSQL-Fälle][maintenance-workdays-api-tests], [Clientvertrag][maintenance-workdays-client], [Clientfälle][maintenance-workdays-client-tests] und [Browser-Fixtures][maintenance-browser] prüfen Grenzen, Bestätigung, alte Clients, Replay, Zeitzonen und Kontextwechsel. [Migrationsvarianten][maintenance-workdays-migration] und [API-/Neustartablauf][roadmap-activity-smoke] prüfen historische Regelkopien nach späteren Vertragsänderungen. | Vollständiger echter Wartungs-/Morgenbriefingablauf mit realen Betriebsdaten und Zustellung über einen gewählten Provider bleibt offen. Die automatische Wartungsanlage belegt weder einen laufenden Produktivworker noch Kalender-, E-Mail-, Push- oder WhatsApp-Zustellung. Die ausdrücklich konfigurierte Arbeitstagsregel gilt je Wartungsvertrag; sie bewertet keine Ressourcenverfügbarkeit oder gesetzlichen Fristen. |

### DP-10 – Dachmodell und Aufmaß (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-10-01** Fachmodell, Einheiten und Revisionen | **K** | [RoofRevision/Measurement][roof-model], [Dach-/Aufmaßtests][roof-tests] und [Graphregression][regression-tests] sichern Gebäude, Vorgängerversion, Mengen und Einheiten. | Betriebliche Dachdetail-/Vermessungsstandards und reale Bestandsakten bleiben zu prüfen. |
| **DP-10-02** Einfache Dachgeometrie und Mengenkern | **K** | [Geometriekern][roof-geometry], [unabhängige Geometrietests][geometry-tests] und [Live-Polygon][live-renovation] prüfen Ebenen, konkave Polygone, Öffnungen und Mengensummen. | Keine 3D-Dachkonstruktion, automatische Flächenerkennung oder statische Nachweisrechnung. |
| **DP-10-03** Aufmaß erfassen und nachweisen | **K** | [Aufmaßtypen][roof-model], [Technical-API/PDF][technical-api] und [Domänentests][roof-tests] speichern Maße und Abzüge unveränderlich. Die [Angular-Aufmaßakte][measurement-ui] prüft vor dem PDF-Download Quelle, Projekt, eingefrorenen Digest, Dateimetadaten, PDF-Kennung und SHA-256 der tatsächlichen Bytes. [Browser-Fixtures][measurement-browser] prüfen intakte und beschädigte Dateien sowie verspätete Downloads nach Betriebs-/Seitenwechsel; der aktuelle Zugriffskontext wird auch nach der asynchronen Hashprüfung erneut geprüft. | Vertragliche Abzugsregeln, praktische Geländeaufnahme und unabhängige fachliche Abnahme bleiben offen. Kontrollierte Browserantworten ersetzen keinen realen Vermessungs- oder vollständigen Browser-/S3-Betriebsnachweis. |
| **DP-10-04** Dachdetails und technische Bauteile | **T** | [RoofDetail][roof-model] modelliert First/Traufe/Ortgang/Kehle/Grat sowie Durchdringung/Ablauf mit expliziter Länge/Zahl und Flächenbezug. | Nur beschreibende Details; keine parametrischen technischen Bauteile oder daraus abgeleitete Entwässerungs-/Lastberechnung. |
| **DP-10-05** Externe Aufmaße und Planunterlagen | **T** | [Dokument-API][documents] nimmt Planunterlagen als geprüfte Dateien auf; [Geometrieeingaben][roof-geometry] sind explizit definiert. | Kein DXF-/IFC-/Laser-/Drohnenadapter oder geprüfte Übernahme externer Aufmaße mit Koordinatentransformation. |
| **DP-10-06** Mengen, Material und Leistungsverzeichnis verbinden | **K** | [QuantityEvidence/MaterialDemand][quantity-model] und [Mengentransfer-API][quantity-api] binden eingefrorene Aufmaßmengen an Angebot, gemessene Rechnung/Korrektur und Einkauf. Drei echte [UI-Fälle][checkpoint-nine] prüfen 10,8 m² Quellmenge, ausdrücklich umgerechnete 13,5 m² und genau einen Bestellentwurf samt Herkunft. | Kein allgemeiner externer LV-/GAEB-Austausch, normative Produkt-/Verschnittregel oder reale betriebliche Mengenabnahme. |

### DP-11 – Technische Fachmodule (P2; DP-11-01 früh P0)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-11-01** Verfahren, Datenstände und Nutzungsrechte klären | **T** | [Methoden-/Rechteinventar][methods] trennt elementare Mathematik, öffentliche Normmetadaten, Herstellerdaten und statische Installerbefunde. Die [Hersteller-Funktionsmatrix](vendor-functional-reference.md) ordnet acht benannte Demos vorläufig den öffentlich beschriebenen Abläufen zu, ohne proprietäre Inhalte zu übernehmen. | Lizenzierte Normvolltexte/Herstellerdaten, Rechteklärung, versionsgenaue Demo-Funktionstests und unabhängige fachliche Referenzabnahme fehlen. |
| **DP-11-02** Rechenkern und nachvollziehbare Ergebnisberichte | **K** | [Provenance][provenance], [TechnicalRecord][technical-record], [Technical-API/PDF][technical-api] und [JSONB-Regressionsfälle][regression-tests] frieren Methode/Input/Resultat/Digest ein. | Nur deklarierte elementare Verfahren; keine generelle Fachsoftware- oder normative Berichtszertifizierung. |
| **DP-11-03** U-Wert und thermische Aufbauten | **T** | [ThermalCalculator][thermal] und [ThermalBridgeCalculator][bridges] berechnen homogene Serien, ideale parallele Pfade und externe psi/chi-Summen. Der [inhomogene Aufbau][inhomogeneous-docs] mit [eigenem Rechenkern][inhomogeneous-core] ergänzt obere/untere Widerstandsgrenzen und eine ausdrücklich nichtnormative U-Wert-Abschätzung für ausgerichtete, ebene, nichtmetallische Schichten mit dokumentierten Materialquellen. [Corefälle][inhomogeneous-tests], [HTTP-/PostgreSQL-Fälle][inhomogeneous-api-tests], [Angular-Arbeitsplatz][technical-ui], [Clienttests][technical-client-tests] und [Browser-Fixtures][technical-browser] umfassen Eingabegrenzen, Quellenbindung und projektgebundene Speicherung. | Vollständige ISO-6946-Verfahren einschließlich systematisch validierter Luftschichten und normativer Korrekturterme, mehrdimensionale Wärmebrücken, unabhängig bestätigte Materialdaten und normative Sparrendachprüfung fehlen. Referenzabweichungen, Fachabnahme und aktuelle deutsche nationale Anwendung bleiben zu prüfen. Die Eingabebestätigung zu metallischen Brücken ist kein unabhängiger Nachweis. |
| **DP-11-04** Feuchteschutz und Verfahrensgrenzen | **T** | Der [ISO-13788-Teilumfang][advanced-exchange-docs] und [Rechenkern][iso13788-core] berechnen monatliche Kondensation, Verdunstung, Wasserüberträge, Jahresrestmengen und den Austrocknungsvergleich über bis zu 120 Monate. Zwölf ausdrücklich erfasste Klimapunkte, Kennwertquellen und Anwendungsbestätigung sind erforderlich. [Corefälle][iso13788-tests] und [Fach-API-Fälle][advanced-technical-api-tests] prüfen begrenzte Eingaben und Ergebnisse; die [erweiterte Angular-Maske][advanced-technical-ui] erfasst Klima-, Material- und Verfahrensangaben; [Technical API][technical-api] speichert reproduzierbare Rechenläufe mit normalisierten Eingaben und Digest am Projekt. Der [erweiterte Clientvertrag][advanced-client-contract] prüft normalisierte Eingaben, quellengebundene Ergebniswerte und die vollständige unveränderliche Vorschau bei Projektablage. [Clientfälle][advanced-client-tests], [Desktop-/Tablet-/Mobil-Fixtures][advanced-browser-tests] und der [Core-Fixturevertrag][advanced-fixture-contract] ergänzen eigenständige Regressionen; [Umfang und Prüfgrenzen][advanced-client-docs] unterscheiden Konsistenzprüfung und Fachabnahme. Der [echte HTTP-/Projektaktenvergleich][advanced-wire-tests] prüft sechs synthetische Fixtures einschließlich der unterschiedlichen Dezimalstring-/eingebetteten JSON-Gelddarstellung; die benannten Geldfelder werden im Client centgenau verglichen. | Kein vollständig abgenommener Feuchteschutznachweis nach DIN 4108-3, keine instationäre hygrothermische Simulation und keine automatische Freigabe realer Bauteile. Nationale Bauartbedingungen, Schadensgrenzen, reale Klima-/Materialdaten und unabhängige Referenzabnahme bleiben offen. Die dokumentierte Abweichung zum informativen ISO-Beispiel C.4/C.5 ist ungeklärt und darf nicht als bestandene Referenz gelten. |
| **DP-11-05** Gefälledämmung und Verlegeplanung | **T** | [Gefälleplatten- und Mehrfeldplanung][advanced-exchange-docs], [Plattenlayout][tapered-board-core] und [Mehrfeldkern][tapered-assembly-core] verarbeiten ausdrücklich eingegebene Hersteller-/Produkt-/Katalogrevisionen und SKUs für rechteckige, einachsige Gefällefelder. Sie berechnen Positionen, örtliche Dicken, Zuschnitte, Verschnitt und zusammengeführte Stück-/Packmengen; Lücken, Überlappungen und Dickenunstetigkeit an gemeinsamen Feldgrenzen werden abgewiesen. [Plattenfälle][tapered-board-tests], [Mehrfeldfälle][tapered-assembly-tests] und [HTTP-/PostgreSQL-Fälle][advanced-technical-api-tests] umfassen synthetische Geometrien und die nichtnormative Projektablage; die [erweiterte Angular-Maske][advanced-technical-ui] bietet die ausdrückliche Katalog- und Feldeingabe. Der [erweiterte Clientvertrag][advanced-client-contract] prüft normalisierte Eingaben, quellengebundene Ergebniswerte und die vollständige unveränderliche Vorschau bei Projektablage. [Clientfälle][advanced-client-tests], [Desktop-/Tablet-/Mobil-Fixtures][advanced-browser-tests] und der [Core-Fixturevertrag][advanced-fixture-contract] ergänzen eigenständige Regressionen; [Umfang und Prüfgrenzen][advanced-client-docs] unterscheiden Konsistenzprüfung und Fachabnahme. Der [echte HTTP-/Projektaktenvergleich][advanced-wire-tests] prüft sechs synthetische Fixtures einschließlich der unterschiedlichen Dezimalstring-/eingebetteten JSON-Gelddarstellung; die benannten Geldfelder werden im Client centgenau verglichen. | Unabhängig geprüfte, rechtssicher nutzbare Herstellerkataloge und Hersteller-/Fachplanerfreigaben fehlen. Innenliegende Abläufe, Kehlen, Öffnungen/Durchdringungen, nichtrechteckige Entwässerungsgeometrie, Zuschnittoptimierung und Wiederverwendung von Verschnitt sind nicht enthalten. Stetige Fugendicken sind kein Entwässerungsnachweis; die Ausgabe ist kein ausführungs- oder bestellverbindlicher Verlegeplan. |
| **DP-11-06** Windlast und Befestigung | **T** | Quellengebundene lokale Winddruckfälle mit getrennten inneren/äußeren Bezugsdrücken, Nettodruck, Sog, lokaler Normalkraft und unveränderlicher Projektablage. [Methoden und Bedienung][roof-actions-docs], [Core][roof-actions-core-tests], [HTTP][roof-actions-api-tests], [Client][roof-actions-client-tests], [Browser][roof-actions-browser] und [Container][roof-actions-smoke]. | Standort-/Zonendaten, Bezugshöhen, Regelwerks-/NA-Auswahl, maßgebende Druckbeiwerte und Fälle, Lastkombinationen, Dynamik, Befestiger-/Untergrunddaten und unabhängig geprüfte konstruktive Nachweise bleiben offen. |
| **DP-11-07** Haupt- und Notentwässerung | **T** | Quellengebundene Bilanzen unabhängiger Einzugsflächen mit Haupt-/Starkregenzufluss, begrenzter Hauptanrechnung beziehungsweise Hauptausfall, getrennten Defiziten und unveränderlicher Projektablage. [Methoden und Bedienung][roof-actions-docs], [Core][roof-actions-core-tests], [HTTP][roof-actions-api-tests], [Client][roof-actions-client-tests], [Browser][roof-actions-browser] und [Container][roof-actions-smoke]. | Standort-/Normdatenwahl, wirksame Flächenermittlung, belegte Kapazitäten bei den maßgebenden Wasserständen, Fließwege, hydraulische Leitungsdimensionierung, Rückstau-/Überstauprüfung und unabhängige Fachabnahme bleiben offen. Kein Nulldefizit ist eine Entwässerungsfreigabe. |
| **DP-11-08** Steildach, Schnee und konstruktive Nachweise | **T** | Quellengebundene einzelne Schneelastfälle mit vorgegebenen Beiwerten, horizontalen Zonen, Vertikallasten/-kräften und unveränderlicher Projektablage. [Methoden und Bedienung][roof-actions-docs], [Core][roof-actions-core-tests], [HTTP][roof-actions-api-tests], [Client][roof-actions-client-tests], [Browser][roof-actions-browser] und [Container][roof-actions-smoke]. | Standort-/Höhendaten, nationale Anwendung, Dachform-/Beiwertwahl, Verwehung und Abrutschen, außergewöhnliche und maßgebende Lastanordnungen, Kombinationen, tragende Bauteile/Verbindungen und unabhängige konstruktive Nachweise bleiben offen. |
| **DP-11-09** Photovoltaik als Dachplanungsmodul | **T** | [PV-Geometrie](pv-module-layout.md) und [Rechenkern](../src/FirstBlick.Core/Technical/PvModuleLayout.cs) planen ein rechteckiges Dachflächen-Raster mit ausdrücklich eingegebenen Modulen, Rand- und Hindernisabständen, Hoch-/Querformatvergleich, positionsbezogener Ausgabeliste und SHA-256-Provenienz. [Corefälle](../tests/FirstBlick.Core.Tests/Technical/PvModuleLayoutTests.cs), [HTTP-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/PvLayoutApiTests.cs) und [Clientverträge](../web/tests/pv-layout.test.ts) prüfen Grenzen; bestandenes CI ist für den finalen Commit gesondert nachzuweisen. | Raster ist weder vollständig optimiert noch für mehrseitige/nichtrechteckige Dächer geeignet. Verschattung, Ertrag, Montage, Befestigung/Last, Brandschutz, Elektrotechnik, Hersteller-/Standortdaten und unabhängige Fachabnahme fehlen. |
| **DP-11-10** Fachsoftwareintegration und Gesamtprüfung | **O** | [Statisches Installerinventar][methods] enthält weder ausgeführte Demoalgorithmen noch kopierte Herstellerdaten. | Fachsoftwareadapter, lizenzierte Datenaustauschverträge und unabhängiger vollständiger Flach-/Steildachvergleich fehlen. |

### DP-12 – Schnittstellen und Datenübernahme (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-12-01** Integrationsverträge und Adapterbasis definieren | **K** | [Versionierte CSV-/Exportverträge][integrations], [Core-/PG-Nachweise][integration-evidence] und drei echte [UI-Ketten][checkpoint-nine] prüfen Vorschau, ausdrücklich ausgelösten Import, stabile Duplikat-IDs, atomare Konfliktablehnung und verifizierte Exportpakete. | Weitere Provider benötigen eigene Profile, Identitäts-/Idempotenzregeln und geprüfte Fixtures. |
| **DP-12-02** GAEB und Material-/Leistungskataloge anbinden | **T** | Die [GAEB-Prozesskette][advanced-exchange-docs] trennt den [strukturellen v1-Katalogimport][gaeb-catalog-api] mit ausdrücklich zugeordneten Kosten von der [v2-XSD-/X84-Exportkette][official-gaeb-api] für DA XML 3.3, X83/X84 2021-05. Original, Positionen, Projekt, Preise und bestätigter Vorschaudigest bleiben gebunden; X83-Original, X84-Ausgabe und Evidenz werden versioniert erhalten. [Katalogfälle][gaeb-catalog-tests], [offizielle Austauschfälle][official-gaeb-tests] und [Endpoint-Fälle][official-gaeb-api-tests] prüfen diese Grenzen. [Preisarbeitslisten][gaeb-worksheet-docs] ergänzen ausdrücklich bestätigte CSV-Bepreisung im Browser mit [Clienttests][gaeb-worksheet-tests] und [Browser-Fixtures][gaeb-worksheet-browser] zu Quellbindung, Atomarität und veralteten Entwürfen. | Keine vollständige GAEB-Phasenunterstützung, besonderen Preis-/Positionsformen, Bietertextergänzungen oder Anlagenrückexport. X83-Freitext bleibt im Original erhalten und wird nicht als verlustfreier X84-Rückexport ausgegeben. Hersteller-/STLB-Lizenzdaten, breite AVA-/Tabellenkalkulationskompatibilität, Zertifizierung und automatische Angebotsabgabe fehlen. Der v1-Katalogimport ersetzt keine XSD-Prüfung; reale Profile und fachliche Angebotsfreigabe bleiben gesondert abzunehmen. |
| **DP-12-03** Buchhaltungs- und Lohnexporte umsetzen | **T** | [LedgerExport][ledger-export], [PayrollTimeExport][payroll-export] und [echte Exporttests][integration-tests] liefern IDs/Digests/Korrekturen sowie exakte UTC-Pausen-/DST-Segmente. | Keine providerfertige Buchungsschnittstelle/Lohnabrechnung, Kontierung, Steuer-/Tariflogik oder Providerabnahme. |
| **DP-12-04** Dokumentierte API und Ereignisse bereitstellen | **T** | [API v1][api-program] und [Adapterdokumentation][integrations] dokumentieren Auth, Formate und `firstblick.integration-event/1.0.0` in Importreceipts. [18 Rechenvorschauen][technical-contract-docs] ergänzen konkrete Ergebnis-/Problem-Schemas, stabile Operationsnamen und einen [versionierten Mindestvertrag][technical-openapi-minimum], der gegen das tatsächlich authentifiziert ausgelieferte [OpenAPI-Dokument geprüft wird][technical-openapi-tests]. Fehlerhafte JSON-/Medientypbindung verwendet den generischen Problem-Pfad auch außerhalb Development. | Keine externe Ereignissubskription/Webhookauslieferung oder vollautomatisierte OpenAPI-Kompatibilitätsprüfung. |
| **DP-12-05** Format- und Anbieterwartung etablieren | **T** | [Integrationswartung][integrations] fordert Versions-/Rechte-/Fixture-/Ablehnungsregeln; Locks/CI sichern Änderungen an der eigenen Implementierung. | Benannte Providerverantwortliche, Versionsmonitoring und konkret freigegebene Fremdprofile fehlen. |

### DP-13 – Migration, Sicherung und dauerhafter Betrieb (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-13-01** Backup und vollständigen Restore implementieren | **K** | [Backup/Restore][operations], [nativer Drill][restore-evidence], [PG18.6-Container-Drill][container-restore] und [Integritätsmanifest][restore-integrity] belegen Zeilen, historische S3-Versionen, Keyring und kontrollierte Recovery-Sperre/Wiederaufnahme. | Beide Drills verwenden nur 20 Tabellen/36 Zeilen und zwei S3-Versionen. Repräsentative Produktions-RPO/RTO, externer Sicherungsbetrieb und der abschließende aktuelle Containerlauf bleiben separat. |
| **DP-13-02** Datenschutz und Datenlebenszyklus ausarbeiten | **T** | [Self-Service-Datenkopie][discovery-docs] mit [11 HTTP-/PG-Fällen][discovery-evidence], [drei echten UI-Downloads][discovery-live] einschließlich ZIP-/Manifest-SHA und [Offline-Purge-Tests][offline-tests] ergänzen die Konten-/Mandantentrennung. | Keine vollständige datenschutzrechtliche Auskunft: Archiv-/Anhangsdaten und andere Kategorien fehlen; Lösch-/Aufbewahrungsregeln, Auskunftskonflikte, Betreibervereinbarungen und fachliche Datenschutzabnahme bleiben offen. |
| **DP-13-03** Last, Kosten und Betriebsfehler messen | **T** | [Budgets][api-program], [Outbox-Fehlerfälle][worker-tests] und [Betriebsmetriken][observability] liefern begrenzte Fehler-/Zeitnachweise. Der neue [Production-Assetcheck][asset-check] erkennt tatsächlich von CSP blockierte Bildschirm-Styles und ist in den [Container-Smoke][container-smoke] eingebunden. | Keine repräsentativen Datenmengen, Last-/Kostenmodelle, Kapazitätsgrenzen, externe Alarmierung oder gemessenen Betriebsausfall-SLOs. |
| **DP-13-04** Wiederholbare Datenmigration bauen | **T** | [Kanonische Migration][migration-docs], [Import-/Prüfwerkzeug][migration-import] und [echter Migrationsdrill][migration-evidence] übernehmen 2 Mandanten/19 Entitäten/2 Dateien, kontrollieren Salden/Hashes/Nummern und weisen 11 negative Quellenfälle zurück; Wiederholung erzeugt keine neuen DB-/S3-Schreibvorgänge. | Der synthetische kanonische Export ersetzt keinen verfügbaren tatsächlichen D1-/R2-Export, bestätigte Quelladapter-/Ownershipzuordnung, echte Quellkontrollsummen oder eine reale Cutover-Abnahme. |
| **DP-13-05** Umschaltung und Rückkehr proben | **T** | [Operations-Runbook][operations] beschreibt isoliertes Ziel, Schreibstopp, Schlüssel, Verifikation und Rückfall; [Restore-Drill][restore-evidence] prüft Zielschutz. | Tatsächliche Umschaltung/DNS/Proxy, Writes nach Umschaltung und Rückkehr mit repräsentativen Bestandsdaten sind nicht geprobt. |
| **DP-13-06** Gesamtfälle und Wartungsdokumentation abschließen | **T** | [Checkpoint 8][backend-eight] mit 377 Serverfällen und [erfolgreichen eigenen CI-/Container-/Restore-/Migrationsläufen][ci-eight] sowie [Checkpoint 9][checkpoint-nine] mit 45 Units, 216 Clientfixtures und 30 unterschiedlichen echten Browserkombinationen dokumentieren die aktuellen getrennten Umfänge. | Kein finaler Gesamtnachweis aller 100 Aufgaben. Der jeweils abschließende veröffentlichte Commit benötigt seine eigenen in PR #1 verlinkten CI-/Operations-Ergebnisse; echte Altquelle, reale Betriebsfälle und externe Fachabnahmen bleiben offen. |

### DP-14 – Kennzahlen und überprüfbare Assistenz (P2/P3)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-14-01** Kennzahlen definieren und verlinken | **T** | [Dashboard][business-api], [Finanzlesemodell][finance-read], [Kostenbericht][cost-docs] und [Betriebsübersicht][observability] verknüpfen klar definierte Zustände bzw. Beträge mit ihren prüfbaren Quellen; [Checkpoint 9][checkpoint-nine] ergänzt echte UI-Nachweise. Die [Projekt- und Aufgabenstatistik][business-activity-docs] mit [Rechenkern][business-activity-core], [HTTP-Abfrage][business-activity-api] und [Angular-Arbeitsplatz][business-activity-ui] definiert angelegte Projekte, angelegte Aufgaben und aktuell gespeicherte Aufgabenfälligkeiten. Ein inklusiver Zeitraum von 1 bis 366 Tagen wird mit dem unmittelbar davor liegenden gleich langen Zeitraum verglichen; Monatsabschnitte, Betriebszeitzone, Projekt-/Archivfilter und verlinkte Quellenrevisionen gehören zum Vertrag. Mehr als 5.000 passende Quellen werden ohne Teilsummen abgewiesen. [Corefälle][business-activity-tests], [HTTP-/Identity-/PostgreSQL-Fälle][business-activity-api-tests], [Clientvertrag][business-activity-client], [Clientfälle][business-activity-client-tests] und [Browser-Fixtures][business-activity-browser] prüfen Summen, Kalendergrenzen, Rechte nach Lock-Warten, Filter und Kontextwechsel. Der [API-/Neustartablauf][roadmap-activity-smoke] vergleicht dieselben Berichtsquellen und Digests. | Vollständiger KPI-Katalog, historische Status-/Bestandsreihen, Abschluss-/Produktivitätskennzahlen, abgestimmte betriebliche Kennzahlendefinitionen und frei wählbare Analysen fehlen. Die aktuellen Fälligkeiten und Statuswerte sind keine Rekonstruktion früherer Rückstände oder Abschlüsse. Der Zeitvergleich bleibt auf die drei ausdrücklich definierten Kennzahlen begrenzt. |
| **DP-14-02** Auswertungen und Datenexport ergänzen | **T** | [Versionierte CSV-Pakete][integrations] und [quellengebundener Kostenbericht][cost-docs] liefern prüfbare Detaildaten; die neue [Exportbedienung][ui-nine] ist mit tatsächlichen Downloads und Manifest-/Digestprüfung im [Checkpoint 9][checkpoint-nine] belegt. Der [Berichtsarbeitsplatz][business-activity-docs] mit [HTTP-Export][business-activity-api], [Angular-Bedienung][business-activity-ui] und [Clientvertrag][business-activity-client] ergänzt CSV-Quelldaten und vollständige JSON-Berichte für die ausdrücklich gewählten Zeitraum-, Projekt- und Archivfilter. Betrieb, Quellenrevisionen und Berichtsprüfsumme binden den Export an die geprüfte Ansicht; geänderte Quellen erfordern ein erneutes Laden. [CSV-Regeln][business-activity-core] neutralisieren die erfassten führenden Formelzeichen in Textzellen. [Corefälle][business-activity-tests], [HTTP-/Identity-/PostgreSQL-Fälle][business-activity-api-tests], [Clientfälle][business-activity-client-tests] und [Browser-Fixtures][business-activity-browser] prüfen Filterbindung, Bytes, Quellen, Rechte und verzögerte Downloads. Der [API-/Neustartablauf][roadmap-activity-smoke] vergleicht Berichtsquellen, Digest und stabile CSV-Bytes. | Frei konfigurierbare Berichtsdesigner, zusätzliche Kennzahlfamilien, interaktive Trends und automatische Berichtszustellung fehlen. Die vorhandenen Berichte sind ausdrücklich definierte Einzelverträge. CSV enthält die Quellen beider Zeiträume; JSON ergänzt Summen und Monatsabschnitte. Die Dateien begründen keine historische Status- oder Produktivitätsauswertung. |
| **DP-14-03** Optionale Assistenz kontrolliert integrieren | **O** | [Architekturgrenzen][architecture] unterscheiden überprüfbare Fachlogik von ungeprüften Schlussfolgerungen. | Ein optionales Assistenzsystem mit Provenienz, Freigaben, Evaluation, Kosten- und Datenschutzkontrolle ist nicht implementiert. |

## Aus dieser Sichtung folgende Prioritäten

1. Den finalen Quellstand mit vollständig grünem Server-/Web- und Container-/Operations-Lauf verbinden; aktuelle Recovery-/Migrationsänderungen gesondert nachweisen.
2. Den bestandenen Offline-Neustartablauf mit repräsentativen Betriebsfällen und realen Geräten abnehmen; Storage-Eviction, Hardwareverlust, zusätzliche Feldrollen und Konfliktfälle gesondert prüfen.
3. Den bestandenen kanonischen Migrationsdrill mit genehmigtem realem D1-/R2-Export, überprüften Quellkontrollsummen und fachlich bestätigtem Mapping proben. Kein bestehendes Produktionsziel überschreiben.
4. Den implementierten Mengenübergang mit betrieblichem Referenzfall und vollständiger Bedienung abnehmen; Istkosten aus nachvollziehbaren Quellen ableiten; Einkaufsdifferenzen und Mahnwesen vollständig spezifizieren.
5. Benötigte Rechnungs-/GAEB-/Bank-/Lohnprofile samt Version, Nutzungsrechten, Referenzdaten und verantwortlicher Abnahme festlegen. Eine eigene CSV-Datei ersetzt diesen Vertrag nicht.
6. Die vorhandenen begrenzten Bauphysikverfahren mit geklärten Datenrechten, Anwendungsprüfung und unabhängigen Referenzfällen fachlich abnehmen; verbleibende Verfahren erst innerhalb ihres definierten Anwendungsbereichs ergänzen. Vorhandene Teilrechnungen sind keine vollständige Feuchte-/Last-/Entwässerungsfreigabe.
7. Reale Sanierungs-, Reparatur- und Wartungsfälle mit dem Betrieb abnehmen und Datenlebenszyklus, Monitoring, Last/Kosten sowie Produktions-RPO/RTO festlegen.

## Pflege dieser Matrix

Bei einer Änderung wird die betreffende Zeile mit dem neuen Quell-/Testbeleg und dessen tatsächlichem Ergebnis aktualisiert. Externe Fachabnahmen benötigen einen benannten Umfang und Beleg; sie werden nicht aus Testanzahlen abgeleitet. Fehlende Funktionen bleiben mit ihrer ursprünglichen Kennung sichtbar. Diese Matrix ist keine Erlaubnis, den Roadmapumfang zu reduzieren.

[discovery-live]: ../docs/evidence/live-discovery-checkpoint7.json
[discovery-ui-docs]: ../docs/discovery-ui.md
[backend-seven]: ../docs/evidence/checkpoint7.json
[checkpoint-ten]: ../docs/checkpoint10.md
[checkpoint-eleven]: ../docs/checkpoint11.md
[supplier-allocations]: ../docs/supplier-cost-allocations.md
[financial-sources]: ../src/FirstBlick.Api/FinancialSources.cs
[quantity-docs]: ../docs/quantity-links.md
[forecast-model]: ../src/FirstBlick.Core/Business/CostForecast.cs
[cost-browser]: ../web/tests/browser/cost-analysis.spec.ts
[finance-links]: ../src/FirstBlick.Api/FinancialSources.cs
[cost-docs]: ../docs/cost-analysis.md
[cost-model]: ../src/FirstBlick.Core/Business/CostAnalysis.cs
[cost-api]: ../src/FirstBlick.Api/CostEndpoints.cs
[cost-tests]: ../tests/FirstBlick.Core.Tests/Business/CostAnalysisTests.cs
[cost-api-tests]: ../tests/FirstBlick.Api.Tests/CostApiTests.cs
[discovery-api]: ../src/FirstBlick.Api/DiscoveryEndpoints.cs
[discovery-access]: ../src/FirstBlick.Api/DiscoveryAccess.cs
[discovery-docs]: ../docs/discovery-data-copy.md
[discovery-tests]: ../tests/FirstBlick.Api.Tests/DiscoveryApiTests.cs
[discovery-evidence]: ../docs/evidence/discovery-checkpoint7.json
[quantity-model]: ../src/FirstBlick.Core/Business/QuantityLinks.cs
[quantity-api]: ../src/FirstBlick.Api/QuantityEndpoints.cs
[quantity-tests]: ../tests/FirstBlick.Core.Tests/Business/QuantityLinkTests.cs
[quantity-api-tests]: ../tests/FirstBlick.Api.Tests/QuantityApiTests.cs
[live-offline]: ../docs/evidence/live-offline-checkpoint7.json
[session-access]: ../web/src/app/core/session-access.ts
[container-restore]: ../docs/evidence/container-restore-drill.json
[migration-docs]: ../docs/migration.md
[migration-import]: ../tools/FirstBlick.Operations/MigrationImport.cs
[migration-evidence]: ../docs/evidence/migration-drill.json
[baseline]: ../docs/BASELINE.md
[roadmap]: ../docs/ROADMAP.md
[architecture]: ../docs/ARCHITECTURE.md
[readme]: ../README.md
[methods]: ../docs/technical-methods.md
[jobs]: ../docs/jobs.md
[integrations]: ../docs/integrations.md
[operations]: ../docs/operations.md
[backend-evidence]: ../docs/evidence/checkpoint6.json
[live-renovation]: ../docs/evidence/live-workflow-checkpoint4.json
[live-portal]: ../docs/evidence/live-portal-checkpoint5.json
[integration-evidence]: ../docs/evidence/integrations-checkpoint.json
[restore-evidence]: ../docs/evidence/restore-drill.json
[restore-integrity]: ../docs/evidence/restore-drill-integrity.json
[ci]: ../.github/workflows/ci.yml
[api-program]: ../src/FirstBlick.Api/Program.cs
[api-infrastructure]: ../src/FirstBlick.Api/ApiInfrastructure.cs
[auth]: ../src/FirstBlick.Api/AuthEndpoints.cs
[management]: ../src/FirstBlick.Api/ManagementEndpoints.cs
[approvals]: ../src/FirstBlick.Api/ApprovalService.cs
[business-api]: ../src/FirstBlick.Api/BusinessEndpoints.cs
[commercial-api]: ../src/FirstBlick.Api/CommercialEndpoints.cs
[field-api]: ../src/FirstBlick.Api/FieldEndpoints.cs
[documents]: ../src/FirstBlick.Api/DocumentEndpoints.cs
[technical-api]: ../src/FirstBlick.Api/TechnicalEndpoints.cs
[portal-api]: ../src/FirstBlick.Api/PortalEndpoints.cs
[integration-api]: ../src/FirstBlick.Api/IntegrationEndpoints.cs
[record-policy]: ../src/FirstBlick.Api/RecordPolicy.cs
[access]: ../src/FirstBlick.Core/Security/AccessRules.cs
[foundation]: ../src/FirstBlick.Core/Business/Foundation.cs
[projects]: ../src/FirstBlick.Core/Business/Projects.cs
[commercial]: ../src/FirstBlick.Core/Business/Commercial.cs
[planning]: ../src/FirstBlick.Core/Business/Planning.cs
[inventory]: ../src/FirstBlick.Core/Business/Inventory.cs
[finance]: ../src/FirstBlick.Core/Business/Finance.cs
[supplier-finance]: ../src/FirstBlick.Core/Business/SupplierFinance.cs
[operations-model]: ../src/FirstBlick.Core/Business/Operations.cs
[training]: ../src/FirstBlick.Core/Business/TrainingAndForms.cs
[roof-model]: ../src/FirstBlick.Core/Business/RoofMeasurements.cs
[portal-model]: ../src/FirstBlick.Core/Business/Portal.cs
[technical-record]: ../src/FirstBlick.Core/Business/TechnicalRecord.cs
[roof-geometry]: ../src/FirstBlick.Core/Technical/RoofGeometry.cs
[thermal]: ../src/FirstBlick.Core/Technical/ThermalCalculator.cs
[bridges]: ../src/FirstBlick.Core/Technical/ThermalBridgeCalculator.cs
[provenance]: ../src/FirstBlick.Core/Technical/CalculationProvenance.cs
[csv]: ../src/FirstBlick.Core/Integrations/CsvV1.cs
[bank-csv]: ../src/FirstBlick.Core/Integrations/BankCsvV1.cs
[ledger-export]: ../src/FirstBlick.Core/Integrations/LedgerExport.cs
[payroll-export]: ../src/FirstBlick.Core/Integrations/PayrollTimeExport.cs
[store]: ../src/FirstBlick.Infrastructure/Persistence/EntityStore.cs
[models]: ../src/FirstBlick.Infrastructure/Persistence/Models.cs
[dbcontext]: ../src/FirstBlick.Infrastructure/Persistence/FirstBlickDbContext.cs
[migration]: ../src/FirstBlick.Infrastructure/Persistence/Migrations/20261020043844_InitialSchema.cs
[finance-read]: ../src/FirstBlick.Infrastructure/Persistence/FinanceReadModel.cs
[service-registration]: ../src/FirstBlick.Infrastructure/ServiceRegistration.cs
[worker]: ../src/FirstBlick.Infrastructure/OutboxProcessor.cs
[object-store]: ../src/FirstBlick.Infrastructure/Storage/ObjectStore.cs
[pdf]: ../src/FirstBlick.Infrastructure/Documents/BusinessPdf.cs
[security-tests]: ../tests/FirstBlick.Api.Tests/SecurityApiTests.cs
[regression-tests]: ../tests/FirstBlick.Api.Tests/AuthorizationRegressionTests.cs
[document-tests]: ../tests/FirstBlick.Api.Tests/DocumentApiTests.cs
[commercial-api-tests]: ../tests/FirstBlick.Api.Tests/CommercialApiTests.cs
[worker-tests]: ../tests/FirstBlick.Api.Tests/WorkerTests.cs
[portal-tests]: ../tests/FirstBlick.Api.Tests/PortalApiTests.cs
[integration-tests]: ../tests/FirstBlick.Api.Tests/IntegrationApiTests.cs
[commercial-tests]: ../tests/FirstBlick.Core.Tests/Business/CommercialTests.cs
[operations-tests]: ../tests/FirstBlick.Core.Tests/Business/OperationsFormTests.cs
[planning-tests]: ../tests/FirstBlick.Core.Tests/Business/PlanningInventoryTests.cs
[supplier-tests]: ../tests/FirstBlick.Core.Tests/Business/SupplierFinanceTests.cs
[ledger-tests]: ../tests/FirstBlick.Core.Tests/Business/LedgerTests.cs
[roof-tests]: ../tests/FirstBlick.Core.Tests/Business/RoofMeasurementTests.cs
[geometry-tests]: ../tests/FirstBlick.Core.Tests/Technical/RoofGeometryTests.cs
[thermal-tests]: ../tests/FirstBlick.Core.Tests/Technical/ThermalTests.cs
[csv-tests]: ../tests/FirstBlick.Core.Tests/Integrations/CsvAdapterTests.cs
[payroll-tests]: ../tests/FirstBlick.Core.Tests/Integrations/PayrollExportTests.cs
[routes]: ../web/src/app/app.routes.ts
[styles]: ../web/src/styles.css
[web-api]: ../web/src/app/core/api.service.ts
[collections]: ../web/src/app/pages/collection.component.ts
[workflow-ui]: ../web/src/app/pages/workflow-panel.component.ts
[offline]: ../web/src/app/core/offline-store.ts
[offline-tests]: ../web/tests/offline-store.test.ts
[sw-tests]: ../web/tests/service-worker.test.ts
[browser-tests]: ../web/tests/browser/workflows.spec.ts
[attachments-eight]: evidence/attachment-inventory-2026-10-20.json
[backend-eight]: evidence/checkpoint8-backend.json
[observability]: observability.md
[operations-api-tests]: ../tests/FirstBlick.Api.Tests/OperationsApiTests.cs
[telemetry-tests]: ../tests/FirstBlick.Api.Tests/TelemetryTests.cs
[invoice-rounding]: invoice-rounding.md
[collections-docs]: collections.md
[checkpoint-nine]: evidence/checkpoint9.json
[checkpoint-nine-docs]: checkpoint9.md
[ui-nine]: checkpoint8-ui.md
[ci-eight]: evidence/checkpoint8-ci.json
[asset-check]: ../scripts/production_assets.py
[container-smoke]: ../scripts/container-smoke.py
[planning-week-docs]: ../docs/planning-week.md
[planning-week-api]: ../src/FirstBlick.Api/PlanningWeekEndpoints.cs
[planning-week-ui]: ../web/src/app/pages/planning-week.component.ts
[planning-week-core-tests]: ../tests/FirstBlick.Core.Tests/Business/PlanningWeekTests.cs
[planning-week-api-tests]: ../tests/FirstBlick.Api.Tests/PlanningWeekApiTests.cs
[planning-week-client-tests]: ../web/tests/planning-week.test.ts
[planning-week-browser]: ../web/tests/browser/planning-week.spec.ts
[diary-docs]: ../docs/diary-reports.md
[diary-workflow]: ../src/FirstBlick.Core/Business/DiaryWorkflow.cs
[diary-api]: ../src/FirstBlick.Api/DiaryReportEndpoints.cs
[diary-core-tests]: ../tests/FirstBlick.Core.Tests/Business/DiaryWorkflowTests.cs
[diary-api-tests]: ../tests/FirstBlick.Api.Tests/DiaryReportApiTests.cs
[diary-browser]: ../web/tests/browser/diary-report.spec.ts
[warehouse-docs]: ../docs/warehouse-adjustments.md
[inventory-reconciliation-tests]: ../tests/FirstBlick.Core.Tests/Business/InventoryReconciliationTests.cs
[inventory-ui]: ../web/src/app/pages/inventory-control.component.ts
[inventory-browser]: ../web/tests/browser/inventory-control.spec.ts
[measurement-ui]: ../web/src/app/pages/measurements.component.ts
[measurement-browser]: ../web/tests/browser/measurement-pdf.spec.ts
[maintenance-docs]: ../docs/maintenance-workflow.md
[maintenance-workflow]: ../src/FirstBlick.Core/Business/MaintenanceWorkflow.cs
[maintenance-planner]: ../src/FirstBlick.Infrastructure/MaintenancePlanner.cs
[maintenance-api]: ../src/FirstBlick.Api/MaintenanceEndpoints.cs
[maintenance-core-tests]: ../tests/FirstBlick.Core.Tests/Business/MaintenanceWorkflowTests.cs
[maintenance-api-tests]: ../tests/FirstBlick.Api.Tests/MaintenanceWorkflowApiTests.cs
[maintenance-browser]: ../web/tests/browser/maintenance.spec.ts
[transfer-chain]: ../src/FirstBlick.Core/Business/InventoryTransferChain.cs
[transfer-chain-tests]: ../tests/FirstBlick.Core.Tests/Business/InventoryTransferChainTests.cs
[transfer-chain-api-tests]: ../tests/FirstBlick.Api.Tests/InventoryTransferChainApiTests.cs
[equipment-docs]: ../docs/equipment-inspections.md
[equipment-workflow]: ../src/FirstBlick.Core/Business/EquipmentInspectionWorkflow.cs
[equipment-api]: ../src/FirstBlick.Api/EquipmentInspectionEndpoints.cs
[equipment-tests]: ../tests/FirstBlick.Core.Tests/Business/EquipmentInspectionWorkflowTests.cs
[equipment-api-tests]: ../tests/FirstBlick.Api.Tests/EquipmentInspectionApiTests.cs
[equipment-client-tests]: ../web/tests/equipment-workflow.test.ts
[equipment-browser]: ../web/tests/browser/equipment-workflow.spec.ts
[equipment-inventory-smoke]: ../scripts/equipment_inventory_smoke.py
[migration-drill-source]: ../tools/FirstBlick.Operations/MigrationDrill.cs
[customer-contacts-docs]: ../docs/customer-contacts.md
[customer-contacts-core]: ../src/FirstBlick.Core/Business/CustomerContacts.cs
[customer-contacts-api]: ../src/FirstBlick.Api/CustomerContactEndpoints.cs
[customer-contacts-ui]: ../web/src/app/pages/customer-contacts.component.ts
[customer-contacts-tests]: ../tests/FirstBlick.Core.Tests/Business/CustomerContactTests.cs
[customer-contacts-api-tests]: ../tests/FirstBlick.Api.Tests/CustomerContactApiTests.cs
[customer-contacts-client]: ../web/src/app/core/customer-contacts.models.ts
[customer-contacts-client-tests]: ../web/tests/customer-contacts.test.ts
[customer-contacts-browser]: ../web/tests/browser/customer-contacts.spec.ts
[equipment-originals-docs]: ../docs/equipment-originals.md
[equipment-originals-api]: ../src/FirstBlick.Api/EquipmentInspectionDocumentEndpoints.cs
[equipment-originals-ui]: ../web/src/app/pages/equipment-documents.component.ts
[equipment-originals-api-tests]: ../tests/FirstBlick.Api.Tests/EquipmentInspectionDocumentApiTests.cs
[equipment-originals-client]: ../web/src/app/core/equipment-documents.models.ts
[equipment-originals-client-tests]: ../web/tests/equipment-documents.test.ts
[equipment-originals-browser]: ../web/tests/browser/equipment-documents.spec.ts
[maintenance-calendar-docs]: ../docs/maintenance-calendar.md
[maintenance-calendar-core]: ../src/FirstBlick.Core/Business/MaintenanceCalendar.cs
[maintenance-calendar-client]: ../web/src/app/core/maintenance-calendar.ts
[maintenance-calendar-tests]: ../tests/FirstBlick.Core.Tests/Business/MaintenanceCalendarTests.cs
[maintenance-calendar-client-tests]: ../web/tests/maintenance-calendar.test.ts
[migration-graph-source]: ../tools/FirstBlick.Operations/MigrationGraph.cs
[contacts-calendar-originals-smoke]: ../scripts/contacts_calendar_originals_smoke.py
[e-invoice-docs]: ../docs/e-invoice.md
[advanced-exchange-docs]: ../docs/advanced-exchange-and-physics.md
[kosit-validator]: ../src/FirstBlick.Infrastructure/Integrations/KositInvoiceValidator.cs
[official-invoice-api]: ../src/FirstBlick.Api/OfficialInvoiceEndpoints.cs
[e-invoice-output]: ../src/FirstBlick.Core/Integrations/ElectronicInvoiceOutput.cs
[official-invoice-acceptance-tests]: ../tests/FirstBlick.Api.Tests/OfficialInvoiceAcceptanceTests.cs
[official-invoice-workflow-tests]: ../tests/FirstBlick.Api.Tests/OfficialInvoiceWorkflowTests.cs
[inhomogeneous-docs]: ../docs/inhomogeneous-thermal.md
[inhomogeneous-core]: ../src/FirstBlick.Core/Technical/InhomogeneousThermalCalculator.cs
[inhomogeneous-tests]: ../tests/FirstBlick.Core.Tests/Technical/InhomogeneousThermalTests.cs
[inhomogeneous-api-tests]: ../tests/FirstBlick.Api.Tests/InhomogeneousThermalApiTests.cs
[technical-ui]: ../web/src/app/pages/technical.component.ts
[technical-client-tests]: ../web/tests/technical-workbench.test.ts
[technical-browser]: ../web/tests/browser/technical-workbench.spec.ts
[iso13788-core]: ../src/FirstBlick.Core/Technical/Iso13788Balance.cs
[iso13788-tests]: ../tests/FirstBlick.Core.Tests/Technical/Iso13788BalanceTests.cs
[advanced-technical-api-tests]: ../tests/FirstBlick.Api.Tests/AdvancedTechnicalApiTests.cs
[tapered-board-core]: ../src/FirstBlick.Core/Technical/TaperedBoardLayout.cs
[tapered-assembly-core]: ../src/FirstBlick.Core/Technical/TaperedRoofAssembly.cs
[tapered-board-tests]: ../tests/FirstBlick.Core.Tests/Technical/TaperedBoardLayoutTests.cs
[tapered-assembly-tests]: ../tests/FirstBlick.Core.Tests/Technical/TaperedRoofAssemblyTests.cs
[gaeb-catalog-api]: ../src/FirstBlick.Api/GaebCatalogEndpoints.cs
[gaeb-catalog-tests]: ../tests/FirstBlick.Api.Tests/GaebCatalogApiTests.cs
[official-gaeb-api]: ../src/FirstBlick.Api/OfficialGaebExchangeEndpoints.cs
[official-gaeb-tests]: ../tests/FirstBlick.Api.Tests/OfficialGaebExchangeTests.cs
[official-gaeb-api-tests]: ../tests/FirstBlick.Api.Tests/OfficialGaebEndpointTests.cs
[gaeb-worksheet-docs]: ../docs/gaeb-price-worksheet.md
[gaeb-worksheet-tests]: ../web/tests/gaeb-price-worksheet.test.ts
[gaeb-worksheet-browser]: ../web/tests/browser/gaeb-price-worksheet.spec.ts
[advanced-technical-ui]: ../web/src/app/pages/advanced-technical.component.ts
[customer-journal-docs]: customer-communications.md
[customer-journal-core]: ../src/FirstBlick.Core/Business/CustomerCommunications.cs
[customer-journal-api]: ../src/FirstBlick.Api/CustomerCommunicationEndpoints.cs
[customer-journal-ui]: ../web/src/app/pages/customer-journal.component.ts
[customer-journal-tests]: ../tests/FirstBlick.Core.Tests/Business/CustomerCommunicationTests.cs
[customer-journal-api-tests]: ../tests/FirstBlick.Api.Tests/CustomerCommunicationApiTests.cs
[customer-journal-client]: ../web/src/app/core/customer-journal.models.ts
[customer-journal-client-tests]: ../web/tests/customer-journal.test.ts
[customer-journal-browser]: ../web/tests/browser/customer-journal.spec.ts
[vehicle-ledger-docs]: vehicle-ledger.md
[vehicle-ledger-core]: ../src/FirstBlick.Core/Business/VehicleLedger.cs
[vehicle-ledger-api]: ../src/FirstBlick.Api/VehicleLedgerEndpoints.cs
[vehicle-ledger-ui]: ../web/src/app/pages/vehicle-ledger.component.ts
[vehicle-ledger-tests]: ../tests/FirstBlick.Core.Tests/Business/VehicleLedgerTests.cs
[vehicle-ledger-api-tests]: ../tests/FirstBlick.Api.Tests/VehicleLedgerApiTests.cs
[vehicle-ledger-client]: ../web/src/app/core/vehicle-ledger.models.ts
[vehicle-ledger-client-tests]: ../web/tests/vehicle-ledger.test.ts
[vehicle-ledger-browser]: ../web/tests/browser/vehicle-ledger.spec.ts
[customer-vehicle-ledgers-smoke]: ../scripts/customer_vehicle_ledgers_smoke.py
[project-templates-docs]: project-templates.md
[project-templates-core]: ../src/FirstBlick.Core/Business/ProjectTemplates.cs
[project-templates-api]: ../src/FirstBlick.Api/ProjectTemplateEndpoints.cs
[project-templates-ui]: ../web/src/app/pages/project-templates.component.ts
[project-templates-tests]: ../tests/FirstBlick.Core.Tests/Business/ProjectTemplateTests.cs
[project-templates-api-tests]: ../tests/FirstBlick.Api.Tests/ProjectTemplateApiTests.cs
[project-templates-client]: ../web/src/app/core/project-template.models.ts
[project-templates-client-tests]: ../web/tests/project-templates.test.ts
[project-templates-browser]: ../web/tests/browser/project-templates.spec.ts
[project-templates-migration]: ../tools/FirstBlick.Operations/MigrationProjectTemplates.cs
[ledger-originals-docs]: ledger-originals.md
[ledger-originals-api]: ../src/FirstBlick.Api/LedgerDocumentEndpoints.cs
[ledger-originals-ui]: ../web/src/app/pages/ledger-originals.component.ts
[ledger-originals-api-tests]: ../tests/FirstBlick.Api.Tests/LedgerDocumentApiTests.cs
[ledger-originals-client-tests]: ../web/tests/ledger-originals.test.ts
[ledger-originals-browser]: ../web/tests/browser/ledger-originals.spec.ts
[ledger-originals-migration]: ../tools/FirstBlick.Operations/MigrationLedgerOriginals.cs
[project-templates-originals-smoke]: ../scripts/project_templates_originals_smoke.py
[contact-history-docs]: customer-contact-history.md
[contact-history-core]: ../src/FirstBlick.Core/Business/CustomerContactHistory.cs
[contact-history-tests]: ../tests/FirstBlick.Core.Tests/Business/CustomerContactHistoryTests.cs
[contact-history-api-tests]: ../tests/FirstBlick.Api.Tests/CustomerContactHistoryApiTests.cs
[contact-history-client-tests]: ../web/tests/customer-contact-history.test.ts
[contact-history-browser]: ../web/tests/browser/customer-contact-history.spec.ts
[contact-history-migration]: ../tools/FirstBlick.Operations/MigrationContactHistory.cs
[task-dependencies-docs]: task-dependencies.md
[task-dependencies-core]: ../src/FirstBlick.Core/Business/TaskDependencies.cs
[task-dependencies-api]: ../src/FirstBlick.Api/TaskDependencyEndpoints.cs
[task-dependencies-ui]: ../web/src/app/pages/task-dependencies.component.ts
[task-dependencies-client]: ../web/src/app/core/task-dependencies.models.ts
[task-dependencies-tests]: ../tests/FirstBlick.Core.Tests/Business/TaskDependencyTests.cs
[task-dependencies-api-tests]: ../tests/FirstBlick.Api.Tests/TaskDependencyApiTests.cs
[task-dependencies-client-tests]: ../web/tests/task-dependencies.test.ts
[task-dependencies-browser]: ../web/tests/browser/task-dependencies.spec.ts
[task-dependencies-migration]: ../tools/FirstBlick.Operations/MigrationTaskDependencies.cs
[maintenance-workdays-docs]: maintenance-workdays.md
[maintenance-workdays-core]: ../src/FirstBlick.Core/Business/MaintenanceWorkdays.cs
[maintenance-workdays-client]: ../web/src/app/core/maintenance-workdays.ts
[maintenance-workdays-tests]: ../tests/FirstBlick.Core.Tests/Business/MaintenanceWorkdaysTests.cs
[maintenance-workdays-api-tests]: ../tests/FirstBlick.Api.Tests/MaintenanceWorkdaysApiTests.cs
[maintenance-workdays-client-tests]: ../web/tests/maintenance-workdays.test.ts
[maintenance-workdays-migration]: ../tools/FirstBlick.Operations/MigrationWorkdays.cs
[maintenance-ui]: ../web/src/app/pages/maintenance.component.ts
[business-activity-docs]: business-activity.md
[business-activity-core]: ../src/FirstBlick.Core/Business/BusinessActivity.cs
[business-activity-api]: ../src/FirstBlick.Api/BusinessActivityEndpoints.cs
[business-activity-ui]: ../web/src/app/pages/business-activity.component.ts
[business-activity-client]: ../web/src/app/core/business-activity.models.ts
[business-activity-tests]: ../tests/FirstBlick.Core.Tests/Business/BusinessActivityTests.cs
[business-activity-api-tests]: ../tests/FirstBlick.Api.Tests/BusinessActivityApiTests.cs
[business-activity-client-tests]: ../web/tests/business-activity.test.ts
[business-activity-browser]: ../web/tests/browser/business-activity.spec.ts
[roadmap-activity-smoke]: ../scripts/roadmap_activity_smoke.py

[roof-actions-docs]: roof-actions.md
[roof-actions-core-tests]: ../tests/FirstBlick.Core.Tests/Technical/RoofActionsTests.cs
[roof-actions-api-tests]: ../tests/FirstBlick.Api.Tests/RoofActionsApiTests.cs
[roof-actions-client-tests]: ../web/tests/roof-actions.test.ts
[roof-actions-browser]: ../web/tests/browser/roof-actions.spec.ts
[roof-actions-smoke]: ../scripts/roof_actions_smoke.py

[advanced-client-docs]: ../docs/advanced-technical-client.md
[advanced-client-contract]: ../web/src/app/core/advanced-technical.models.ts
[advanced-client-tests]: ../web/tests/advanced-technical.test.ts
[advanced-browser-tests]: ../web/tests/browser/advanced-technical.spec.ts
[advanced-fixture-contract]: ../tests/FirstBlick.Core.Tests/Technical/AdvancedClientContractTests.cs
[closeout-46]: ../docs/roadmap-closeout.md

[technical-contract-docs]: ../docs/api-calculation-contracts.md
[technical-openapi-minimum]: ../docs/contracts/technical-calculations-v1.json
[technical-openapi-tests]: ../tests/FirstBlick.Api.Tests/TechnicalOpenApiTests.cs
[advanced-wire-tests]: ../tests/FirstBlick.Api.Tests/AdvancedTechnicalWireApiTests.cs

[bounded-sources]: bounded-business-sources.md

[policy-selection-tests]: ../tests/FirstBlick.Api.Tests/RecordPolicySelectionApiTests.cs

[graph-source-tests]: ../tests/FirstBlick.Api.Tests/GraphSourceSelectionApiTests.cs
