# Inhomogeneous Dächer – Widerstandsgrenzen statt idealer Einzelpfade

Stand: 21.10.2026 · Roadmap **DP-11-03** (Teilumfang)

## Zweck und Prüfbarkeit

Die bisherige Methode `thermal.parallel` rechnet unabhängige Wärmeleitpfade, ohne den Einfluss isothermer Zwischenschichten zu berücksichtigen. `thermal.inhomogeneous` stellt für einen **ausgerichteten, nichtmetallischen, ebenen Schichtaufbau** zwei unabhängig berechnete Grenzmodelle gegenüber.

- **Obere Widerstandsgrenze:** Jeder Flächenanteil erhält den gesamten Serienschichtwiderstand einschließlich beider Oberflächenwiderstände. Die flächengewichteten Wärmedurchgänge werden addiert: `R_upper = 1 / Σ(f_i / R_path,i)`.
- **Untere Widerstandsgrenze:** Jede Materialschicht wird als isotherme Ebene betrachtet: `R_layer,j = 1 / Σ(f_i / R_layer,i,j)`. Die Schichten sowie Innen- und Außenoberflächenwiderstand werden in Serie addiert: `R_lower = R_si + Σ R_layer,j + R_se`.
- Die **vorläufige Abschätzung** ist `R_estimate = (R_upper + R_lower) / 2`, `U_estimate = 1 / R_estimate`. Die ausgewiesene Halbspreizung ist `100 × (R_upper - R_lower) / (R_upper + R_lower)` Prozent. Grenzwerte bleiben als ungerundete Zahlen im Ergebnis erhalten.

Die Abschnitte müssen auf denselben Schichtgrenzen liegen. Pro Abschnitt gelten separate Wärmeleitfähigkeiten; die Dicken eines Schichtindex müssen in allen Abschnitten identisch sein. Flächenanteile summieren sich auf 1. Innerhalb der bewusst begrenzten Näherung werden **Widerstandsquotienten > 1,5 abgewiesen**, ebenso überlappende/nicht zuordenbare Schichtgrenzen, negative oder nicht endliche Größen und fehlende Materialquellen.

### Synthetischer Rechenfall

Ein zweipfadiger Aufbau mit Innenoberflächenwiderstand 0,13 und Außenoberflächenwiderstand 0,04 m²K/W, beidseitiger 20-mm-Beplankung (0,2 W/(mK)) und einer 200-mm-Schicht aus 15 % Holz (0,12 W/(mK)) und 85 % Dämmung (0,04 W/(mK)) ergibt:

| Größe | Unabhängig reproduzierbarer Wert |
|---|---:|
| Oberer Widerstand | 4,3115243101 m²K/W |
| Unterer Widerstand | 4,2161538462 m²K/W |
| Gemittelter Widerstand | 4,2638390781 m²K/W |
| Geschätzter Wärmedurchgangskoeffizient | 0,2345304271 W/(m²K) |
| Halbspreizung | 1,11836 % |

Die Zahl ist ein **synthetischer Regressionsfall**, kein geprüfter Holzrahmenaufbau mit realen Produkteigenschaften.

## Zugriff und Nachweise

`POST /api/v1/technical/thermal/inhomogeneous` erstellt eine autorisierte **nicht buchende** Rechenvorschau. Bei `POST /api/v1/technical/runs` kann ein zuständiger Anwender mit `method="thermal.inhomogeneous"` den normalisierten Eingabe- und Ergebnisstand projekt-/mandantengebunden, auditierbar und mit Eingabe-SHA-256 ablegen. Die vorhandenen technischen PDF-Endpunkte verwenden den gespeicherten Ergebnissnapshot. Ein Rollenwechsel oder fehlende Projektberechtigung wird nicht durch die Methode umgangen.

Die Testfälle überprüfen unabhängig berechnete Grenzwerte, Energiefluss, gleiche Pfade, Reihenfolge-Digest, Grenzüberschreitung, falsche Flächenanteile, nicht zueinander passende Schichtdicken, fehlende Quellen, Metallbrücken-Ausschluss, Rollenrechte und gespeicherte Reproduzierbarkeit.

## Normstatus und bewusst nicht unterstützte Fälle

**Funktioniert grundsätzlich:** Berechnung der deklarierten Grenzwiderstände unter den oben definierten mathematischen Bedingungen.

**Offiziell unterstützt:** Die Software unterstützt ausschließlich ihren versionierten, eingegrenzten API-Vertrag. Die verwendete ISO 6946:2017 beschreibt einen verwandten begrenzten Ansatz; die konkrete Implementierung wurde **nicht von ISO, DIN oder einem Hersteller geprüft**.

**Best Practice für weitere Entwicklung:** Unabhängige Normreferenzen, Materialdatenstände, Luftschichten-/Belüftungsmodell, Korrekturen, multidimensionale Wärmebrücken und plausibilisierte echte Sparrendach-Beispiele ergänzen; erst danach eine DIN-EN-ISO-Konformitätsbewertung versuchen.

**Sicherheitsseitig empfehlenswert:** Eingaben nur mit nachweisbaren Quellen, keine stillen Standardmaterialien, keine automatische normative Kennzeichnung, keine Freigabe für Beschaffung oder Bauausführung.

Insbesondere fehlen die modellierte Wirkung metallischer Befestiger, durchgehende Metallbrücken, Luftschichten und Kavitäten, Temperaturabhängigkeit, Feuchte-, Dampfdiffusions- und Luftströmungseinfluss, Bauteilanschlüsse und Wärmebrücken. Der Anwender muss ausdrücklich bestätigen, dass solche Metallbrücken nicht vorliegen; diese Bestätigung ist **kein unabhängiger Nachweis**. `metalBridgingModelled=false`, `constructionComplianceAssessed=false` und `provenance.isNormative=false` bleiben immer falsch.

Primärquelle zum Anwendungsbereich: [ISO 6946:2017](https://www.iso.org/standard/65708.html), im Jahr 2022 bestätigt; korrigierte englische Fassung 2021-12. Keine Übernahme lizenzierter Normtexte oder herstellerseitiger Katalogdaten.

**Angular-Anbindung (21.10.2026):** Im Rechenarbeitsplatz „Dach & Berechnung“ steht „Sparrendach – Widerstandsgrenzen“ zur Verfügung. Für jeden Flächenabschnitt werden Anteil, benannte Materialschichten, Dicken, Leitfähigkeiten und Kennwertquellen separat erfasst. Die Eingabe erzwingt identische physische Schichtgrenzen, einen Flächenanteil von eins, eine dokumentierte Materialquelle und die explizite Ausschlussbestätigung für metallische Brücken. Bei Änderung werden alte Ergebnisse und Speicherbelege verworfen. Das Ergebnis stellt beide Grenzwiderstände und den geschätzten U-Wert gegenüber, bleibt als nichtnormativ gekennzeichnet und kann nur nach Projekt- und Rollenprüfung gespeichert werden. Vitest-Regressionstests überprüfen den Eingabe-, Ergebnis- und Speicherkontrakt; Browser- und Backend-CI sind zusätzlich notwendig.

**Offen innerhalb DP-11-03:** Systematisch validierte Luftschichten, normative Korrekturterme, mehrdimensionale Brücken, Referenzabweichungen/Fachabnahme und Abgleich mit der aktuellen deutschen nationalen Anwendung.
