# Nachweismatrix für alle 100 Roadmap-Aufgaben

Historischer Klassifikationsstand: 20.10.2026. Aktuelle Umfangsergänzung: 21.10.2026 für **DP-06-01, DP-06-03, DP-07-03, DP-07-04, DP-07-06, DP-09-02, DP-09-06 und DP-10-03**. Diese Quell-/Nachweissichtung übernimmt alle 100 Kennungen und Titel aus [ROADMAP.md](ROADMAP.md). Die acht genannten Zeilen und die [maschinenlesbare Matrix](evidence/roadmap-matrix.json) benennen die ergänzte Implementierung, ihre Tests und die verbleibenden Grenzen. Die übrigen Zeilen behalten ihren jeweils angegebenen historischen Nachweisstand. Keine Roadmap-Checkbox, K/T/O-Einstufung oder ursprüngliche Abnahmebedingung wird dadurch verändert.

## Bedeutung der Statusangaben

| Status | Bedeutung |
|---|---|
| **K – Kern belegt** | Der in der Zeile präzise genannte technische Kern ist implementiert und durch Quell-/Testbelege gestützt. Der Rest in der rechten Spalte bleibt offen. Das bedeutet ausdrücklich keine vollständige ursprüngliche Fach- oder Produktivabnahme. |
| **T – Teilumfang** | Es gibt eine funktionierende Grundlage oder begrenzte Nachweise; wesentliche Funktionen, Integration oder der passende Gesamtnachweis fehlen noch. |
| **O – Offen** | Für die verlangte Fachfunktion ist keine akzeptierbare Implementierung vorhanden. Beschreibende Felder, Schnittstellennamen oder Ablehnung unsupported Inputs zählen nicht als diese Funktion. |

Die unveränderte historische Zählung ist eine Inventarübersicht, kein Fertigstellungsprozentsatz: **49 K, 41 T, 10 O = 100 Aufgaben**. Sie ist keine erneute Einstufung aller Aufgaben nach den Ergänzungen vom 21.10.2026. Kein Eintrag behauptet eine normative Zertifizierung, einen erfolgten Produktionswechsel oder eine externe Anbieterabnahme.

## Aktuelle Ergänzung – 21.10.2026

Die acht betroffenen Zeilen werden um den [Wochenarbeitsplatz][planning-week-docs], die [geprüften Tagesrapporte][diary-docs], die [Lagerkorrekturen][warehouse-docs], den verifizierten Aufmaß-PDF-Download und die [Wartungsfolge][maintenance-docs] ergänzt. Sie beschreiben jeweils den technischen Umfang und seine verbleibenden Grenzen. Projekt-Rücklieferungen aktualisieren jetzt auch die zugehörigen Reservierungszähler; deshalb ist **DP-07-04** ausdrücklich Bestandteil dieser Ergänzung. Lieferantenretouren nach einem Lagerwechsel sind auf eine nachgewiesene direkte Umlagerung begrenzt.

Die verlinkten Testdateien unterscheiden Core-/Clientprüfungen, echte HTTP-/Identity-/PostgreSQL-Fälle und responsive Browser-Fixtures. Die Browser-Fixtures verwenden kontrollierte HTTP-Antworten; die Rapport-API-Tests verwenden einen deterministischen, versionierten Objektspeicher als Testdouble. Diese Nachweise dürfen nicht als zusätzlicher echter Browser-/S3-Betriebsdurchlauf gezählt werden. Vorhandene Testfälle und getrennte lokale Ergebnisse behaupten keinen gemeinsamen erfolgreichen CI-/Standards-/Operations-Lauf des final zusammengeführten Commits. Dafür ist dessen eigener Ausführungsnachweis erforderlich; eine neue Gesamtzahl wird hier nicht gebildet.

