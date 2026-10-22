# PV-Dachgruppen – mehrere rechteckige Dachflächen (DP-11-09, Teilumfang)

Stand: 22.10.2026 · **Nicht normativer Geometrieentwurf; keine Montagefreigabe.**

Die Arbeitsfläche **/technical/pv-set** erweitert den [bestehenden Einzelrechner](pv-module-layout.md)
um bis zu **acht namentlich getrennte Dachflächen**. Jede Fläche hat ihre **eigenen auf der
geneigten Dachfläche gemessenen Maße**, Modulmaße und Nennleistung, Quellen, Randabstände,
Modulabstände, Orientierungswahl und maximal 64 Hindernisrechtecke. Die Koordinaten
beginnen für **jede** Dachfläche neu links unten bei (0,0); sie sind keine gemeinsamen
Gebäudekoordinaten.

## Nachvollziehbarer Berechnungsablauf

1. Eingabe und Quellangaben für die gesamte Dachgruppe sowie jede einzelne Fläche prüfen.
   Dachflächenbezeichnungen werden getrimmt und sind unabhängig von Groß-/Kleinschreibung eindeutig.
2. Pro Dachfläche den geprüften Einzelalgorithmus aufrufen; je Einzelraster höchstens 2.500
   Kandidaten, insgesamt höchstens 10.000 Kandidaten über alle Flächen. Eine Überschreitung
   oder ungültige Geometrie wird **abgewiesen**, nicht unbemerkt gekürzt.
3. Die **tatsächlichen Einzelresultate** mit Ausrichtung, Positionen, Hindernisabweisungen,
   Modulfläche, Nennleistung und eigenem SHA-256 beibehalten. Gesamtstückzahl, Summen der
   **gemessenen Dachflächen**, Modulflächen und Nennleistung werden ausschließlich
   als Summen der einzelnen Ergebnisse gebildet.
4. Versionierte Gruppenprovenienz **pv-roof-set-grid/1.0.0** über den normalisierten
   Gesamtinput erzeugen. Vor dem JSON-Export prüft der Client Quellbezug, Einzelresultate,
   Gesamtwerte und den ausdrücklich nicht normativen Status erneut.
5. Optional im Projekt als unveränderliche technische Ergebnisakte ablegen:
   POST /api/v1/technical/runs mit **method=roof.pv-multi-layout**.
   Der Stand wird durch den vorhandenen Audit-/Revisions-/Digest-Vertrag der
   technischen Ergebnisse geschützt.

**Direkter Endpunkt:** POST /api/v1/technical/roof/pv-multi-layout.
Die Berechnung und die Projektablage erfordern technische Schreibrechte im jeweils
aktiven Betrieb. Bei Betriebs- oder Berechtigungswechsel ignoriert die Oberfläche
veraltete Antworten.

## Prüfbare Grenzen

**Kein Beweis dafür, dass die physisch gemessenen Dachflächen disjunkt sind.**
Überschneidungen, gemeinsame Kanten und übergreifende Hindernisse werden weder
geometrisch rekonstruiert noch automatisch erkannt. Mehrfach erfasste Flächen erhöhen
rechnerisch die Summen und müssen vor fachlicher Verwendung ausgeschlossen werden.

Die Belegung bleibt ein **ursprungsgebundenes Raster je Rechteck**; es gibt keine
optimierte mehrseitige Verlegung, Dachpolygonbelegung, Ertragsberechnung oder
Hersteller-/Unterkonstruktionsprüfung. Insbesondere werden **keine** Aussagen zu
Statik, Windsog, Schneelast, Blitz-/Brandschutz, Laufwegen, Elektrik,
Dachgenehmigung oder normativer Montagefähigkeit getroffen.

**Status:** DP-11-09 bleibt **T / Teilumfang**. Ein freigegebener PV-Ausführungsplan
erfordert gesonderte unabhängige Fachprüfung, lokale Regeln und
herstellerspezifische Nachweise. Erfolgreiche CI-/Operations-Läufe sind für den
konkreten Merge-Commit gesondert nachzuweisen.

## Quell- und Testverweise

- Core: src/FirstBlick.Core/Technical/PvRoofSetLayout.cs
- Core-Regressionen: tests/FirstBlick.Core.Tests/Technical/PvRoofSetLayoutTests.cs
- API, Speicherung und Berechtigung: src/FirstBlick.Api/TechnicalEndpoints.cs,
  src/FirstBlick.Core/Business/TechnicalRecord.cs
- Echte HTTP-/PostgreSQL-Tests: tests/FirstBlick.Api.Tests/PvRoofSetApiTests.cs
- Angular: web/src/app/pages/pv-roof-set.component.ts und .html
- Client-Eingabe und Summenprüfung: web/src/app/core/pv-roof-set.models.ts,
  web/tests/pv-roof-set.test.ts
