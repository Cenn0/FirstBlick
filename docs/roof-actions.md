# Vorgegebene Dachlasten und Abflussmengen

Stand: 22.10.2026. **DP-11-06/07/08 sind Teilumfänge, keine abgeschlossenen Fachnachweise.** Die Maske `/technical/actions` nimmt vollständige Quellen und ausdrücklich eingegebene Werte entgegen. Es gibt keine vorbelegten Standort-, Produkt- oder Normkennwerte.

## Bedienung und Projektablage

1. Winddruck, Schneelast oder Entwässerungsbilanz wählen; eine Auswahländerung verwirft vorherige Eingaben und Ergebnisse.
2. Datenstand des einzelnen Falls, 1 bis 32 eindeutig bezeichnete Bereiche, Zahlen und Quellen erfassen. Beim Regen muss das Notentwässerungsszenario ausdrücklich gewählt werden.
3. Berechnen. Die Oberfläche prüft die Zuordnung zur Eingabe, alle ausgegebenen Zahlen, Methode, Version und Verfahrensgrenzen. Nach einer Eingabeänderung ist ein neuer Rechenlauf erforderlich.
4. Optional ein Projekt aus der aktuellen Suche wählen, Bericht benennen und bewusst speichern. Der Server rechnet erneut und bindet Betrieb, Projekt, Akteur, Zeitpunkt, Quellen und Resultat an die bestehende unveränderliche technische Projektakte. JSON kann vor oder nach der Speicherung heruntergeladen werden; PDF-Erstellung und Originaldownload stehen in der vorhandenen Ergebnisakte zur Verfügung.

Betriebs-/Rechtewechsel und Verlassen der Maske verwerfen verspätete Antworten. Mitarbeiter ohne technisches Schreibrecht und Betriebe mit deaktiviertem Modul können weder Vorschau noch Speicherung ausführen. Es werden keine externen Dienste aufgerufen, keine Dateien aus den Demo-Installern ausgeführt und keine Herstellerdaten übernommen.

## Verträge und unabhängige Zahlenbeispiele

| Bereich | Vorschau | Methode der Projektakte | Rechenvertrag |
|---|---|---|---|
| Wind | `POST /api/v1/technical/roof/wind` | `roof.wind` | Außendruck `we=qe*cpe`, Innendruck `wi=qi*cpi`; lokaler Nettodruck nach innen `p=we-wi`, Normalkraft `F=p*A`, Außensog `max(0,-p)`. Innere und äußere Bezugsdrücke sind getrennt. |
| Schnee | `POST /api/v1/technical/roof/snow` | `roof.snow` | Vertikale Last je horizontaler Projektionsfläche `s=mu*Ce*Ct*sk`, Vertikalkraft `F=s*A`. Nur ausdrücklich gleichzeitige, überschneidungsfreie Flächen desselben Falls addieren. |
| Abfluss | `POST /api/v1/technical/roof/drainage` | `roof.drainage` | Hauptbedarf `Qd=rd*A*C/10000`, Starkregenzufluss `Qs=rs*A/10000`. Es erfolgt keine Rückhaltegutschrift beim Starkregen. |

Beim Restabflussszenario `ResidualAfterPrimaryDesignFlow` wird höchstens `min(Qd, belegte Hauptkapazität)` angerechnet. Beim Szenario `PrimaryUnavailable` ist diese Anrechnung null. Notabflussbedarf ist `max(0, Qs-Anrechnung)`. Für beide getrennten Wege wird der positive Fehlbetrag gegenüber der angegebenen jeweiligen Kapazität ausgegeben. Überschüsse anderer Einzugsflächen werden niemals verrechnet. Das sind **arithmetische Vergleiche**, keine Bestätigung, dass ein konkreter Ablauf, Leitungsweg oder Notüberlauf diese Menge tatsächlich erreicht.