## Gelesene Ausführungsnachweise

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
| **DP-00-02** Daten und Dateien inventarisieren | **T** | [Baseline][baseline], [Methodeninventar][methods] und [damaliges Anhanginventar][attachments-eight] erfassen Quellgrenzen sowie sechs bei der ursprünglichen statischen Inventur mit PE-Header/SHA-256 geprüfte Installer. Für `flachdach-demo.exe` und `steildach-demo.exe` liegen im jüngeren Projektkontext zusätzliche Anhänge vor; deren Byte-/Methodenprüfung gehört **nicht** zu diesem Inventarnachweis. | Produktive D1-/R2-Exporte und reale Datensatz-/Dokumentvollständigkeit fehlen. Beide nachträglich angehängten Demos müssen erst sicher inventarisiert und fachlich analysiert werden; frühere filename- oder hashbasierte Flachdach-Beobachtungen sind kein aktueller Analysenachweis. |
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
| **DP-02-02** C#-Fachmodule und OpenAPI-Vertrag einführen | **T** | [Core-Fachmodule][foundation] sind von HTTP/Persistenz getrennt; [API-Start][api-program] bietet authentifiziertes OpenAPI v1; [Adaptervertrag][integrations] dokumentiert Binär-/CSV-Semantik. | Vollständige maschinenlesbare Erfolgs-/Fehlerschemas und rückwärtskompatible OpenAPI-Vertragsprüfungen sind nicht für alle Endpunkte nachgewiesen. |
| **DP-02-03** PostgreSQL und EF-Core-Migrationen strukturieren | **K** | [DbContext][dbcontext], [Initialmigration][migration], tenantqualifizierte Fremdschlüssel und [Security-PG-Tests][security-tests] sichern Isolation auch unter Umgehung des EntityStore. | Große Upgrade-/Downgradepfade, Produktionsmigration und belastbare Optimierung des JSONB-Lesemodells bleiben offen. |
| **DP-02-04** .NET-Worker, Outbox und atomaren Audit aufbauen | **K** | [EntityStore][store], [CommandExecutor][api-infrastructure] und [Worker][worker] verbinden Mutation/Audit/Receipt/Outbox atomar; [Worker-PG-Tests][worker-tests] prüfen SKIP LOCKED, Token-CAS und Absturzfälle. | Audit ist Anwendungsevidenz, kein manipulationssicheres externes WORM-Archiv; nicht unterstützte Generierungsjobs schlagen ausdrücklich fehl. |
| **DP-02-05** C#-Dokumentendienst und Dateispeicher entwickeln | **K** | [Dokument-API][documents], [S3-Adapter][object-store], [PDF-Erzeugung][pdf], [Korruptionstests][document-tests] und [realer S3-Download][live-renovation] belegen Version/SHA/Größenbindung. | Produktive Speicherpolitik, Malware-Prüfung und professionelle Dokumentvorlagen sind nicht vollständig abgenommen. |
| **DP-02-06** API-Abfragen und Angular-Datenzugriff konsolidieren | **T** | [SQL-paginierte Suche][discovery-api], [zentrale Datensatzrechte][discovery-access] und [Angular-Kontextschutz][ui-nine] bündeln begrenzte, mandantengefilterte Lesezugriffe. Rechte-/Betriebswechsel entwerten alte Listen, Details und Downloads; die [aktuellen Fixture-/Live-Nachweise][checkpoint-nine] trennen Fehlerfälle und echte Datenzugriffe. [FinancialSources][financial-sources] begrenzt verknüpfte Rechnungs-/Korrekturhistorien mandanten- und auftragsbezogen; [Checkpoint 11][checkpoint-eleven] sichert Projektkosten-Quellmetadaten und Referenzen zusätzlich ab. | Kein repräsentativer Lastnachweis oder vollständiger globaler Suchindex. Abhängige Sammlungen der RecordPolicy und weitere Finanzendpunkte können noch ganze Mandantenhistorien lesen. Index-, Größen- und Latenzmessungen unter realistischer Last sind nicht erbracht. |
| **DP-02-07** Serverkonfiguration, Schlüssel und Beobachtbarkeit schaffen | **T** | [Konfiguration][service-registration], [Schlüssel][api-program] und [Betriebsbeobachtung][observability] ergänzen persistente Schlüssel um begrenzte Command-/Outbox-Metriken sowie einen mandantengebundenen Geschäftsführungs-Snapshot. [Vier Snapshot- und drei Metrikfälle][operations-api-tests] sind im [vollständigen Checkpoint-8-Lauf][backend-eight] enthalten. | Externer Metrikexport, verteiltes Tracing, Alarmzustellung, reale Secret-Rotation, repräsentative Last und vereinbarte Betreiber-SLOs bleiben offen. Der Snapshot behauptet keine Worker-/Providerverfügbarkeit. |

