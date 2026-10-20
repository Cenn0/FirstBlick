# Rechteckige lineare Gefälledämmung – Mengenhilfe, keine Verlegeplanung

Stand: 20.10.2026. Das Verfahren `roof.tapered-insulation` / `rectangular-linear-tapered-insulation` v1.0.0 ergänzt DP-11-05 um **eine begrenzte, überprüfbare Materialvolumenberechnung**. Es ist keine komplette Verlegeplanung oder bautechnische Freigabe.

## Eingabe und Einheiten

`lengthM`, `widthM`: Seitenlängen eines *horizontal projizierten*, rechteckigen Grundrisses in Metern, je 0,01–1.000 m. `minimumThicknessM`: senkrechte Dicke am Punkt (0,0), 0,01–2 m. `lengthGradientPercent` und `widthGradientPercent`: nichtnegative konstante Anstiege von 0–10 % in +x bzw. +y (Höhendifferenz/Horizontaldistanz × 100). `measurementSource`: erforderliche frei benannte Referenz auf die eingegebenen Maße/Anstiege. Maximale resultierende Dicke: 2 m.

Die Dickenverteilung auf dem Rechteck lautet `t(x,y) = t0 + (gx/100)*x + (gy/100)*y`. Das Volumen des kontinuierlichen Dämmkörpers über dem **horizontalen** Grundriss ist exakt

```text
A = L * W
t_max = t0 + L*gx/100 + W*gy/100
t_mean = (t0 + t_max)/2
V = A * t_mean
```

Referenz mit ausschließlich synthetischen Daten: `L=12 m, W=10 m, t0=0,08 m, gx=0 %, gy=2 %` ergibt `t_max=0,28 m`, `t_mean=0,18 m` und `V=21,6 m³`. Vier unverwechselbare Eckwerte dienen der visuellen Kontrolle. Ein gleichmäßig dicker Aufbau (beide Gradienten 0) wird ohne Sonderfall berechnet.

## API, Berechtigungen und Evidenz

`POST /api/v1/technical/roof/tapered-insulation`: authentifizierte, rollengebundene, nicht buchende Vorschau. `POST /api/v1/technical/runs` mit `method="roof.tapered-insulation"`: bewusste, mandantengebundene und auditierte Ablage mit Eingabedigest und unverändert wiederprüfbarem Ergebnis. Beide Endpunkte verwenden die bestehende technische Autorisierung. Ergebnisse sind immer `isNormative=false`.

Core-Tests decken die unabhängigen 21,6-m³- und 4,32-m³-Referenzen, alle Eckdicken, Nullgefälle, ungültige Grenzen, Extremwerte und Digeständerungen ab. Echte HTTP-/PostgreSQL-Tests prüfen Zugriff, persistierte Evidenz und ungültige Daten.

## Grenzen und offene DP-11-05-Teile

**Nicht berechnet:** Gefällekeile aus einem konkreten Herstellerkatalog, Plattengrößen, Treppen- und Stufenraster, Zuschnitt, Abfall, Preis, Befestigung, Materialzulässigkeit, tatsächliche Ablaufpositionen, Tiefpunkte/Sättel/Gegengefälle, Attika, Lichtkuppeln, Dachöffnungen, projektierte Entwässerungswege oder Leistungsnachweise. Ein aus der geometrischen Mengenhilfe abgeleiteter Einkauf oder Verlegeplan ohne separate Prüfung wäre sachlich falsch. Für diese Aufgaben bleiben Herstellerdaten, reale Dachgeometrie, Prüfverfahren und betriebliche Freigabe erforderlich.

Code: `src/FirstBlick.Core/Technical/TaperedInsulationCalculator.cs`; Tests: `tests/FirstBlick.Core.Tests/Technical/TaperedInsulationTests.cs` und `tests/FirstBlick.Api.Tests/TaperedInsulationApiTests.cs`. Eine erfolgreiche CI bestätigt die bezeichneten Testfälle, keine Norm- oder Herstellerfreigabe.
