# FirstBlick – Rework-Roadmap

> Übernommen aus der Projektanalyse „DachPlaner Rework Roadmap“, 19.10.2026 (34 Seiten). Der Originalbericht umfasst 100 Aufgaben in 15 Epics. Diese Fassung erhält alle Aufgabenkennungen und ursprünglichen Abnahmekriterien. Ein implementierter Teilablauf bedeutet nicht automatisch, dass das gesamte Epic abgeschlossen ist.

## Selbst gehostete Mehrmandantenanlage und fachliche Demo-Referenzen (23.10.2026)

Die durch den einmaligen Bootstrap gesetzte Betreiberrolle kann zusammen mit einer aktiven Geschäftsführungsmitgliedschaft einen weiteren [leeren, getrennten Betrieb](business-onboarding.md) anlegen und anschließend in ihn wechseln. Für ältere Installationen gibt es einen geschützten, einmaligen Hostbefehl zur Rollenvergabe. Die aktuellen Identity-Claims werden vor Auftrag und Replay geprüft. Der neue HTTP-/Identity-/PostgreSQL-Fall und die responsive Bedienprüfung müssen am endgültigen Commit erfolgreich ausgeführt werden. **DP-03-01 bleibt K**, weil reale Kontoübernahme, vollständige Plattformverwaltung und betriebliche Gesamtfreigabe fehlen; die 100 ursprünglichen Kriterien bleiben offen.

Alle acht benannten MF-Demos wurden lesend an Größe, Hash und allgemeinen Produktmetadaten gebunden. Die [fachliche Referenzmatrix](vendor-functional-reference.md) ordnet ausschließlich öffentlich vom Hersteller beschriebene Funktionen den vorhandenen FirstBlick-Teilmodellen und Restarbeiten zu. Die Installer wurden nicht gestartet oder ausgeschlachtet. Herstellerbeschreibungen ersetzen weder vollständige lizenzierte ISO-/DIN-Verfahren noch unabhängige Fachabnahme. Die Klassifikation **49 K / 49 T / 2 O** ändert sich dadurch nicht.

## Dezimal-Drahtvertrag und Angular-Fehlerdiagnose (23.10.2026)

**DP-02-02/DP-12-04/DP-02-07 bleiben T.** Ein [zusätzliches authentifiziertes OpenAPI-Drahtdokument](api-calculation-contracts.md) beschreibt tatsächlich ausgegebene Dezimalstrings und akzeptierte Eingaben. Der Fehlervertrag nennt `requestId`; Angular zeigt eine begrenzte serverseitige Anfragekennung. Die ursprünglichen Abnahmekriterien, Kategorien und Gesamtzählung bleiben erhalten. Ausgeführte End-to-End- und CI-Nachweise sind am endgültigen Commit zu prüfen.

## Explizites HTTPS-Proxyhosting (23.10.2026)

**DP-02-07 / DP-13-05 bleiben T.** [Proxyhosting](proxy-hosting.md) ergänzt einzeln konfigurierte vertrauenswürdige Proxyadressen, einen begrenzten Forwarded-Header-Hop, Negativprüfungen und einen ausdrücklich gewählten HTTPS-Ursprung für den bestehenden synthetischen Containerablauf. Ohne konfigurierte Gegenstelle werden keine Forwarded-Header vertraut. Ausgeführte CI-/Testhostnachweise müssen zum endgültigen Commit gehören; reale Umschaltung und fehlende externe Grundlagen bleiben offen.

## Fachliche CI-Akzeptanzfälle (23.10.2026)

Ein zusätzlicher [fachlicher P0-Regressionsblock](evidence/domain-acceptance-cases.md) prüft mit eigenständigen Zahlenorakeln Wind, Schnee, Entwässerung, Wärmeleitwiderstand sowie Teil-/Schlussrechnungen einschließlich einer referenzierten Gutschrift. Die CI führt ihn als eigens benannten, fehlerschließenden Schritt nach dem allgemeinen Serverlauf aus. **Die zugehörigen Roadmap-Einträge bleiben im bisherigen K/T/O-Status; Normen-, Steuer-, Statik- oder Realfallabnahmen sind dadurch nicht erfüllt.**

## Anfragekorrelation DP-02-07 (23.10.2026)

Die HTTP-Antworten geben eine ausschließlich serverseitig erzeugte `X-Request-Id` zurück; Problem-Details enthalten dieselbe Kennung im gesonderten Feld `requestId`. Das bestehende W3C-fähige `traceId`-Feld kann abweichen. Die [Betriebsbeobachtbarkeit](observability.md) dokumentiert Grenzen und die echte HTTP-/PostgreSQL-Regression. **DP-02-07 bleibt T:** Metrikexport, verteilte Traces, Alarmierung, SLOs und Secret-Rotation sind dadurch nicht umgesetzt. Die Zählung der 100 Aufgaben wird nicht geändert.

## Begrenzte Quellen und selektive Finanzhistorien (23.10.2026)