### DP-03 – Identität, Freigaben, Module und Bediengrundlage (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-03-01** ASP.NET-Identität und Betriebsmitgliedschaften trennen | **K** | [Identity-/Membership-Modelle][models], [Auth][auth], [Mandantenfilter][api-infrastructure] und [Securitytests][security-tests] trennen Person, bestätigtes Konto und aktive Betriebsmitgliedschaft. | Produktive Kontoübernahme/Migration bleibt ein eigenständiger Nachweis; kein Kopieren alter Zugangsdaten wird als gelungen behauptet. |
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
| **DP-04-01** CRM und Ansprechpartner ausbauen | **T** | [Customer][projects] und typisierte CRM-Seiten in [Collection UI][collections] speichern Kontakt-, Adress- und Notizfelder; [echter Ablauf][live-renovation] legt Kunden an. | Mehrere strukturierte Ansprechpartner, Kontakthistorien/Dublettenführung und betriebliches CRM-Fachmodell fehlen. |
| **DP-04-02** Gebäude und dauerhafte Dachakte einführen | **K** | [Building/Project][projects], [RoofRevision][roof-model], [Roof-/Aufmaßtests][roof-tests] und [Sanierungsnachweis][live-renovation] verbinden dauerhaftes Gebäude mit unveränderlichen Dachversionen. | Reale Bestandsakten und Reparatur/Wartung über mehrere Projekte wurden nicht durchgängig abgenommen. |
| **DP-04-03** Projekte und Arbeitsstruktur modellieren | **T** | [Project/TaskItem][projects], Graphregeln in [Business API][business-api] und [echter Ablauf][live-renovation] bilden Projektbezüge, Typen/Status und Aufgaben ab. | Komplexe Arbeitsstruktur, Projektvorlagen und betrieblich bestätigte Projektorganisation fehlen. |
| **DP-04-04** Aufgaben, Fristen und Verlauf integrieren | **K** | [TaskItem][projects], Fristen im [Dashboard][business-api], [Projektverlauf][discovery-api] und [11 echte HTTP-/PG-Tests][discovery-evidence] verbinden Aufgaben mit aktuell zugänglichen Projekt-Ereignissen; [18 Fixtures und 3 echte Browserabläufe][discovery-live] prüfen die [Bedienoberfläche][discovery-ui-docs]. | Historische Projektverknüpfungen werden nicht rekonstruiert; umfassende automatische Erinnerungs- und betriebliche Abnahmeabläufe fehlen. |
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
| **DP-07-03** Wareneingang und Bestandsbewegungen erfassen | **K** | [Lagerkorrekturen][warehouse-docs], [Bestandsregeln][inventory] und [Feldkommandos][field-api] ergänzen Teilwareneingänge um Inventur mit Beleg und frischem Buchstand, atomare Umlagerungspaare sowie originalgebundene Lieferanten-/Baustellenrückgaben. Eine Lieferantenretoure aus einem anderen Lager benötigt ein geprüftes direktes Transferpaar. Das paginierte Journal ist in der [Lagerprüfung][inventory-ui] bedienbar; [Core-Regressionen][inventory-reconciliation-tests], [HTTP-/PostgreSQL-Tests][commercial-api-tests] und [Browser-Fixtures][inventory-browser] behandeln Mengen-, Beleg- und Kontextgrenzen. Bestehende Bewegungsdigests bleiben erhalten. | Reale Inventur-/Mehrlager-Abnahmen, mehrstufige Transferketten, physische Chargen-/Serienzuordnung, weitergehende Bewertungsvorschriften und externe Retourenbelege bleiben offen. Der direkte Transfer belegt eine Mengen-/Bewertungskette, keine einzelne physische Liefereinheit. |
| **DP-07-04** Projektverbrauch und Reservierung verbinden | **K** | [Reserve/Consume/Release und ProjectReturn][inventory] binden die Baustellenrückgabe an Originalverbrauch und exakt passende Reservierung. In derselben Transaktion sinkt consumedQuantity und steigt releasedQuantity; der noch reservierte Rest bleibt unverändert. [Core-Regressionen][inventory-reconciliation-tests] und [konkurrierende HTTP-/PostgreSQL-Rückgaben][commercial-api-tests] prüfen atomare Zähler, Mengenrest und Replay. [QuantityEvidence/MaterialDemand][quantity-model] und [Mengentransfer-API][quantity-api] übernehmen weiterhin ausdrücklich geprüfte Bedarfe aus eingefrorenen Aufmaßen in einen Bestellentwurf. | Vollautomatische regelbasierte Materialdisposition einschließlich realer Packungsgrößen/Lieferantenangebote und repräsentative Mehrlagerpraxis fehlen. Eine bewusst freigegebene Aufmaßumrechnung ist kein allgemeines normatives Materialbedarfsverfahren. |
| **DP-07-05** Geräte und Fahrzeuge verwalten | **T** | [Equipment][inventory] enthält Gerät/Fahrzeug, Kennzeichen, Ort, Prüfungstermin und Verfügbarkeit; [Planungstests][planning-tests] prüfen Ressourcenkollisionen. | Wartungshistorie, Fahrtenbuch, Kilometer/Kosten, Prüfbelege und durchgängiger Flottenprozess fehlen. |
| **DP-07-06** Einkaufsablauf und Differenzen absichern | **T** | [Lagerkorrekturen][warehouse-docs] führen Inventur-, Umlagerungs- und Rückgabefälle mit Belegrest, freiem Bestand, unveränderlichen Bewegungen und aktueller Rollenprüfung auch vor Replay aus. Lieferantenrückgaben rechnen den Bestellstatus zurück und erlauben Ersatzlieferungen. [Core-Tests][planning-tests], [Rückgabe-Regressionen][inventory-reconciliation-tests] und [HTTP-/PostgreSQL-Tests][commercial-api-tests] prüfen Doppel-/Parallelbuchung und Ablehnung ohne Teiländerung. Der [aufmaßgebundene Einkaufsentwurf][quantity-docs] bleibt an den bestehenden Freigabeweg gebunden. | Lieferantengutschriften, Schadens-/Transportschadenabläufe, externe Bestell-/Retourenübermittlung und vollständige betriebliche Einkaufsabnahme fehlen. Die Lagerbuchung löst keine Zahlung, Gutschrift oder Lieferantenbenachrichtigung aus. |