- Wind: `qe=0.8 kN/m²`, `cpe=-1.5`, `qi=0.6 kN/m²`, `cpi=0.2`, lokale Fläche `12 m²` ergeben `we=-1.2`, `wi=0.12`, `p=-1.32 kN/m²`, `F=-15.84 kN`. Kräfte verschiedener Dachrichtungen werden nicht zu einer skalaren Gebäudekraft addiert.
- Schnee: `sk=1.8 kN/m²`, `Ce=0.8`, `Ct=1`, `mu=0.8` ergeben `1.152 kN/m²`. Auf `50 m²` horizontaler Projektion sind das `57.6 kN`. Eine zweite gleichzeitige Zone mit `mu=1.2` und `30 m²` ergänzt `51.84 kN`, zusammen `109.44 kN`.
- Abfluss: `rd=300 l/(s*ha)`, `rs=600 l/(s*ha)`, `A=200 m²`, `C=0.8` ergeben Hauptbedarf `4.8 l/s` und Starkregenzufluss `12 l/s`. Bei Hauptkapazität `4 l/s` und Notkapazität `6 l/s` fehlen im Restmodus `0.8 l/s` Hauptkapazität und `2 l/s` Notkapazität. Beim Hauptausfall fehlen `6 l/s` Notkapazität.

Diese Werte sind synthetisch. Sie sind keine Vorgaben für einen Standort oder eine Dachkonstruktion. Die Grenzen (höchstens 32 Bereiche, Fläche höchstens 1 Mio. m², Regen höchstens 10.000 l/(s*ha), weitere explizite Kennwertgrenzen) begrenzen den Softwarevertrag und definieren keinen baulich zulässigen Einsatzbereich. Fehlende JSON-Zahlen, auch zulässige Nullwerte, müssen ausdrücklich vorhanden sein; sie werden nicht durch implizite Standardwerte ersetzt.

## Fachliche Grenzen und Abhängigkeiten

| Aufgabe | Bereits nutzbarer Teil | Vor vollständiger Umsetzung/Abnahme erforderlich |
|---|---|---|
| DP-11-06 | Quellengebundene lokale Druck-/Sogberechnung pro Zone | Standort, Windrichtung, Geländedaten und Bezugshöhen; gültige Regelwerks-/NA-Auswahl, korrekte Zonen und Beiwerte; alle maßgebenden Fälle; Lastkombinationen und dynamische Antwort; Untergrund, Befestiger, Widerstände und unabhängige Prüfung. |
| DP-11-07 | Unabhängige Einzugsflächen und zwei ausdrücklich getrennte Notabflussszenarien | Standortregen und wirksame Flächen; nachgewiesene Kapazitäten bei den maßgebenden Wasserständen; Höhen und Fließwege, hydraulisches Leitungsnetz, Rückstau-/Überstau-/Versagensfälle; Auswahl des fachlich erforderlichen Szenarios und unabhängige Prüfung. |
| DP-11-08 | Ein vorgegebener Schneelastfall auf horizontalen Zonen | Standort-/Höhendaten und nationale Anwendung, Dachform, Verwehung/Abrutschen und außergewöhnliche Fälle; alle maßgebenden Lastanordnungen, Kombinationen, tragende Bauteile/Verbindungen und unabhängige Prüfung. |

Die Rechenläufe bleiben `isNormative=false`, `structuralDesignApproved=false` beziehungsweise `hydraulicDesignApproved=false`. Es gibt keine Zusage offizieller Unterstützung eines vollständigen DIN-/EN-Verfahrens. Die Berechnung funktioniert innerhalb des beschriebenen Zahlenvertrags; fachliche Empfehlung und Sicherheit einer realen Konstruktion lassen sich daraus nicht ableiten.

## Quellenprüfung und Rechte

Am 22.10.2026 wurden folgende Originalquellen für die grundsätzliche Gleichungsstruktur und deren Abgrenzung gelesen. Die historischen JRC-Handbücher liefern Anschauungsbeispiele und **keinen Beleg für den aktuell geltenden deutschen Norm-/NA-Stand**. Es wurden keine Standorttabellen, Druckbeiwerttabellen oder Herstellerkennwerte importiert.

