# Fachliche P0-Referenztests – synthetische Akzeptanzfälle

Stand: 23.10.2026. Implementierung:
[`DomainAcceptanceGateTests`](../../tests/FirstBlick.Core.Tests/Technical/DomainAcceptanceGateTests.cs).
Diese Tests sind **explizite Regressionen** für unabhängig festgelegte mathematische
Erwartungswerte und fachliche Invarianten, nicht ein Bau-, Steuer- oder
Normenkonformitätsnachweis.

## Unabhängig vorgegebene Orakel

| Fall | Roadmap-Bezug | Gegebener Sachverhalt | Erwartetes Ergebnis |
| --- | --- | --- | --- |
| WIND-01 | DP-11-06 | 4 m²; qₑ=2, cₑ=-1; qᵢ=1, cᵢ=0,5 | Nettodruck -2,5 kN/m², Normalkraft -10 kN, Sog 2,5 kN/m² |
| WIND-02 | DP-11-06 | 6 m²; qₑ=1,5, cₑ=1,2; qᵢ=1, cᵢ=-0,2 | Nettodruck +2 kN/m², Normalkraft +12 kN |
| WIND-03 | DP-11-06 | Beide Druckbeiwerte null | Nettodruck/Kraft/Sog null |
| SNOW-01 | DP-11-08 | sₖ=2, Cₑ=0,8, Cₜ=0,9; disjunkte Zonen 50 m² μ=1, 25 m² μ=0,5, 10 m² μ=0 | Teilkräfte 72 / 18 / 0 kN; Summe 90 kN |
| DRAIN-01 | DP-11-07 | 100 m², 200/500 L/(s ha), primär 1,5 L/s, Notüberlauf 4 L/s, Restflussszenario | Bemessungsabfluss 2, Starkregen 5, primär anrechenbar 1,5, Notbedarf 3,5 L/s |
| DRAIN-02 | DP-11-07 | Gleiche Eingaben, Hauptentwässerung vollständig ausgefallen | Primärgutschrift 0, Notbedarf 5, Notdefizit 1 L/s |
| DRAIN-03 | DP-11-07 | Zweiter unabhängiger Bereich 200 m², Abflussbeiwert 0,8, primär 10, Notkapazität 3 L/s | Zweiter Notbedarf 6,8, Notdefizit 3,8 L/s trotz großer Kapazität im ersten Bereich |
| THERM-01 | DP-11-03 | d=0,1 m, λ=0,05 W/(m K), Rsi=0,13, Rse=0,04, ΔT=30 K | Gesamtwiderstand 2,17 m²K/W, U=1/2,17 |
| THERM-02 | DP-11-03 | Gleiche Eingaben, d=0,2 m | Gesamtwiderstand 4,17 m²K/W, U=1/4,17 |
| FIN-01 | DP-08-02/03 | Drei Einheiten zu je 0,07 € netto/19 %; zwei Teilrechnungen, vollständige Gutschrift auf erste, Schlussrechnung | Netto/Steuer pro Beleg 7/1, 7/2, -7/-1, 14/2 Cent; Gesamtsaldo 21/4 Cent |

Zusätzlich scheitern unsinnige Quellangaben und nicht eindeutige Zonen,
bevor ein fachliches Ergebnis freigegeben werden könnte.

## CI-Gate und Nachweisdisziplin

Die vorhandene `server`-CI führt das gesamte .NET-Testprojekt aus und danach
die benannte `DomainAcceptanceGateTests`-Teilmenge nochmals als
**gesondert sichtbaren, fehlerschließenden Schritt**. Dieser Schritt verlangt
keine fremden Material-/Klimadaten und verwendet ausschließlich synthetische
Eingaben. Der vollständige Build und die Suite müssen auf demselben Commit
erfolgreich sein; bloß vorhandene Testdateien sind kein Ausführungsnachweis.

`scripts/verify-domain-gate.py` prüft anschließend die tatsächliche TRX-Datei:
mindestens elf unterschiedliche ausgeführte Fälle der benannten Klasse,
ausschließlich `Passed`, konsistente Ergebniszähler und keine übersprungenen
oder unvollständigen Ergebnisse. Fehlende Dateien und eine leere oder versehentlich
falsch gefilterte Suite blockieren damit ebenfalls den Job. Sechs synthetische
Negativ-/Positivtests prüfen diese Prüfung vor dem tatsächlichen Testlauf.

Diese Orakel prüfen die beschriebene Teilrechnung und Rechnungsarithmetik,
nicht: normative Wind-/Schneezonenwahl, Statik oder Befestigung,
hydraulische Rohr-/Notentwässerungsdimensionierung, die nationale
Wärmeschutz-/Feuchteschutznorm, Steuerrecht, Lieferantenkonformität,
anwendbare Herstellerkataloge oder die betriebliche Freigabe. Ein
Fehlschlag blockiert den Merge; ein Erfolg ändert die Statuswerte
49 K / 49 T / 2 O nicht.