### DP-08 – Rechnungen, offene Posten und lesendes Banking (P1)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-08-01** Beleg- und Steuerregeln spezifizieren | **T** | [Money/Pricing][commercial], [Invoice][finance], [kumulative Rechnungsarithmetik][invoice-rounding] und [SupplierInvoice][supplier-finance] erzwingen Cent-, Positions- und Summenregeln. 21 neue Core-Fälle prüfen Originalpositionsrundung, tatsächliche Vorbeträge und Centkorrekturen. | Steuerfälle, Belegpflichtfelder und rechts-/anbieterbezogene Rechnungsprofile sind nicht vollständig spezifiziert oder extern abgenommen. |
| **DP-08-02** Ausgangsrechnungen und Korrekturen umsetzen | **K** | [InvoiceWorkflow][finance] verwendet für neue Belege [kumulative Originalpositionsbeträge und eingefrorene Herleitung][invoice-rounding]. Der ältere Checkpoint 8 belegt 21 Core-, fünf echte HTTP-/PG- und drei PDF-Fälle; [Checkpoint 11][checkpoint-eleven] ergänzt einen ausdrücklich begrenzten Null-Euro-Mengenabschluss mit eigenen Core-/API-Regressionsfällen und erfolgreichen Commit-bezogenen Prüfungen. [FinancialSources][financial-sources] lädt zugehörige Rechnungen und Korrekturen über begrenzte mandantenbezogene Links statt ganzer Mandantenhistorien. Legacy-Digests und Aufmaßherkunft bleiben prüfbar. | E-Rechnungsprofile, steuerliche/vertragliche Abnahme und repräsentative große Belegmengen sind offen; der Null-Euro-Mengenabschluss ist eine technische Sonderfallregel, keine normative Zusicherung. Andere Finanzendpunkte können weiterhin mandantenweite Historien laden. |
| **DP-08-03** Strukturierte E-Rechnungen verarbeiten | **O** | [Integrationsgrenzen][integrations] benennen XRechnung/ZUGFeRD ausdrücklich als nicht implementierte Provider-/Profilformate. | Sichere profilversionierte XML-Validierung, Schematron/Regeln, Importvorschau und geprüfte strukturierte Ausgabe fehlen. |
| **DP-08-04** Offene Posten und Mahnwesen führen | **K** | [Forderungsfälle][collections-docs] verbinden offenen Arbeitsvorrat, geprüfte Salden, manuelle Stufen, Kontakte, Streit-/Pausenzustände und Wiederöffnung. 18 Core-/elf PG-Fälle, 27 Clientfixtures und drei echte 119→99-EUR-Vorschauabläufe sind in [Checkpoint 8][backend-eight] und [Checkpoint 9][checkpoint-nine] belegt. | Keine automatische Mahnzustellung, Gebühren-/Zinsberechnung, Rechtsprüfung oder historische as-of-Buchhaltung. Freigabe dokumentiert den internen Entwurf; ein tatsächlicher Kontakt wird separat mit Beleg erfasst. |
| **DP-08-05** Eingangsbelege prüfen und freigeben | **T** | [SupplierInvoice][supplier-finance], [Freigabekommandos][commercial-api] und [SupplierFinanceTests][supplier-tests] prüfen Eingangsrechnungen/-gutschriften und unabhängige Freigabe. | Vollständiger Beleg-/OCR-/Profilprüfungsprozess, echte Lieferantenbelege und betriebliche Fachabnahme fehlen. |
| **DP-08-06** Bankdaten ausschließlich lesend anbinden | **T** | [CSV-Bankadapter][bank-csv], [Integrations-API][integration-api] und [echte PG-Tests][integration-tests] importieren ausschließlich vorhandene Buchungen mit stabilen IDs und ohne Zahlungsauftrag. | Automatische lesende Anbindung an einen tatsächlichen Bankanbieter samt Vertrag/Tokenverwaltung ist nicht implementiert. |
| **DP-08-07** Zahlungsabgleich und Rücknahme implementieren | **K** | [PaymentWorkflow][finance], [PayablesWorkflow][supplier-finance], [Ledger-/Supplier-Tests][supplier-tests] und [PG-Konkurrenztests][commercial-api-tests] prüfen Teil-/Sammelzahlungen, Skonto, Einbehalt, Refund und Rücklastschrift. | Praktische Bankreferenzdaten und vollständige fachliche Freigabe sämtlicher Geschäftsfälle fehlen. |
| **DP-08-08** Finanzablauf und Exporte fachlich prüfen | **T** | [Finanzexport][ledger-export], kumulative Rechnungen/Korrekturen und saldengebundene Forderungen sind mit Core-/PG-Fällen sowie echten [Rechnungs-, Forderungs- und Exportketten][checkpoint-nine] geprüft. Drei CSV/ZIP-Abläufe verifizieren tatsächlich heruntergeladene Exportbytes gegen ihre Manifeste. | Kein Steuerberater-/Buchhaltungsanbieter-Abnahmetest oder produktiver Kontoabgleich; eigener CSV-Vertrag ist kein DATEV-Zertifikat. |

