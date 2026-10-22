# Photovoltaik – geometrische Modulbelegung (Teilumfang DP-11-09)

Stand: 22.10.2026 · FirstBlick · **nicht normativer Geometrieentwurf**.

Mit `POST /api/v1/technical/roof/pv-layout` kann eine rechteckige, **auf der Dachfläche gemessene** Ebene
mit nominalen PV-Modulen belegt werden. Die Eingabe umfasst:

- Breite und Höhe auf der geneigten **Dachebene** (m, nicht Grundrissprojektion),
- Modulbreite, Modulhöhe und deklarierte Nennleistung (Wp) einschließlich Datenquelle,
- Abstände zu sämtlichen Dachkanten, horizontale/vertikale Modulabstände und
  zusätzlichen Abstand um Hindernisse, jeweils in Metern,
- 0 bis 64 rechteckige Ausschlussflächen mit Dachflächen-Koordinaten,
- feste Hoch-/Querformat-Ausrichtung oder automatische Auswahl nach **maximaler Stückzahl**.

Die Platzierung beginnt links unten an der eingestellten Randlinie und verwendet ein festes
rechteckiges Raster. Ergebnisse: sämtliche Positionen (x/y/Breite/Höhe), gewählte
Ausrichtung, Anzahl geprüfter Rasterplätze, abgewiesene Plätze, belegte Fläche und
Nennleistung `Stückzahl × Modul-Nennleistung`. Bei Gleichstand gewinnt Hochformat.
Beide Orientierungen werden **nicht** geometrisch verschachtelt; nichtrechteckige,
mehrseitige, versetzte oder gedrehte Felder sind ausgeschlossen. Ein Hindernis sperrt
jeden Rasterplatz, dessen rechteckige Modulfläche dessen Ausschlussrechteck nach
Berücksichtigung des expliziten Abstands schneidet. Ein bloßes Berühren bei Abstand 0
gilt nicht als Schnitt.

Für die Eingaben gelten endliche Zahlen, positive Dimensionen und obere Budgetgrenzen.
Spätestens nach 2.500 Kandidaten pro geprüftem Raster wird abgebrochen. Das Ergebnis
liefert eine versionierte SHA-256-Provenienz und kann mit `method=roof.pv-layout`
über `POST /api/v1/technical/runs` als projektreferenzierter, erneut berechenbarer,
unveränderlicher technischer Originalstand gespeichert werden. Lesen/Schreiben bleibt
an Betriebsidentität und `technical-results`-Rechte gebunden.

## Grenzen und ausdrückliche Nichtzusagen

**Keine Montage-, Brandschutz-, Elektro-, Statik-, Windsog-, Schneelast-, Abstands-,
Verschattungs-, Ertrags- oder Genehmigungsplanung.** Die Randabstände sind
Nutzereingaben und keine behördlichen oder herstellerbezogenen Mindestwerte.
Eine Modulkennlinie, Wechselrichter, Verschaltung, Verschattung, Tragfähigkeit oder
die tatsächliche Modul-/Unterkonstruktionskompatibilität werden nicht geprüft.
Wp ist Nennleistung und keine zugesicherte Jahresenergie.

**Fachlicher Status:** Ein unabhängig bestätigter Datenstand für Standort,
Hersteller, Dachaufbau, Brandschutz, strukturelle Lasten und elektrotechnische
Auslegung ist weiterhin erforderlich. Deshalb verbleibt DP-11-09 als
**technisch teilweise implementiert**, nicht als fachlich abgenommen.

## Nachweise

- Rechenkern: `src/FirstBlick.Core/Technical/PvModuleLayout.cs`
- Corefälle: `tests/FirstBlick.Core.Tests/Technical/PvModuleLayoutTests.cs`
- Autorisierung, HTTP, PostgreSQL und Projektnachweis:
  `tests/FirstBlick.Api.Tests/PvLayoutApiTests.cs`
- CI-Ausführung und Operations sind jeweils für den **konkret veröffentlichten Commit**
  nachzuweisen; das Vorhandensein einer Testdatei ersetzt keinen bestandenen Lauf.
