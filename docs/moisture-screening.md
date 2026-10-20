# Feuchteschutz – physikalisches Screening (kein normativer Nachweis)

Vertrag `moisture.screening`, Verfahrensversion `1.0.0`. Diese Funktion stellt eine **rein stationäre, eindimensionale Vorprüfung** bereit und dokumentiert Eingaben, Grenzbedingungen und Quellen. Eine vollständige Feuchteschutzbeurteilung nach DIN 4108-3:2024-03 oder ISO 13788:2012 wurde **nicht** implementiert und wird durch diesen Bericht nicht behauptet.

## Fachliche Berechnung

Eingaben (von innen nach außen): Innentemperatur/Außentemperatur in °C; relative Luftfeuchte in Prozent; explizite innere/äußere Wärmeübergangswiderstände in m²K/W; je homogene Vollschicht Dicke `d` (m), Wärmeleitfähigkeit `lambda` (W/(m K)), Diffusionswiderstandszahl `mu` (dimensionslos) und benannte Wertquelle.

1. Wärmeleitwiderstand jeder Schicht: `R = d / lambda`; `Rtotal = Rsi + sum(R) + Rse`; stationärer Wärmestrom `q = (Ti - Te) / Rtotal`.
2. Diffusionsäquivalente Luftschichtdicke `sd = d * mu`. Die anliegenden Wasserdampfteildrücke ergeben sich aus eingegebener Feuchte und temperaturabhängig approximiertem Sättigungsdruck. Bei dieser **Screening-Näherung** wird der Dampfteildruck an jeder Schichtgrenze linear über die kumulative sd verteilt.
3. Schichtgrenztemperaturen werden aus dem kumulierten thermischen Widerstand ermittelt. Vergleiche `pv` mit `psat(T)` und markiere `pv > psat` als potenzielle Übersättigung.
4. Der berechnete Innenoberflächen-RH-Wert wird dargestellt; Werte >80 % erhalten einen Prüfhinweis. **80 % ist hier kein normativer Ja-/Nein-Grenzwert für die Konstruktion.**

Das verwendete Magnus-artige Näherungsgesetz schaltet bei 0 °C zwischen Wasser-/Eisform um. Druckwerte in Pa, Wärmestrom in W/m² und U-Wert in W/(m²K). Ergebnisse enthalten normalisierte, vom Aufrufer isolierte Quelldaten, eine SHA-256-Eingabebindung, einzelne Schichtgrenzen und die explizite Kennzeichnung `requiresDetailedAssessment=true` sowie `provenance.isNormative=false`.

## APIs und gespeicherter Verlauf

- `POST /api/v1/technical/moisture/screening`: mandanten- und berechtigungsgeschützte, **nicht buchende** Vorprüfung.
- `POST /api/v1/technical/runs` mit `method = "moisture.screening"`: optional bewusste und idempotente Speicherung als `TechnicalRecord` mit Projekt, Digest, Audit und denselben Fachgrenzen. Ausgabe als PDF verwendet den allgemeinen, ausdrücklich nichtnormativen Berichtsrenderer.

Die bestehende Rollen-/Modulprüfung gilt. Die Berechnung ist unabhängig vom Quellprojekt und darf deshalb **niemals** automatisch eine bautechnische Freigabe oder eine verbindliche IsNormative-Aussage setzen.

## Was für DP-11-04 weiterhin fehlt

- Vollständige DIN-4108-3-Verfahrenswahl: normative Ausnahmekonstruktionen, Gültigkeits-/Höhen- und Nutzungsgrenzen, Dachkonstruktion, Verschattung/Farbe und erforderliche Randbedingungen.
- Monatliche Glaser-/ISO-13788-Bilanz einschließlich kondensierter Mengen, Wiederverdunstung, Jahresabschluss, kritische Oberflächenfeuchte mit vollständiger Zeitreihe und Normparameter.
- Feuchteabhängige Eigenschaften, Luftkonvektion, kapillarer Transport, Anfangsfeuchte, Regen, Innenklima, Luftschichten sowie mehrdimensionale Wärmebrücken- und Bauteilgeometrie.
- Hygrothermische Simulationsfähigkeit, unabhängige, lizenzierte Norm-/Materialreferenzfälle, externe fachliche Validierung.

Sobald der vollständige Norminhalt und die Rechte an erforderlichen Norm-/Materialwerten verfügbar sind, ist eine eigenständige versionierte Implementierung mit Zulässigkeitskontrollen und Referenzabnahme vorzusehen; die vorhandene `moisture.screening`-Verfahrenskennung darf nicht still in einen normativen Nachweis umgedeutet werden.

## Primärquellen

- DIN Media, **DIN 4108-3:2024-03**: https://www.dinmedia.de/de/norm/din-4108-3/373925063 (Neufassung, darunter Einschränkung des Periodenbilanzverfahrens auf Höhenlagen bis 700 m).
- ISO, **ISO 13788:2012**: https://www.iso.org/standard/51615.html (vereinfachte Methoden; Ausschluss u.a. kapillarer Vorgänge, Lufttransport und Feuchtespeicherung).

Quellcode: `src/FirstBlick.Core/Technical/MoistureScreening.cs`; Tests: `tests/FirstBlick.Core.Tests/Technical/MoistureScreeningTests.cs`, `tests/FirstBlick.Api.Tests/MoistureApiTests.cs`.

## Zwölf unabhängige Klimamonate (nicht als Periodenbilanz zu verstehen)

Das ergänzende Verfahren `moisture.monthly-screening` verlangt genau zwölf Monate mit je einem expliziten Klima-Datensatz (Innen-/Außentemperatur und relative Feuchte). Es berechnet zwölf **unabhängige stationäre** Dampfdruck- und Temperaturprofile, benennt Monate mit möglicher Schichtgrenz-Übersättigung oder erhöhter Innenoberflächenfeuchte und hält die Input-Provenienz fest. Ungültige oder doppelte Monatskennungen werden abgewiesen. Eine fehlende Zeitreihe darf nicht als Jahresprüfung dargestellt werden.

**Wichtig:** Diese 12 Einzelberechnungen sind ausdrücklich keine DIN-4108-3- oder ISO-13788-Monatsbilanz. Das Verfahren summiert keine Kondensatmassen, rechnet keine Monatsübergänge oder Austrocknungsreserven und trifft deshalb **keine** normative Tauwasser- oder Schimmelfreigabe. Source: `src/FirstBlick.Core/Technical/MonthlyMoistureScreening.cs`, Tests: `tests/FirstBlick.Core.Tests/Technical/MonthlyMoistureScreeningTests.cs`.