### DP-09 – Kundenportal, Wartung und persönliche Kommunikation (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-09-01** Kundenportal mit freigegebenen Inhalten aufbauen | **K** | [Portalmodell][portal-model], [API][portal-api], [Portaltests][portal-tests] und [echter Portalbrowserlauf][live-portal] beschränken persönliche Partner auf einzelne eingefrorene Verkaufsinhalte/Dokumente. | Realer Kundenbetrieb und alle kommerziellen Portalvarianten sind nicht abgenommen. |
| **DP-09-02** Wartungsverträge und wiederkehrende Termine führen | **T** | [Wartungsfolge][maintenance-docs], [Fachablauf][maintenance-workflow], [Kommandos][maintenance-api] und [Workerplanung][maintenance-planner] verbinden ausdrücklich konfigurierte Checklisten und Vorlauf mit manueller oder aktivierter automatischer Anlage von Wartungsprojekt und eingefrorenem Termin. Zugewiesene Mitarbeitende dokumentieren Prüfergebnisse; Managementabschluss schreibt Projekt, Termin und Folgetermin atomar fort. [Core-Tests][maintenance-core-tests], [HTTP-/PostgreSQL-Tests][maintenance-api-tests] und [Browser-Fixtures][maintenance-browser] prüfen Monatsendanker, Vertragsende, sichere Altbestandswerte, Doppelplanung, Rechte und unveränderliche Nachweise. | Die fachliche Eignung betriebsspezifischer Checklisten und ein realer Wartungsfall sind nicht extern abgenommen. Automatische Feiertags-/Wochenendverschiebung, kaufmännische Auftrag-/Rechnungserzeugung und externe Kalenderintegration fehlen. Die automatische Anlage ist standardmäßig aus; Personaleinsätze werden in der bestehenden Disposition zugeordnet. |
| **DP-09-03** Kommunikation an Vorgänge binden | **T** | [DeliveryAttempt][commercial], [Outbox][worker] und [PortalFeedback][portal-model] binden Versand/Rückfragen an Vorgang und freigegebenen Stand. | Keine vollständige ein-/ausgehende Mail-/Chat-Konversationsakte oder produktive Providerhistorie. |
| **DP-09-04** Persönliche Benachrichtigungsregeln speichern | **K** | [NotificationPreference][operations-model], [Worker][worker] und [Benachrichtigungstests][worker-tests] prüfen persönliche Uhrzeit/Zeitzone/Wochentage sowie aktuelle Berechtigung. | Zustellung über reale externe Anbieter ist nicht belegt; ruhende lokale Geräte erhalten keinen garantierten Push. |
| **DP-09-05** Push, E-Mail und WhatsApp anbinden | **T** | [SMTP-Adapter][worker] unterstützt konfiguriertes STARTTLS; [Worker-Tests][worker-tests] prüfen fehlende Konfiguration, Fehler und Uncertainty ohne echte Nachricht. | Push- und WhatsApp-Anbindung sowie echter SMTP-Zustellnachweis fehlen; ungedeckte Kanäle melden keinen falschen Erfolg. |
| **DP-09-06** Morgenbriefing und Wartungsablauf absichern | **T** | [Briefingregeln][operations-model] und [Worker-PG-Tests][worker-tests] deduplizieren lokale Tage/DST und filtern aktuelle eigene Arbeit. Ergänzend verbindet die [Wartungsfolge][maintenance-docs] Vertrag, Arbeitsprojekt, dokumentierte Checkliste, Abschluss und nächsten Fälligkeitstermin. [HTTP-/PostgreSQL-Tests][maintenance-api-tests] prüfen zwei Scheduler, Betriebsdatum, Wiederherstellungssperre und entzogene Zuordnung vor Replay; [Browser-Fixtures][maintenance-browser] prüfen den bedienbaren Ablauf und Kontextwechsel. | Vollständiger echter Wartungs-/Morgenbriefingablauf mit realen Betriebsdaten und Zustellung über einen gewählten Provider bleibt offen. Die automatische Wartungsanlage belegt weder einen laufenden Produktivworker noch Kalender-, E-Mail-, Push- oder WhatsApp-Zustellung. |

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
| **DP-11-01** Verfahren, Datenstände und Nutzungsrechte klären | **T** | [Methoden-/Rechteinventar][methods] trennt elementare Mathematik, öffentliche Normmetadaten, Herstellerdaten und statische Installerbefunde. | Lizenzierte Normvolltexte/Herstellerdaten, Rechteklärung und unabhängige fachliche Referenzabnahme fehlen. |
| **DP-11-02** Rechenkern und nachvollziehbare Ergebnisberichte | **K** | [Provenance][provenance], [TechnicalRecord][technical-record], [Technical-API/PDF][technical-api] und [JSONB-Regressionsfälle][regression-tests] frieren Methode/Input/Resultat/Digest ein. | Nur deklarierte elementare Verfahren; keine generelle Fachsoftware- oder normative Berichtszertifizierung. |
| **DP-11-03** U-Wert und thermische Aufbauten | **T** | [ThermalCalculator][thermal], [ThermalBridgeCalculator][bridges] und [unabhängige Thermiktests][thermal-tests] berechnen homogene Serien, ideale parallele Pfade und externe psi/chi-Summen. | Vollständige ISO-6946-Verfahren, Luftschichten/Korrekturen, reale Materialdaten und normative Sparrendachprüfung fehlen. |
| **DP-11-04** Feuchteschutz und Verfahrensgrenzen | **O** | [Methodengrenzen][methods] dokumentieren, warum eine U-Wert-Rechnung kein Feuchteschutznachweis ist. | DIN-4108-3-/ISO-13788-Verfahrenswahl, Tauwasser/Trocknung und hygrothermische Simulation sind nicht implementiert. |
| **DP-11-05** Gefälledämmung und Verlegeplanung | **T** | [Linearer rechteckiger Gefälledämmungsrechner](tapered-insulation.md), [Core-Test](../tests/FirstBlick.Core.Tests/Technical/TaperedInsulationTests.cs) und [HTTP-/PostgreSQL-Test](../tests/FirstBlick.Api.Tests/TaperedInsulationApiTests.cs) belegen eine ausdrücklich nichtnormative Materialvolumenhilfe für rechteckige Grundrisse. | Herstellergebundene Platten, Entwässerungsgeometrie, reale Dachöffnungen, Zuschnitt/Verlegeoptimierung und betriebliche Fachabnahme fehlen; keine freigegebene Verlegeplanung. |
| **DP-11-06** Windlast und Befestigung | **O** | [Methodengrenzen][methods] schließen Wind-/Befestigungsnachweise aus. | Zonen-/Standortdaten, Regelwerk, Lastkombinationen, Befestiger-/Untergrunddaten und geprüfte Nachweise fehlen. |
| **DP-11-07** Haupt- und Notentwässerung | **O** | [Dachdetails][roof-model] können einen Ablauf nur beschreiben; [Methodengrenzen][methods] unterscheiden dies von Dimensionierung. | Haupt-/Notentwässerung mit Regendaten, Fließwegen, hydraulischen Grenzen und unabhängigen Referenzen fehlt. |
| **DP-11-08** Steildach, Schnee und konstruktive Nachweise | **O** | [Geometriekern][roof-geometry] berechnet einfache geneigte Ebenen; [Methodengrenzen][methods] schließen Tragwerksaussagen aus. | Schnee, Steildachkonstruktion, tragende Verbindungen und konstruktive Nachweise fehlen. |
| **DP-11-09** Photovoltaik als Dachplanungsmodul | **O** | [Methodengrenzen][methods] führen Photovoltaik ausdrücklich als offenen Fachbereich. | Modulbelegung/Verschattung, Befestigung/Last, Elektroplanung und Hersteller-/Standortdaten fehlen. |
| **DP-11-10** Fachsoftwareintegration und Gesamtprüfung | **O** | [Statisches Installerinventar][methods] enthält weder ausgeführte Demoalgorithmen noch kopierte Herstellerdaten. | Fachsoftwareadapter, lizenzierte Datenaustauschverträge und unabhängiger vollständiger Flach-/Steildachvergleich fehlen. |