**DP-02-06 bleibt T.** [Quellenbudgets](bounded-business-sources.md) begrenzen ganze Mengen bereits vor der JSON-Übertragung. Zahlungen, Rücklastschriften und Bankimporte laden zugehörige Quellen statt kompletter Mandantenhistorien. Bei zu großen Mengen wird kein Teilbestand als gültiger Saldo oder Berechtigungsentscheidung verwendet. Die Fortsetzung ergänzt selektive RecordPolicy-Abfragen und ein gemeinsames Quellenbudget je DbContext. Repräsentative Last sowie Budgets für verbleibende direkte SQL-/Exportpfade bleiben offen. [PR #21](https://github.com/Cenn0/FirstBlick/pull/21) enthält die commitbezogenen Prüfungen.

## Technische API-Verträge und echte Dezimal-Ausgaben (23.10.2026)

**DP-02-02/DP-12-04 bleiben T.** [18 technische Rechenvorschauen](api-calculation-contracts.md) erhalten explizite OpenAPI-Ergebnis-/Fehlerschemas und einen HTTP-geprüften versionierten Mindestvertrag. Fehlerhafte Minimal-API-Bindung verwendet jetzt auch außerhalb der Entwicklung den vorhandenen generischen Problem-Details-Pfad. Dies ersetzt keine vollständige API-Kompatibilitätsprüfung oder Webhookzustellung.

Der zusätzliche echte HTTP-/Projektablagenvergleich für sechs synthetische Fachfälle sichert **DP-11-04/05** gegen eine zuvor in reinen Core-Fixtures unsichtbare Grenze: Vorschau-Geldwerte werden als Dezimalstrings übertragen, eingebettete technische Ergebnis-JSON kann sie als Zahlen enthalten. Ausschließlich diese benannten Geldfelder werden centgenau normalisiert; Maße und Quellen bleiben streng gebunden. Alle vier Aufgaben bleiben Teilumfänge. Endgültige CI-/Operations-/Standards-Ergebnisse sind in [PR #20](https://github.com/Cenn0/FirstBlick/pull/20) nachzuvollziehen.

## Erweiterte Fachmasken und fixierte 46er-Arbeitsmenge (23.10.2026)

**DP-11-04/05 bleiben T.** [Die erweiterten Clientverträge](advanced-technical-client.md) ergänzen Mengen-/Wasserbilanzen, Eingabe-/Quellenbindung, unveränderte Vorschau bei Projektablage, Grenzflächenhöchstmengen und eigenständige UI-Regressionen. Sie sind keine normative oder Herstellerabnahme. Die [fixierten ursprünglichen 46 Teilaufgaben](roadmap-closeout.md) erhalten ihren Nenner und benennen Software-Restarbeit sowie benötigte externe Grundlagen. Die aktuelle Zählung bleibt **49 K / 49 T / 2 O**; die ursprünglichen Abnahmekriterien werden nicht abgehakt. Tatsächliche Laufnachweise stehen am endgültigen Commit in [PR #20](https://github.com/Cenn0/FirstBlick/pull/20).

## Fortsetzung – vorgegebene Wind-, Schnee- und Abflussfälle (22.10.2026)

- **DP-11-06:** [Lokale Winddruckfälle](roof-actions.md) führen getrennte äußere/innere Bezugsdrücke und vorgegebene Beiwerte zu richtungsgebundenem Nettodruck, Sog und lokaler Normalkraft. Standort-/Zonenwahl, dynamische Antwort, Lastkombinationen, Befestiger und Standsicherheitsprüfung bleiben offen.
- **DP-11-07:** [Entwässerungsbilanzen](roof-actions.md) rechnen 1 bis 32 unabhängige Einzugsflächen mit belegten Kapazitäten, getrenntem Berechnungs-/Starkregen und ausdrücklich gewähltem Restabfluss- oder Hauptausfallszenario. Hydraulische Dimensionierung, Wasserstände, Fließweg-/Rückstauprüfung und Auswahl des anzuwendenden Regelwerks bleiben offen.
- **DP-11-08:** [Vorgegebene Schneelastfälle](roof-actions.md) rechnen explizite Form-/Expositions-/Temperaturbeiwerte und Bodenschneelast auf horizontalen Zonen eines gleichzeitigen Falls. Verwehung, Standort-/Dachformwahl, Kombinationen und tragende Konstruktion bleiben offen.

Die gemeinsame Angular-Maske bietet Quellen, bestätigte Eingaben, geprüfte Zahlen, JSON-Export und bewusste Projektablage; bestehende technische Originalakten bieten PDF-Erstellung. Core-/Client-, echte HTTP-/PostgreSQL- und Browser-Vertragstests sowie der tatsächliche API-/S3-/PDF-/Neustartablauf sind verlinkt. Deren erfolgreiche Ausführung muss am final veröffentlichten Commit belegt werden.

Die drei Aufgaben wechseln **O → T**. Aktuelle Kategorien: **49 K / 49 T / 2 O**, keine Fertigstellungsquote. DP-11-10 und DP-14-03 bleiben O. Die übrigen 97 Kategorien und alle ursprünglichen Abnahmekriterien bleiben erhalten. **Alle 100 Aufgaben sind weiterhin nicht vollständig umgesetzt oder abgenommen.** Reale Altquellen, Providerzugänge, Norm-/Herstellerdaten und unabhängige fachliche Prüfung sind nicht durch Softwaretests ersetzbar.

## Aktuelle Fortsetzung – Kontaktverlauf, Aufgabenfolge, Wartungsarbeitstage und Berichte (22.10.2026)

Die folgenden **fünf Teilfunktionen betreffen sieben ursprüngliche Aufgaben**. FirstBlick bleibt das Entwicklungsziel; die Kategorien und ursprünglichen Abnahmekriterien werden mit dieser Fortschreibung nicht neu bewertet.

- **DP-04-01 – Kontaktwerte-Historie:** Die [Änderungshistorie für Ansprechpartner](customer-contact-history.md) zeigt Anlage, Bearbeitung, Hauptkontaktwechsel, Archivierung und Wiederherstellung mit früheren und neuen Werten, damaligem Akteursnamen und Kundenrevision. Originale und Kontaktänderung werden gemeinsam gespeichert; Seitenwechsel, archivierte Kontakte, aktuelle Rechte und Replay werden berücksichtigt. Für Bestandskontakte beginnt der Nachweis bei der ersten aufgezeichneten Änderung. Die allgemeinen Kundenstammfelder erhalten dadurch keine vollständige Feldhistorie.
- **DP-04-03 / DP-04-04 – Aufgabenabhängigkeiten:** Ein [eigener Arbeitsbereich](task-dependencies.md) verbindet vorhandene Aufgaben desselben Projekts und Betriebs mit bis zu 16 unmittelbaren Vorgängern in einem begrenzten Graphen von höchstens 1.000 Aufgaben. Der Server verhindert Kreise, verfrühten Start oder Abschluss und die Rücknahme beziehungsweise Archivierung benötigter Vorgänger. Generische Aufgabenbearbeitung unterliegt denselben Regeln. Mehrstufige Arbeitspakete, automatische Terminrechnung, Personaleinteilung und Erinnerungszustellung bleiben offen.
- **DP-09-02 / DP-09-06 – Wartungswochentage und Ausnahmen:** [Vertragliche Arbeitstagsregeln](maintenance-workdays.md) führen über ausdrücklich gewählte Wochentage, Sperrtage und eine bestätigte Vorschau zu einem gesonderten Ausführungstag. Fälligkeit und Monatsendanker bleiben erhalten. Gespeicherte Termine bewahren ihre damalige Regelkopie; die aktivierte automatische Planung verwendet dieselben Regeln. Feiertage werden nicht automatisch angenommen, Personaleinsätze und Kalenderzustellung sind nicht enthalten.
- **DP-14-01 – Definierte Zeitvergleiche:** Die [Projekt- und Aufgabenstatistik](business-activity.md) vergleicht angelegte Projekte, angelegte Aufgaben und aktuell hinterlegte Aufgabenfälligkeiten für 1 bis 366 Kalendertage mit dem unmittelbar vorherigen gleich langen Zeitraum. Monatsabschnitte, Betriebszeitzone, Projekt-/Archivfilter und verlinkte Quellenrevisionen machen die Zahlen prüfbar. Das Limit von 5.000 Quellen führt bei Überschreitung zu einer Ablehnung ohne Teilsummen. Frühere Rückstände, Abschlusszahlen oder Produktivität werden daraus nicht abgeleitet.
- **DP-14-02 – Gefilterte CSV-/JSON-Berichte:** Derselbe [Berichtsarbeitsplatz](business-activity.md) exportiert die geprüften Quellen als CSV und den vollständigen Bericht als JSON. Filter, Betrieb, Quellenrevisionen und Berichtsprüfsumme binden den Download an die zuvor geladene Ansicht; die Oberfläche prüft zusätzlich die tatsächlichen Bytes. Geänderte Quellen verlangen ein erneutes Laden. CSV-Textzellen neutralisieren die erfassten führenden Formelzeichen. Frei gestaltbare Berichte und automatische Berichtszustellung bleiben offen.

Die Prüfpfade umfassen Core-/Clientfälle, echte HTTP-/Identity-/PostgreSQL-Fälle und Browser-Fixtures mit kontrollierten Antworten. Die Migrationsvarianten für [Kontakthistorien](../tools/FirstBlick.Operations/MigrationContactHistory.cs), [Aufgabenketten](../tools/FirstBlick.Operations/MigrationTaskDependencies.cs) und [Ausführungstage](../tools/FirstBlick.Operations/MigrationWorkdays.cs) sowie der [gemeinsame API-/Neustartablauf](../scripts/roadmap_activity_smoke.py) prüfen erhaltene Werte, Kennungen, Regeln und Exportbindung. Diese Quellpfade behaupten keinen erfolgreichen Lauf: CI-, Standards-, Container- und Operations-Ergebnisse müssen zum selben final veröffentlichten Commit gehören.

[Nachweismatrix](roadmap-evidence.md) und [JSON-Inventar](evidence/roadmap-matrix.json) führen dieselben sieben Ergänzungen. **DP-04-04 bleibt K; die sechs anderen Aufgaben bleiben T. Die Zählung bleibt 49 K / 46 T / 5 O.** Sie beschreibt die gelisteten Kategorien, keine Fertigstellungsquote oder Gesamtabnahme. Die übrigen 93 Matrixzeilen und alle 100 ursprünglichen Roadmap-Checkboxen bleiben unverändert. Die nachfolgenden Fortschreibungen beschreiben jeweils ihren damaligen Stand.

## Fortsetzung – Mehrflächige PV-Dachgruppe (22.10.2026)

- **DP-11-09, zusätzlicher Teilumfang:** [PV-Dachgruppen](pv-roof-set.md) berechnen bis zu acht eindeutig bezeichnete, getrennt aufgemessene rechteckige Dachflächen. Hindernisse und Raster bleiben je Dachfläche eigenständig; Summen, Einzelpositionen, Quellen und Gruppen-/Einzelprovenienz sind überprüfbar. Ein HTTP-Endpunkt, die unveränderliche technische Projektablage sowie Angular-Bedienung und Core-/API-/Client-Regressionen wurden ergänzt.
- **Grenzen:** Physische Überschneidungen mehrerer Dachflächen werden nicht erkannt; Gesamtflächen sind arithmetische Summen, kein geodätischer Nachweis. Herstellerfreigabe, Ertrag, Verschattung, PV-Elektrik, Statik, Brandschutz und Montageplanung bleiben offen. **DP-11-09 bleibt T (Teilumfang).** Erstellte Tests sind ohne erfolgreiche Ausführung auf dem endgültigen Commit kein Testnachweis.

## Neue Fortsetzung – PV-Modulbelegung als begrenzte Geometriehilfe (22.10.2026)

- **DP-11-09:** Der [PV-Rechenkern](pv-module-layout.md) erstellt für eine einzeln gemessene rechteckige Dachfläche ein begrenztes, reproduzierbares Modulraster. Berücksichtigt werden Hoch-/Querformat (auch Vergleich nach Stückzahl), Nutzer-Randabstände, Modulabstände, bis zu 64 ausgeschlossene Hindernisrechtecke und ihr Freihalteabstand. Ergebnis: Modulpositionen, Nennleistung in Wp, Ablehnungsgründe durch Hindernisse und versionsgebundene Berechnungsprovenienz.
- **Bedienung und Projektakte:** Eine eigene responsive Maske unter `/technical/pv` ermöglicht Eingabe und JSON-Export. Die unveränderliche technische Projektablage kann den Rechenlauf über `method=roof.pv-layout` aufnehmen. Core-, Client- und tatsächliche HTTP/PostgreSQL-Fälle prüfen Rechte, ungültige Geometrien und Nachweisbindung. Erfolgreiche CI-/Operations-Läufe sind am konkret veröffentlichten Commit zu belegen.
- **Unverändert offen:** Verschattung, Ertrag, Unterkonstruktion, Schnee/Wind, Elektrotechnik, Brandschutz, Herstellerverträglichkeit, Dachgenehmigungen und unabhängige Fachabnahme. Die P2-Anforderung ist **Teilumfang**, keine abgeschlossene PV-Planung.

## Aktuelle Fortsetzung – Projektvorlagen und Originalbelege (22.10.2026)

**FirstBlick bleibt das ausschließliche Entwicklungsziel.**

- **DP-04-03 / DP-04-04:** [Projektvorlagen](project-templates.md) mit geordneten Aufgaben und relativen Kalendertagen führen über eine konkrete, revisionsgebundene Vorschau zur bewussten atomaren Aufgabenanlage. Jede Anwendung erhält unveränderliche Vorlagen-/Projekt-Snapshots, Aufgabenkennungen, Herkunft und Digest. Spätere Aufgabenbearbeitung oder Vorlagenarchivierung erhalten diesen Nachweis. Mehrfachübernahmen derselben Vorlage je Projekt, veraltete Quellen und unzulässige Datumswerte werden abgewiesen. Aufgabenabhängigkeiten, Arbeitskalender, Personaleinteilung und automatische Erinnerungen bleiben offen.
- **DP-04-01 / DP-09-03 / DP-07-05:** [Originaldateien an Kommunikation und Fahrzeugkosten](ledger-originals.md) ergänzen beide Arbeitsplätze um mehrere PDF-/Bildbelege mit paginierter Liste, Wiederaufnahme und verifiziertem Download. Dateien bleiben an den unveränderten Originaldigest und eine konkrete Speicherversion gebunden. Storno oder Archivierung erhalten vorhandene Originale; weitere Uploads prüfen den aktuellen Quellzustand und die aktuellen Rechte. Automatische Postfachübernahme, OCR, Rechnungsprofilprüfung und Kostenbuchungen sind damit nicht enthalten.
- **Migration und Betrieb:** Die Migrationsprüfung verbindet jeden Anwendungsnachweis mit seinen tatsächlichen Aufgaben und prüft Dateien auf die korrekte Quelle, Version, Zeitfolge und Zugriffsgrenze. Der [Container-/S3-/Neustartcheck](../scripts/project_templates_originals_smoke.py) erzeugt die Abläufe über die tatsächliche API und vergleicht nach Neustart dieselben Aufgaben, Anwendungen, Belegversionen und Originalbytes. Die erfolgreiche Ausführung ist jeweils für den veröffentlichten Commit nachzuweisen.

[Nachweismatrix](roadmap-evidence.md) und [JSON-Inventar](evidence/roadmap-matrix.json) aktualisieren dieselben fünf Aufgaben. Ihre Kategorien bleiben unverändert: **49 K / 45 T / 6 O** zählt die vorhandenen Einträge und ist keine Fertigstellungsquote. Die ursprünglichen 100 Aufgaben und Abnahmekriterien bleiben erhalten.

## Vorherige Fortsetzung – Kommunikationsakte, Fahrten und Fahrzeugkosten (22.10.2026)

**Entwicklungsziel ist ausschließlich FirstBlick. DachPlaner bleibt Referenz.**

- **DP-04-01 / DP-09-03:** Die [Kommunikationsakte](customer-communications.md) dokumentiert Telefonate, E-Mails, Besprechungen und interne Notizen beim Kunden, optional mit Ansprechpartner und zugehörigem Projekt. Sie erhält die damaligen Bezeichnungen und unveränderlichen Originalinhalte. Korrekturen werden mit Grund, Person, Zeitpunkt und exaktem Originaldigest separat gespeichert. Verlauf, Seitenwechsel und Erfassung sind in Angular bedienbar; Zeitzone, aktuelle Rechte, Kundenrevision und Idempotenz werden geprüft. Historische Akten archivierter Kunden bleiben lesbar. Automatische Postfach-/Chatübernahme, Versand, Kundenzusammenführung und vollständige Feldänderungshistorien bleiben offen.
- **DP-07-05:** Der Arbeitsplatz [Fahrten und Fahrzeugkosten](vehicle-ledger.md) verbindet Fahrzeug-/Fahrersuche, abgeschlossene Fahrten mit Kilometerständen, belegreferenzierte EUR-Kosten, begründete Stornos und Monatsauswertung. Die gesamte aktive Historie begrenzt Kilometerfolge und Fahrzeug-/Fahrerüberschneidungen; die Monatsanzeige ist keine Prüfgrenze. Originale, historische Bezeichnungen und Stornos bleiben getrennt erhalten. Die Funktion ist eine betriebliche Aufzeichnung ohne Zusage eines steuerlich anerkannten Fahrtenbuchs oder automatischer Buchhaltung. Telematik, umfassende Kostenintegration und automatische gesetzliche Prüffristen bleiben offen.
- **Datenübernahme und Betriebsablauf:** [Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) und [Drill](../tools/FirstBlick.Operations/MigrationDrill.cs) prüfen historische Quellen, Akteure, Digests, Zeitfolge und aktive Fahrten. Der neue [Container-/Neustartablauf](../scripts/customer_vehicle_ledgers_smoke.py) erzeugt die Daten über die tatsächliche API und vergleicht danach unveränderte Originale und wirksame Summen. Core-/HTTP-/Clientfälle und responsive Browser-Fixtures ergänzen diese Prüfungen. Erfolgreiche Ausführung ist jeweils am veröffentlichten Commit durch CI, Standards und Operations zu belegen.

Die [Nachweismatrix](roadmap-evidence.md) und [JSON-Fassung](evidence/roadmap-matrix.json) aktualisieren die drei genannten Aufgaben. Ihre Gesamtklassifikation bleibt **T**. Die Inventarzählung **49 K / 45 T / 6 O** ist unverändert und bezeichnet weder eine Fertigstellungsquote noch eine neue Abnahme aller 100 Anforderungen. Ursprüngliche Aufgaben und Abnahmekriterien bleiben erhalten.

## Vorherige Fortsetzung – Kundenkontakte, Prüfungsoriginale und Wartungskalender (21.10.2026)

**Entwicklungsziel ist ausschließlich FirstBlick. DachPlaner bleibt Referenz und wird nicht weiterentwickelt.**

- **DP-04-01:** [Mehrere Ansprechpartner](customer-contacts.md) je Kunde mit Funktion, E-Mail, Telefon, Hinweisen und ausdrücklich gewähltem Hauptkontakt. Archivierung und Wiederherstellung erhalten die Kontaktidentität. Bestehende einzelne Kontaktfelder bleiben erhalten; allgemeine Kundenänderungen können die neue Kontaktliste nicht löschen. Aktuelle Rechte, Revisionen und Idempotenz werden serverseitig geprüft. Vollständige Kommunikationshistorie, Kundenzusammenführung und externe Kontaktsynchronisierung bleiben offen.
- **DP-07-05:** [Originalprüfberichte](equipment-originals.md) als PDF, PNG oder JPEG ergänzen abgeschlossene Prüfungen, Wartungen und Reparaturen. Mehrere Originale werden an den unveränderten Abschlussdigest und konkrete Speicherversionen gebunden. Upload, Wiederaufnahme und Download prüfen Dateigröße, Typ, Kennung und SHA-256. Historische und archivierte Originale bleiben erreichbar; das Anhängen verändert weder Prüfergebnis noch Gerätestand. Fahrtenbuch, Fahrzeugkosten, automatische gesetzliche Fristen und unabhängige Kalender je Prüfart bleiben offen.
- **DP-09-02:** Der [ICS-Export](maintenance-calendar.md) macht einen gespeicherten Wartungstermin als ganztägigen Fälligkeitshinweis herunterladbar. Stabile Identität, Terminrevision, korrekte Tagesgrenzen und verifizierte Bytes binden die Datei an den geöffneten Stand. Der Export enthält Titel, Datum und Status; Checklisten und interne Projektangaben bleiben im Betrieb. Kalenderabonnements, automatische Anbietersynchronisierung und Einladungszustellung bleiben offen.
- **Datenübernahme und Neustart:** Die [kanonische Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) erhält Kontakte samt historischen Akteuren und prüfungsgebundene Originale. Der [erweiterte Containerablauf](../scripts/contacts_calendar_originals_smoke.py) prüft die drei Funktionen über Identity, echte API und versionierten Objektspeicher und vergleicht nach Neustart dieselben Kennungen, Revisionen und Bytes. Die jeweiligen CI-/Operations-Läufe müssen den veröffentlichten Commit bestätigen.

Die [Nachweismatrix](roadmap-evidence.md) und [JSON-Fassung](evidence/roadmap-matrix.json) enthalten diese drei Ergänzungen. Außerdem werden überholte Offen-Angaben bei **DP-08-03, DP-11-03/04/05 und DP-12-02** an bereits vorhandene, konkret belegte Teilfunktionen angepasst. Vier Einträge wechseln deshalb von O zu T. Die daraus folgenden **49 K / 45 T / 6 O** zählen die Kategorien der aufgeführten Zeilen; sie sind keine Fertigstellungsquote und keine neue Gesamtabnahme aller 100 Anforderungen. Der historische Stand **49 K / 41 T / 10 O** bleibt ausdrücklich dokumentiert. Die ursprünglichen Aufgaben und Abnahmekriterien sind unverändert.

## Vorherige Fortsetzung – Geräteprüfungen und mehrstufige Lagerketten (21.10.2026)

**Entwicklungsziel ist ausschließlich FirstBlick. DachPlaner bleibt Referenz und wird nicht weiterentwickelt.**

- **DP-07-05 / Anbindung an DP-06-01:** Der neue [Geräteprüfungsarbeitsplatz](equipment-inspections.md) bietet Fälligkeits- und Sperrfilter, geplante Prüfung/Wartung/Reparatur, unveränderliche Ergebnis- und Absagebelege sowie manuelle Folgetermine. Negative Ergebnisse sperren neue Einsatzzuordnungen und zeigen betroffene bestehende Einsätze. Eine ausdrücklich dokumentierte positive Einsatzbeurteilung kann die Ressource wieder freigeben. Geräterevision, Vorgangsrevision, aktuelles Recht und Betriebszeitzone werden serverseitig geprüft. Damals fehlende Originalprüfbericht-Uploads werden durch die oben dokumentierte Fortsetzung ergänzt. Fahrtenbuch, Kilometerkosten und unabhängige Prüfkalender je Prüfart bleiben offen.
- **DP-07-03 / DP-07-06:** [Beleggebundene Transferketten](warehouse-adjustments.md) verbinden den ursprünglichen Wareneingang über bis zu 32 Umlagerungen mit einer Lieferantenrückgabe. Jeder Schritt benötigt eine ausdrückliche Quelle. Teilmengen, Verzweigungen, Wert, Zeitfolge, Transferpaare und freier Lagerbestand werden geprüft; fremder zusätzlicher Bestand vergrößert keine belegte Quellmenge. Die Lageroberfläche zeigt den vollständigen Pfad und seine noch nicht zugeordnete Menge. Alte direkte Transferbelege und deren Digests bleiben kompatibel; eine nachträglich erfundene Herkunft wird nicht ergänzt.
- **Durchgängige Betriebsprüfung:** Der [Containerablauf](../scripts/equipment_inventory_smoke.py) prüft beide Funktionen über die echte API und liest ihre Nachweise nach dem Neustart erneut. Der [Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs) enthält vollständige Lager- und Prüfakten sowie negative Varianten. Die Migrationsprüfung akzeptiert jetzt gültige Lieferanten-/Baustellenrückgaben und prüft deren Originalbezug; bisher wären diese vorhandenen Buchungsarten dort fälschlich abgewiesen worden.

Die [Nachweismatrix](roadmap-evidence.md) und [JSON-Fassung](evidence/roadmap-matrix.json) sind für diese drei Aufgaben synchronisiert. Die Erweiterung umfasst Core, HTTP-/PostgreSQL-Verträge, Angular-Bedienung und passende Regressionen. Ein bestandener Ausführungsnachweis gilt jeweils für den konkret geprüften Commit; Testdateien allein behaupten keinen erfolgreichen Lauf. Die ursprünglichen 100 Aufgaben und ihre Abnahmekriterien bleiben erhalten.

## Vorherige Fortsetzung – Wochenplanung, Tagesrapporte, Lager und Wartungsfolge (21.10.2026)

Die folgenden durchgängigen Abläufe ergänzen die bestehenden Fachkerne. Die [Nachweismatrix](roadmap-evidence.md) und ihre [maschinenlesbare Fassung](evidence/roadmap-matrix.json) führen für die acht berührten Aufgaben dieselben aktuellen Quell-/Testbelege und Restgrenzen.

- **DP-06-01:** Der [Wochenarbeitsplatz](planning-week.md) verbindet eine begrenzte serverseitige Wochenprojektion mit einem responsiven Kalender, Ressourcenfiltern sowie Anlegen, Bearbeiten und Absagen von Einsätzen. Anzeige und Eingabe verwenden die Betriebszeitzone; Zeitumstellung, nicht existierende und doppelte Ortszeiten werden ausdrücklich behandelt. Mitarbeiter-/Gerätekollisionen, Abwesenheiten, Projektfenster, Rechte und Revisionen bleiben serverseitig verbindlich. Routenoptimierung, Fahrzeit-/Tarifbewertung und eine reale betriebliche Planungsabnahme bleiben offen.
- **DP-06-03:** [Tagesrapporte](diary-reports.md) führen vom bearbeitbaren Entwurf über ausdrücklich eingereichte, eingefrorene Inhalte zur unabhängigen Prüfung durch Bauleitung oder Geschäftsführung. Rückgaben benötigen einen Grund und erlauben Korrektur mit erneuter Einreichung. Erst freigegebene Berichte erzeugen ein mehrseitiges, quellengebundenes PDF mit verifizierten Downloadbytes. Diese interne Prüfung ist keine Kundenabnahme, digitale Unterschrift oder Zusage rechtssicherer Archivierung; der reale Baustellentag bleibt fachlich abzunehmen.
- **DP-07-03 / DP-07-04 / DP-07-06:** Die [Lagerkorrekturen](warehouse-adjustments.md) umfassen belegte Inventurdifferenzen, Umlagerungen mit bestätigtem Ausgangsbestand und originalgebundene Lieferanten-/Baustellenrückgaben. Projekt-Rückgaben korrigieren Verbrauchs- und Freigabezähler der genau zugehörigen Reservierung atomar. Lieferantenretouren aus einem anderen Lager benötigen das nachgewiesene direkte Transferpaar und bleiben durch Originalzugang, Transferrest und freien Bestand begrenzt. Bestellstatus, Ersatzlieferungen, paginiertes Journal und Angular-Lagerprüfung bilden einen gemeinsamen Ablauf. Mehrstufige Transferketten, Chargenbewertung, Lieferantengutschriften und reale Inventur-/Mehrlagerabnahmen bleiben offen.
- **DP-10-03:** Die Aufmaßakte bietet für festgehaltene Aufmaße einen PDF-Download mit Prüfung von Projekt, Ursprungsobjekt, eingefrorenem Digest, MIME-Typ, Dateigröße, PDF-Kennung und SHA-256. Verspätete Antworten nach Betriebs-, Rechte- oder Seitenwechsel werden auch nach der asynchronen Hashprüfung verworfen. Geländeaufnahme, vertragliche Abzugsregeln und unabhängige fachliche Abnahme bleiben offen.
- **DP-09-02 / DP-09-06:** Die [Wartungsfolge](maintenance-workflow.md) verbindet eine ausdrücklich konfigurierte Vertragscheckliste mit manueller oder aktivierter automatischer Anlage eines Wartungsprojekts und eingefrorenen Termins. Zugewiesene Mitarbeitende dokumentieren Prüfergebnisse; der Managementabschluss schreibt Projekt, Termin und Vertragsfolgetermin gemeinsam fort. Monatsendanker, Vertragsende, konkurrierende Scheduler und aktuelle Berechtigungen werden berücksichtigt. Kaufmännische Auftragserzeugung, externe Kalender-/Benachrichtigungszustellung und reale fachliche Wartungsabnahme sind nicht enthalten; die Wartungsfolge ersetzt keinen Provider-Nachweis des Morgenbriefings.

Die verlinkten Tests unterscheiden Core-/Clientfälle, echte HTTP-/Identity-/PostgreSQL-Fälle und Browser-Fixtures mit kontrollierten HTTP-Antworten. Lokale Läufe der einzelnen Arbeitsstände und frühere PR-Prüfungen ergeben keinen gemeinsamen CI-/Standards-/Operations-Nachweis des final zusammengeführten Commits; dessen Ausführung ist gesondert zu belegen. Aus dieser Dokumentation wird keine gemeinsame Testgesamtzahl abgeleitet.

Die ältere **49 K / 41 T / 10 O**-Inventur bezeichnet historische Einstufungen technischer Kerne, keine fertig abgenommenen Anforderungen. In dieser damaligen Ergänzung blieben die Kategorien und die 100 ursprünglichen Roadmap-Checkboxen unverändert. Sie aktualisierte die konkreten Fähigkeiten und Grenzen der acht genannten Aufgaben, ohne sämtliche ursprünglichen Abnahmekriterien neu zu bewerten oder einen Fertigstellungsprozentsatz zu behaupten. Die spätere begrenzte Korrektur von vier überholten Offen-Einstufungen steht im aktuellen Abschnitt oben.

## Fortsetzung – inhomogene Wärmedämmaufbauten (21.10.2026)

**DP-11-03 / M5, technischer Teilumfang:** Die [Widerstandsgrenzen für Sparrendächer](inhomogeneous-thermal.md)
ergänzen unabhängige Parallelpfade um eine obere und untere Wärmewiderstandsabschätzung
für ausgerichtete, ausdrücklich nichtmetallische Schichten. Neu sind die Core-Berechnung,
`POST /api/v1/technical/thermal/inhomogeneous`, persistierbare
`technical/runs` (`method=thermal.inhomogeneous`), unabhängige Zahlenbeispiele,
Grenzfall- und HTTP-/PostgreSQL-Tests. Die Angular-Fachmaske für getrennte Flächenabschnitte, eigene Materialpfade, Flächenanteile, Quellen und Ausschluss metallischer Wärmebrücken wurde ergänzt. Sie verwendet den vorhandenen API-Endpunkt und die dokumentierte Projektablage; clientseitige Feld-, Schichtgrenzen- und Provenienzprüfungen sichern diese Teilfunktion zusätzlich ab. Luftschichtkorrekturen, mehrdimensionale Brückenberechnungen und eine unabhängige normative Fachabnahme bleiben offen.
Die ursprünglich unverändert beibehaltenen 100 Roadmap-Checkboxen sind **keine**
aktuelle Fertigstellungsstatistik.

## Umsetzung der geprüften Fachmodule (21.10.2026)

Die [erweiterte Fach- und Schnittstellendokumentation](advanced-exchange-and-physics.md) beschreibt die neuen, separat überprüfbaren Abläufe und Grenzen:

- **DP-08-03:** Geprüftes XRechnung-XML anhand der gepinnten KoSIT-Konfiguration, manuell bestätigte Lieferantenrechnung mit Original-/Prüfbericht-/Prüfbelegversionen, Freigabe-/Zahlungsabgleich sowie validierter UBL-Rechnungsausgabe aus dem eingefrorenen Ist-Rechnungsstand. Keine automatische Banküberweisung, Peppol-Zustellung oder generelle ZUGFeRD-/DATEV-Freigabe.
- **DP-12-02:** GAEB-X83/X84 anhand der originalen XSD 3.3/2021-05 prüfen; Angebote mit quell- und projektgebundener Prüffreigabe als X84 mit archiviertem X83-Original und eigener, gegen Datenbank-Fremdschlüssel referenzierter Exportakte ausgeben. Nur reguläre Positionen; phasenbedingt nicht darstellbare Freitexte sind ausdrücklich ausgewiesen.
- **DP-11-04:** Monatsbilanzen für Kondensation/Verdunstung und mehrjährige Austrocknungsvergleiche nach dem bezeichneten ISO-13788:2012-Verfahren, mit Anwendungsgrenzen, Klimanachweisen und prüfsummengebundenen Projektläufen. Kein umfassender Nachweis der DIN 4108-3 oder unabhängige Fachabnahme; der dokumentierte Referenzwiderspruch bleibt offen.
- **DP-11-05:** Rechteckiges, einachsiges Gefälleplattenraster mit expliziten Hersteller-/Katalogversions-/SKU-Eingaben, Stücklisten, Packmengen und Verschnitt. Kein bestätigter Herstellerkatalog, keine Innenentwässerung, keine normative Verlegefreigabe.

Diese Fähigkeiten sind **technisch implementierte Teilumfänge**, keine pauschale Erledigung sämtlicher ursprünglicher Aufgaben und keine Zusage umfassender Norm-/Herstellerkonformität. Maßgeblich sind die **CI-, Standards- und Operations-Prüfergebnisse desselben finalen GitHub-Commits**. Ältere Checkpoints behalten ihre historische Aussage zum jeweiligen Quellstand; nachfolgende Abschnitte dokumentieren den früheren Stand.

## Weiterführung – Originale XML-Akten

Die [Originalakten-Dokumentation](xml-originals.md) ergänzt **DP-08-03, DP-12-02 und DP-02-05** um einen zusammenhängenden Ablauf: strukturell geprüfte Originaldatei → bewusste Ablage → konkrete S3-Version und Eingangsbeleg → mandantengebundene Dublettenprüfung → wiederauffindbare Akte → erneut verifizierter Download oder Wiederaufnahme im Prüfbereich. GAEB-Akten zeigen die tatsächlich daraus übernommenen Katalogpositionen; ungespeicherte Kalkulationswerte werden bei der Wiederaufnahme nicht ohne Bestätigung verworfen.

Die Implementierung umfasst Backend, responsive Angular-Bedienung, Core-/HTTP-/PostgreSQL-Regressionen, Browser-Vertragstests und einen erweiterten tatsächlichen Containerablauf mit PostgreSQL/S3, Sicherung und Neustart. Die zugehörigen Ausführungsergebnisse werden für den konkret veröffentlichten Commit in [Folge-PR #2](https://github.com/Cenn0/FirstBlick/pull/2) nachgewiesen. Ein vorhandener Test oder Workflow ist für sich kein bestandener Lauf.

Die Dateiaufbewahrung **bucht keine Rechnung**, erzeugt ohne gesonderte bestätigte Katalogübernahme keine Leistung und behauptet weder vollständige Profil-/XSD-/Schematron-Konformität noch ein zertifiziertes Langzeitarchiv. Die weiter erforderliche vollständige E-Rechnungs- und GAEB-Verarbeitung bleibt offen. Die untenstehenden ursprünglichen Aufgaben werden deshalb nicht pauschal abgehakt; die historischen Gesamtzählungen werden durch diesen Teilumfang nicht in einen neuen Fertigstellungsprozentsatz umgedeutet.

## Vorheriger Arbeitsstand – Checkpoint 13

[Checkpoint 13](checkpoint13.md) beschreibt die neuen durchgängigen Bedienabläufe und ihre konkreten Testbelege. Die [Bedienungs- und Vertragsdokumentation](calculation-and-trade-workbenches.md) erklärt Eingaben, Speicherung, Importbestätigung und bewusst nicht unterstützte Fälle.

- **DP-11-02/03/04/05:** Fünf vorhandene Rechenverfahren sind über einen gemeinsamen Angular-Arbeitsplatz bedienbar: Dachgeometrie, Wärme, Einzelklima- und Zwölfmonats-Feuchtescreening sowie lineare Gefälledämmung. Ergebnisprüfung, ausdrückliche Projektablage, JSON-Export und quellen-/prüfsummengebundener PDF-Download bilden einen zusammenhängenden Ablauf.
- **DP-12-02:** GAEB-Datei → unveränderte Originalbytes → Positionsauswahl → ausdrücklich eingegebene eigene Kosten → geprüfte Zuordnung → bestätigte atomare Katalogübernahme. Der Prüfstand ist serverseitig an den Betrieb gebunden; fehlende Kostenfelder werden nicht als Nullwerte erfunden.
- **DP-08-03:** Eine lesende UBL-/CII-XML-Oberfläche zeigt Angaben, Positionen und Prüfhinweise. Eine vollständige Profilvalidierung, steuerliche Verarbeitung oder strukturierte Ausgabe ist weiterhin nicht vorhanden.
- **DP-03-06/07 und DP-06-08:** Zusätzliche Browserfälle decken Desktop, Tablet und Mobil, Betriebswechsel, verspätete Antworten, Rechteentzug, fehlerhafte Antworten und explizite Bestätigung ab. Die neuen HTTP-/PostgreSQL-Fälle prüfen reale serverseitige Mandanten-, Pflichtfeld- und Transaktionsgrenzen.

Die [ältere vollständige Nachweismatrix](roadmap-evidence.md) bleibt als historisches Inventar erhalten. **Für die hier berührten Aufgaben ergänzt und berichtigt die Tabelle in Checkpoint 13 den älteren Stand**, insbesondere die früher als vollständig offen beschriebenen XML-/Feuchte-Teilfunktionen. Die historischen Kategorien 49 K / 42 T / 9 O werden nicht als aktualisierte Gesamtzählung oder Fertigstellungsprozentsatz ausgegeben. Die gesamte 100-Aufgaben-Matrix wurde in diesem Checkpoint nicht neu abgenommen.

[Checkpoint 9](checkpoint9.md) dokumentiert den damaligen Gesamtstand; [Checkpoint 10](checkpoint10.md) ergänzt begrenzte Rechnungsquellen und versionierte Kostenprognosen; [Checkpoint 11](checkpoint11.md) ergänzt den Null-Euro-Mengenabschluss und abgesicherte Kostenquellen. Die [lineare Gefälledämmungs-Mengenhilfe](tapered-insulation.md) bleibt von einer herstellergebundenen Verlegeplanung getrennt. Jeder Testbeleg gilt ausschließlich für seinen angegebenen Quellstand.

Weiter offen bleiben vollständige E-Rechnungsprofile und Import-/Exportketten, normative Feuchteschutzverfahren, Hersteller-Verlegeplanung, weitere technische Fachmodule, automatisierter Providerbetrieb, repräsentative Last-/SLO-Nachweise und reale Migrationsquellen. Eine erfolgreiche Softwareprüfung ersetzt keine unabhängige Prüfung der Fachverfahren. Open-Source-Weiterentwicklung ist kein formaler Produktionsfreigabeprozess.

## Zielarchitektur

- **Frontend:** Angular 22, responsive PWA, selektive Offline-Daten in IndexedDB
- **Backend:** C# / ASP.NET Core 10, modularer Monolith mit versionierter OpenAPI
- **Daten:** PostgreSQL, EF Core, mandantenbezogene Schlüssel und Transaktionen
- **Dateien:** S3-kompatibler Objektspeicher mit Versionierung, Prüfsummen und Zugriffskontrolle
- **Hintergrundarbeit:** separater .NET Worker, transaktionale Outbox, Leasing und Klärungszustände
- **Sicherheit:** persönliche Konten, serverseitige Rollen/Fähigkeiten, HttpOnly-Cookies, CSRF, Audit und Vieraugenfreigabe
- **Betrieb:** reproduzierbare CI, Backups und Restore, Migration aus D1/R2, dokumentierter Rollout

## Meilensteine

| Stufe | Inhalt | Abnahme |
|---|---|---|
| M0 | Ausgangsbasis, Anforderungen, Fachverfahren | Quellstand, Dateninventar und Referenzfälle geklärt |
| M1 | Verlässlicher Kern, DP-01/02 und Identität | Builds, Regressionen, Berechtigungen, Audit und Jobs geprüft |
| M2 | Kunden/Objekte, Angebote, Aufträge und Aufmaß | Anfrage bis angenommenem Auftrag und Nachtrag |
| M3 | Disposition, Zeiten, Material und Offline | Reproduzierbarer Baustellenarbeitstag inklusive Neustart |
| M4 | Rechnungen, Zahlungen, Portal und Wartung | Abrechnung und lesender Zahlungsabgleich |
| M5 | Dachmodell und technische Fachmodule | Unabhängig geprüfte Flach-/Steildach-Referenzfälle |
| M6 | Migration, Restore und Gesamtabnahme | Daten/Dokumente übernommen, Endabläufe nachgewiesen |

## Vollständige Aufgabenstruktur

### DP-00 – Ausgangsbasis und Produktumfang (P0)
- [ ] DP-00-01 Aktuellen Quellstand zusammenführen
- [ ] DP-00-02 Daten und Dateien inventarisieren
- [ ] DP-00-03 Verbindliche Anforderungsmatrix erstellen
- [ ] DP-00-04 Drei reale Betriebsabläufe modellieren (Sanierung, Reparatur, Wartung)
- [ ] DP-00-05 Projektregeln und Entscheidungen dokumentieren

### DP-01 – Nachgewiesene Fehler und verlässliche Entwicklung (P0/P1)
- [ ] DP-01-01 Reproduzierbare Ausgangsbasis und neue CI herstellen
- [ ] DP-01-02 Aktivierung und Kontowiederherstellung trennen
- [ ] DP-01-03 Angebotsstatus beim Versand erhalten
- [ ] DP-01-04 Effektive Kalkulationswerte einfrieren
- [ ] DP-01-05 Eingabe- und Speicherbudgets abstimmen
- [ ] DP-01-06 Unklare Versandvorgänge auflösbar machen
- [ ] DP-01-07 Planungsinvarianten in beide Richtungen prüfen
- [ ] DP-01-08 Finale Sicherheitsheader prüfen

### DP-02 – Modularer Kern, Daten und Hintergrundarbeit (P1)
- [ ] DP-02-01 C#-/Angular-Serverbasis aufsetzen
- [ ] DP-02-02 C#-Fachmodule und OpenAPI-Vertrag einführen
- [ ] DP-02-03 PostgreSQL und EF-Core-Migrationen strukturieren
- [ ] DP-02-04 .NET-Worker, Outbox und atomaren Audit aufbauen
- [ ] DP-02-05 C#-Dokumentendienst und Dateispeicher entwickeln
- [ ] DP-02-06 API-Abfragen und Angular-Datenzugriff konsolidieren
- [ ] DP-02-07 Serverkonfiguration, Schlüssel und Beobachtbarkeit schaffen

### DP-03 – Identität, Freigaben, Module und Bediengrundlage (P1)
- [ ] DP-03-01 ASP.NET-Identität und Betriebsmitgliedschaften trennen
- [ ] DP-03-02 Funktionsrechte serverseitig ausdrücken
- [ ] DP-03-03 Mehrere Geschäftsführer und Delegation ermöglichen
- [ ] DP-03-04 Konfigurierbare Vieraugenfreigabe implementieren
- [ ] DP-03-05 Module pro Betrieb steuern
- [ ] DP-03-06 Angular-Oberfläche mit Routen und Designsystem aufbauen
- [ ] DP-03-07 Berechtigungsmatrix als Prüfsuite verankern
- [ ] DP-03-08 Externe Partner gezielt beteiligen

### DP-04 – Kunden, Gebäude, Dach und Projektakte (P1)
- [ ] DP-04-01 CRM und Ansprechpartner ausbauen
- [ ] DP-04-02 Gebäude und dauerhafte Dachakte einführen
- [ ] DP-04-03 Projekte und Arbeitsstruktur modellieren
- [ ] DP-04-04 Aufgaben, Fristen und Verlauf integrieren
- [ ] DP-04-05 Allgemeine Projektunterlagen bereitstellen
- [ ] DP-04-06 Übergreifende Suche und Archivierung ergänzen

### DP-05 – Kalkulation, Angebot, Auftrag und Nachtrag (P1)
- [ ] DP-05-01 Kalkulations- und Leistungskatalog konsolidieren
- [ ] DP-05-02 Angebots- und Vertragszustände trennen
- [ ] DP-05-03 Professionelle Angebotsbearbeitung ermöglichen
- [ ] DP-05-04 Auftrag aus angenommener Version erzeugen
- [ ] DP-05-05 Nachträge und Leistungsänderungen führen
- [ ] DP-05-06 Soll, Ist und Prognose vergleichen
- [ ] DP-05-07 Kaufmännischen Referenzablauf absichern

### DP-06 – Planung, Personal, Baustellenarbeit und Offline (P1)
- [ ] DP-06-01 Disposition mit Zeitfenstern und Ressourcen aufbauen
- [ ] DP-06-02 Zeiten, Pausen und Tätigkeiten erfassen
- [ ] DP-06-03 Bautagebuch und Rapporte erstellen
- [ ] DP-06-04 Abnahme, Mangel und Nacharbeit führen
- [ ] DP-06-05 Personal, Urlaub und Abwesenheit ergänzen
- [ ] DP-06-06 Ausbildungsmodul umsetzen
- [ ] DP-06-07 Angular-PWA und dauerhafte Offline-Synchronisierung bauen
- [ ] DP-06-08 Mobile und Tablet-Abläufe praktisch prüfen
- [ ] DP-06-09 Eigene Formulare konfigurieren

### DP-07 – Einkauf, Lager, Material, Geräte und Fahrzeuge (P1/P2)
- [ ] DP-07-01 Lieferanten und Einkaufsstammdaten modellieren
- [ ] DP-07-02 Bedarf in geprüfte Bestellung überführen
- [ ] DP-07-03 Wareneingang und Bestandsbewegungen erfassen
- [ ] DP-07-04 Projektverbrauch und Reservierung verbinden
- [ ] DP-07-05 Geräte und Fahrzeuge verwalten
- [ ] DP-07-06 Einkaufsablauf und Differenzen absichern

### DP-08 – Rechnungen, offene Posten und lesendes Banking (P1)
- [ ] DP-08-01 Beleg- und Steuerregeln spezifizieren
- [ ] DP-08-02 Ausgangsrechnungen und Korrekturen umsetzen
- [ ] DP-08-03 Strukturierte E-Rechnungen verarbeiten
- [ ] DP-08-04 Offene Posten und Mahnwesen führen
- [ ] DP-08-05 Eingangsbelege prüfen und freigeben
- [ ] DP-08-06 Bankdaten ausschließlich lesend anbinden
- [ ] DP-08-07 Zahlungsabgleich und Rücknahme implementieren
- [ ] DP-08-08 Finanzablauf und Exporte fachlich prüfen

### DP-09 – Kundenportal, Wartung und persönliche Kommunikation (P1/P2)
- [ ] DP-09-01 Kundenportal mit freigegebenen Inhalten aufbauen
- [ ] DP-09-02 Wartungsverträge und wiederkehrende Termine führen
- [ ] DP-09-03 Kommunikation an Vorgänge binden
- [ ] DP-09-04 Persönliche Benachrichtigungsregeln speichern
- [ ] DP-09-05 Push, E-Mail und WhatsApp anbinden
- [ ] DP-09-06 Morgenbriefing und Wartungsablauf absichern

### DP-10 – Dachmodell und Aufmaß (P1/P2)
- [ ] DP-10-01 Fachmodell, Einheiten und Revisionen
- [ ] DP-10-02 Einfache Dachgeometrie und Mengenkern
- [ ] DP-10-03 Aufmaß erfassen und nachweisen
- [ ] DP-10-04 Dachdetails und technische Bauteile
- [ ] DP-10-05 Externe Aufmaße und Planunterlagen
- [ ] DP-10-06 Mengen, Material und Leistungsverzeichnis verbinden

### DP-11 – Technische Fachmodule (P2; DP-11-01 früh P0)
- [ ] DP-11-01 Verfahren, Datenstände und Nutzungsrechte klären
- [ ] DP-11-02 Rechenkern und nachvollziehbare Ergebnisberichte
- [ ] DP-11-03 U-Wert und thermische Aufbauten
- [ ] DP-11-04 Feuchteschutz und Verfahrensgrenzen
- [ ] DP-11-05 Gefälledämmung und Verlegeplanung
- [ ] DP-11-06 Windlast und Befestigung
- [ ] DP-11-07 Haupt- und Notentwässerung
- [ ] DP-11-08 Steildach, Schnee und konstruktive Nachweise
- [ ] DP-11-09 Photovoltaik als Dachplanungsmodul
- [ ] DP-11-10 Fachsoftwareintegration und Gesamtprüfung

### DP-12 – Schnittstellen und Datenübernahme (P1/P2)
- [ ] DP-12-01 Integrationsverträge und Adapterbasis definieren
- [ ] DP-12-02 GAEB und Material-/Leistungskataloge anbinden
- [ ] DP-12-03 Buchhaltungs- und Lohnexporte umsetzen
- [ ] DP-12-04 Dokumentierte API und Ereignisse bereitstellen
- [ ] DP-12-05 Format- und Anbieterwartung etablieren

### DP-13 – Migration, Sicherung und dauerhafter Betrieb (P1)
- [ ] DP-13-01 Backup und vollständigen Restore implementieren
- [ ] DP-13-02 Datenschutz und Datenlebenszyklus ausarbeiten
- [ ] DP-13-03 Last, Kosten und Betriebsfehler messen
- [ ] DP-13-04 Wiederholbare Datenmigration bauen
- [ ] DP-13-05 Umschaltung und Rückkehr proben
- [ ] DP-13-06 Gesamtfälle und Wartungsdokumentation abschließen

### DP-14 – Kennzahlen und überprüfbare Assistenz (P2/P3)
- [ ] DP-14-01 Kennzahlen definieren und verlinken
- [ ] DP-14-02 Auswertungen und Datenexport ergänzen
- [ ] DP-14-03 Optionale Assistenz kontrolliert integrieren

## Fachliche und technische Abnahmebedingungen

- **Mandanten:** Zwei Betriebe, getrennte Daten, mehrere GF, Rollen, Module, Widerruf.
- **Vieraugenregel:** Keine Selbstfreigabe; Bindung an Objekt, Revision und Inhaltsdigest.
- **Dachsanierung:** Kunde → Dachrevision → Aufmaß → Angebot → Annahme → Auftrag → Nachtrag → Baustelle → Abnahme → Rechnung → Zahlung.
- **Reparatur/Wartung:** Wiederverwendung der Dachakte über mehrere Projekte.
- **Offline:** ganzer Arbeitstag einschließlich Neustart, Berechtigungsänderung und konfliktfreier Synchronisierung.
- **Finanzen:** Teil-/Sammelzahlungen, Skonto, Einbehalt, Korrekturen, Rücklastschriften; Banking strikt lesend.
- **Fachberechnung:** geprüfte Norm-/Datenversionen, unabhängige Referenzfälle und dokumentierte Anwendungsgrenzen; keine ungeprüften Ergebnisse als Nachweis ausgeben.
- **Betrieb:** Fehlerfälle, vollständiger Restore, reproduzierbare CI und reale Desktop-/Tablet-/Mobilprüfungen.

## Kritische Grenzen und offene Entscheidungen

1. Der historische Quellcode vom 07.09.2026 ist nicht nachweislich identisch mit dem zuletzt gehosteten Stand.
2. Reale D1/R2-Daten, Dokumente, Kennwörter und Konfiguration gehören **nicht** in dieses öffentliche Repository.
3. Die Open-Source-Lizenz ist vom Rechteinhaber festzulegen; nicht automatisch annehmen.
4. Normdaten, Herstellerkennwerte, Fachprüfung und Nutzungsrechte sind vor normativen Nachweisen zu klären.
5. E-Rechnungsprofile, Steuerfälle, DATEV-/GAEB-/Bank-/WhatsApp-Verträge müssen versionsbezogen geprüft werden.
6. Ein grüner Build allein ist kein Nachweis vollständiger Fachkonformität.

## Historische erste Reihenfolge

DP-00-01 bis 03, DP-01-01 und DP-11-01 beginnen; danach DP-01-02 bis 06 schließen. Parallel DP-02-01 (Serverbasis), Identität und Dokumentmodell vorbereiten. Erst nach überprüften Grundlagen Finanz- und Freigabeabläufe ausweiten. Der aktuelle Entwicklungsstand steht oben und in den Checkpoints; diese ursprüngliche Reihenfolge behauptet nicht, dass bereits implementierte Grundlagen erneut fehlen.

**Quelle:** DachPlaner – Projektanalyse und Rework Roadmap, 19.10.2026, insbesondere Seiten 17–32. Diese Markdown-Fassung übernimmt Aufgabenkennungen und Titel und verdichtet die Abnahmebedingungen. Für die vollständigen individuellen Aufgabenbeschreibungen gilt der Originalbericht.
