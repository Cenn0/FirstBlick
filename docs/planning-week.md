# Wochenplan mit Betriebszeitzone

Roadmap: **DP-06-01**, Ergänzung zur bestehenden Einsatzliste und den serverseitigen Planungsregeln.

## Bedienbarer Ablauf

`/planning-week` zeigt sieben Geschäftstage mit Personal, Projekt, Beginn/Ende und zugeordneten Geräten beziehungsweise Fahrzeugen. Ein Datum wählt seine Kalenderwoche; vorherige und nächste Woche sind direkt erreichbar. Ein Link mit `?weekStart=2026-10-19` öffnet eine konkrete Woche. Die Oberfläche ordnet sich auf Tablet und Mobil in drei beziehungsweise einer Spalte an.

Neue Einsätze werden aus den ausdrücklich ausgewählten Projekt-, Mitarbeiter- und Gerätedaten erfasst. Suchlisten sind in 25er-Seiten aufgeteilt. Bestehende Einsätze lassen sich ändern oder ausdrücklich absagen. Eine Absage erhält den Datensatz in der Einsatzliste und entfernt ihn aus der aktiven Wochenansicht. Wochenwechsel und andere Bearbeitungsaktionen sind während eines offenen Entwurfs gesperrt; zum Verwerfen wird die Bearbeitung bewusst geschlossen.

Die bereits vorhandene API bleibt für Änderungen maßgeblich: Idempotency-Key, aktuelle Rechte, Betriebsmodul, Revisionsprüfung, atomare Speicherung und Audit. Mitarbeiter-/Gerätekollisionen, genehmigte Abwesenheiten, Projektzeitfenster und die Obergrenze von 31 Tagen werden serverseitig geprüft. Auch ein inzwischen unverfügbares Gerät verhindert nicht die Absage seines bestehenden Einsatzes. Ein neuer aktiver Einsatz mit einem unverfügbaren Gerät wird weiterhin abgewiesen.

## Geschäftszeit statt Gerätezeit

Alle Anzeige- und Eingabefelder verwenden die konfigurierte IANA-Zeitzone des Betriebs. Das Gerät darf sich in einer anderen Zeitzone befinden. Jeder Tagesanfang wird separat nach UTC umgerechnet; ein Geschäftstag bei Zeitumstellung kann deshalb 23 oder 25 Stunden dauern.

Nicht existierende Ortszeiten werden abgewiesen. Bei einer doppelt vorkommenden Ortszeit muss der Anwender den UTC-Zeitversatz ausdrücklich wählen. Bei der Bearbeitung werden der bereits gespeicherte Zeitpunkt, seine tatsächliche Wiederholung und Millisekunden erhalten. Beginn und Ende sind halboffene Intervalle: ein Einsatz, der exakt am Beginn eines Tages endet, gehört nicht mehr zu diesem Tag.

Die Geschäftszeitauflösung nutzt die Zeitzonendaten von .NET beziehungsweise `Intl` im Browser. Für Geschäftszeitzonen, deren Mitternacht am gewählten Datum fehlt oder mehrdeutig ist, weist der Server die Wochenprojektion ausdrücklich ab; er erfindet keine Tagesgrenze. Geprüfte Fälle umfassen Berlin sowie clientseitig eine Zeitzone mit halbstündiger Zeitumstellung.

## Leseschnittstelle und Grenzen

`GET /api/v1/planning/week?weekStart=YYYY-MM-DD` akzeptiert optional `employeeId`, `projectId` und `equipmentId`. Filter wirken zusätzlich zur Mandanten- und Datensatzberechtigung. Sie erweitern keine Sichtbarkeit. Der Zugriff auf Mitarbeiterdaten für eingeschränkte Rollen wird unmittelbar in der Datenbank auf eigene aktive Mitarbeiterprofile begrenzt; die gesamte Einsatzhistorie wird nicht vorab geladen.

Die Antwort enthält Tagesgrenzen, Betriebszeitzone, wiederholte Filter, maximal 1.000 aktive Einsätze sowie die Namen genau der referenzierten Projekte, Mitarbeiter und Betriebsmittel. Bei mehr Treffern scheitert die Anfrage mit `planning_week_budget`; es wird kein stillschweigend gekürzter Kalender ausgegeben. Die Stammdatensuche ermöglicht eine anschließende Einschränkung. Abgesagte oder archivierte Einsätze fehlen im aktiven Kalender.

Die Oberfläche prüft Antwortumfang, Identitäten, Revisionen, Zeitzonen-/Tagesgrenzen, Filterbindung, Gerätezuordnung und tatsächliche Tagesüberlappung. Nach einem Betriebs- oder Rechtewechsel werden Entwürfe und alte Daten verworfen; verspätete Antworten werden nicht übernommen. Serverkonflikte lassen die Eingabe zur Korrektur stehen.

Die bestehende Modulpolitik bleibt erhalten: Das Abschalten des Baustellenmoduls verhindert neue Änderungen. Berechtigte historische API-Lesezugriffe bleiben möglich; die Wochenplan-Oberfläche wird bei abgeschaltetem Modul ausgeblendet. Eine Wiederherstellungssperre wird weiterhin zentral durch den Server erzwungen.

## Verifikation und verbleibender Roadmap-Umfang

- C#-Kern: DST-Tagesgrenzen, halboffene Überschneidungen, gültiger Wochenbeginn und UTC-Verträge.
- Echte HTTP-/PostgreSQL-Fälle: Projektion, eigene Mitarbeiter-/Mandantensicht, Ressourcenfilter und Namen, 1.001-Treffer-Grenze mit anschließender Filterung, Rollen-/Modulpolitik, Revisionskonflikte, Gerätekollision und Absage trotz zwischenzeitlicher Nichtverfügbarkeit.
- TypeScript: Betriebszeit unabhängig vom Gerät, Lücken und doppelte Zeiten, Millisekunden, halbstündige Zeitumstellung, Kalendergrenzen und fehlerhafte Antwortbindungen.
- Browser-Fixtures: Darstellung auf drei Viewports, Neuanlage, Bearbeiten/Absagen, Zeitumstellungen, Konfliktkorrektur, reine Leserechte, Modulabschaltung, fehlerhafte Antworten, Filter und verspätete Antwort nach Betriebswechsel.

Die Browserprüfungen verwenden kontrollierte HTTP-Antworten; sie sind von den echten PostgreSQL-Tests getrennt. Eine grüne Ausführung gilt für den jeweils geprüften Commit, nicht für künftige Änderungen.

Diese Erweiterung schließt den bisher unvollständigen grafischen Wochenarbeitsplatz. Routenoptimierung, automatische Fahrzeit- oder tarifliche Arbeitszeitbewertung und eine eigenständige Fachplanung realer Betriebsdaten werden damit nicht behauptet. Die übrigen Roadmap-Punkte werden durch diese Funktion nicht pauschal als abgeschlossen markiert.