### DP-12 – Schnittstellen und Datenübernahme (P1/P2)

| Aufgabe | Stand | Konkrete Implementierungs-/Testbelege | Fehlender Umfang bzw. Abnahmegrenze |
|---|---|---|---|
| **DP-12-01** Integrationsverträge und Adapterbasis definieren | **K** | [Versionierte CSV-/Exportverträge][integrations], [Core-/PG-Nachweise][integration-evidence] und drei echte [UI-Ketten][checkpoint-nine] prüfen Vorschau, ausdrücklich ausgelösten Import, stabile Duplikat-IDs, atomare Konfliktablehnung und verifizierte Exportpakete. | Weitere Provider benötigen eigene Profile, Identitäts-/Idempotenzregeln und geprüfte Fixtures. |
| **DP-12-02** GAEB und Material-/Leistungskataloge anbinden | **O** | [Integrationsgrenzen][integrations] erklären fehlende stabile externe Katalog-/Versionsidentität. | GAEB und Material-/Leistungskatalogimport mit geprüften Versionen/Zuordnungen sind nicht implementiert. |
| **DP-12-03** Buchhaltungs- und Lohnexporte umsetzen | **T** | [LedgerExport][ledger-export], [PayrollTimeExport][payroll-export] und [echte Exporttests][integration-tests] liefern IDs/Digests/Korrekturen sowie exakte UTC-Pausen-/DST-Segmente. | Keine providerfertige Buchungsschnittstelle/Lohnabrechnung, Kontierung, Steuer-/Tariflogik oder Providerabnahme. |
| **DP-12-04** Dokumentierte API und Ereignisse bereitstellen | **T** | [API v1][api-program] und [Adapterdokumentation][integrations] dokumentieren Auth, Formate und `firstblick.integration-event/1.0.0` in Importreceipts. | Keine externe Ereignissubskription/Webhookauslieferung oder vollautomatisierte OpenAPI-Kompatibilitätsprüfung. |
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
| **DP-14-01** Kennzahlen definieren und verlinken | **T** | [Dashboard][business-api], [Finanzlesemodell][finance-read], [Kostenbericht][cost-docs] und [Betriebsübersicht][observability] verknüpfen klar definierte Zustände bzw. Beträge mit ihren prüfbaren Quellen; [Checkpoint 9][checkpoint-nine] ergänzt echte UI-Nachweise. | Vollständiger KPI-Katalog, zeitliche Vergleichsreihen, abgestimmte betriebliche Kennzahlendefinitionen und frei wählbare Analysen fehlen. |
| **DP-14-02** Auswertungen und Datenexport ergänzen | **T** | [Versionierte CSV-Pakete][integrations] und [quellengebundener Kostenbericht][cost-docs] liefern prüfbare Detaildaten; die neue [Exportbedienung][ui-nine] ist mit tatsächlichen Downloads und Manifest-/Digestprüfung im [Checkpoint 9][checkpoint-nine] belegt. | Breitere frei konfigurierbare Auswertungen und interaktive Trends fehlen; die vorhandenen Berichte sind ausdrücklich definierte Einzelverträge. |
| **DP-14-03** Optionale Assistenz kontrolliert integrieren | **O** | [Architekturgrenzen][architecture] unterscheiden überprüfbare Fachlogik von ungeprüften Schlussfolgerungen. | Ein optionales Assistenzsystem mit Provenienz, Freigaben, Evaluation, Kosten- und Datenschutzkontrolle ist nicht implementiert. |