- [JRC-Handbuch 3, Wind Actions, Abschnitt 4](https://eurocodes.jrc.ec.europa.eu/sites/default/files/2021-12/handbook3.pdf): getrennte Außen-/Innendrücke und Bezugsgrößen, lokale Flächennormale. Keine Übernahme seiner Beispiel- oder Standortwerte als Anwendungsstandard.
- [JRC-Handbuch 4, Kapitel 5, Abschnitt 3.2](https://eurocodes.jrc.ec.europa.eu/sites/default/files/2021-12/handbook4.pdf): öffentliches Zahlenbeispiel zur Multiplikation der vorgegebenen Schneekoeffizienten. Der Test verwendet ungerundete Produkte; die gerundete Tabellenangabe wird nicht als exakter Wert behandelt.
- [Sita: Notentwässerung](https://www.sita-bauelemente.de/anwendungen/notentwaesserung/): Abgrenzung von Berechnungs- und Starkregen sowie eigenständiger Notentwässerung. Herstellerfreigaben oder geltende DIN-Volltexte werden dadurch nicht ersetzt.

## Prüfpfade

- [Core-Regressionen](../tests/FirstBlick.Core.Tests/Technical/RoofActionsTests.cs): unabhängige Zahlen, Richtung, Einheiten, keine Flächenquerverrechnung, Ausfallfall, Grenzen, Pflichtfelder und gespeicherte Integrität.
- [HTTP-/Identity-/PostgreSQL-Tests](../tests/FirstBlick.Api.Tests/RoofActionsApiTests.cs): Vorschau, erneute Berechnung, Speicherung, Idempotenz, Mandanten, Rollen, Module und fehlende JSON-Pflichtwerte.
- [Clientfälle](../web/tests/roof-actions.test.ts) und [Browser-Fixtures](../web/tests/browser/roof-actions.spec.ts): vollständige Bedienung auf drei Viewports, Quellen-/Zahlenbindung, Eingabewechsel, Export, Projektablage, widersprüchliche Antworten und verspätete Ergebnisse. Die Browserantworten sind kontrollierte Fixtures, keine echte HTTP-/Datenbank-Abnahme.
- [Containerablauf](../scripts/roof_actions_smoke.py): tatsächliche API, PostgreSQL, Versionen im S3-Speicher, quellgebundene PDF-Bytes, Idempotenz und Wiederlesen derselben Originale nach API-/Worker-Neustart. Eingebunden in Operations vor und nach Sicherung/Neustart.

Erfolgreiche CI-, Standards- und Operations-Läufe sind für denselben final veröffentlichten Commit zu prüfen. Testquellen und frühere grüne Läufe sind kein Nachweis, dass ein späterer Commit bestanden hat.

## Review und Abhängigkeiten

Das manuelle Diff-Review prüft serverseitige Rechte/Module/Mandanten, Pflichtwerte ohne stille Nullannahmen, begrenzte Zahlen/Listen, eingefrorene Eingaben, verzögerte Antworten und kontrollierte Export-/Projektbindung. Das ist keine unabhängige Penetrationsprüfung oder vollständige Repository-Sicherheitsabnahme.

Die npm-Prüfung vom 22.10.2026 fand im vorhandenen Angular-Build-Werkzeug einen verwundbaren Vite-Zweig. Ein gezieltes npm-Override ersetzt ausschließlich dessen Vite durch **8.3.5**; Angular bleibt 22.2.3. Das ist eine lokal und in CI zu prüfende Integrationsentscheidung, **keine von Angular bestätigte Freigabe dieser abweichenden exakten Abhängigkeit**. Das Override sollte entfernt werden, sobald der gewählte Angular-Build-Stand selbst einen bereinigten Vite-Stand verwendet.

Primärquellen: [Vite-Dateizugriffsadvisory](https://github.com/advisories/GHSA-rq7h-c2jc-7f22) und [Vite 8.3.5](https://github.com/vitejs/vite/releases/tag/v8.3.5). Die npm-Metadaten von `@angular/build@22.2.3` und `@angular/build@22.2.4` nennen weiterhin exakt Vite 8.3.0; ein Angular-Patchwechsel allein würde diese exakte Abhängigkeit nicht bereinigen. Die vorgefundene Lockdatei enthielt Vite 8.3.0 unter dem Build-Paket. Der Entwicklerserver bleibt auf Loopback beschränkt. CI führt nun `npm audit --audit-level=moderate` einschließlich Entwicklungsabhängigkeiten aus. Audit-Ergebnisse sind zeitabhängig und werden nicht als dauerhafte Sicherheitsgarantie bezeichnet.