## Aus dieser Sichtung folgende Prioritäten

1. Den finalen Quellstand mit vollständig grünem Server-/Web- und Container-/Operations-Lauf verbinden; aktuelle Recovery-/Migrationsänderungen gesondert nachweisen.
2. Den bestandenen Offline-Neustartablauf mit repräsentativen Betriebsfällen und realen Geräten abnehmen; Storage-Eviction, Hardwareverlust, zusätzliche Feldrollen und Konfliktfälle gesondert prüfen.
3. Den bestandenen kanonischen Migrationsdrill mit genehmigtem realem D1-/R2-Export, überprüften Quellkontrollsummen und fachlich bestätigtem Mapping proben. Kein bestehendes Produktionsziel überschreiben.
4. Den implementierten Mengenübergang mit betrieblichem Referenzfall und vollständiger Bedienung abnehmen; Istkosten aus nachvollziehbaren Quellen ableiten; Einkaufsdifferenzen und Mahnwesen vollständig spezifizieren.
5. Benötigte Rechnungs-/GAEB-/Bank-/Lohnprofile samt Version, Nutzungsrechten, Referenzdaten und verantwortlicher Abnahme festlegen. Eine eigene CSV-Datei ersetzt diesen Vertrag nicht.
6. Technische Nachweise erst nach geklärter Methode, Datenrechten, Applicability-Prüfung und unabhängigen Referenzfällen implementieren. U-Wert-/Polygonarithmetik darf nicht als Feuchte-/Last-/Entwässerungsnachweis ausgegeben werden.
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
