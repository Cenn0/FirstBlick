# Kumulative Rechnungsrundung

## Behobene Rechenfehler

Eine beauftragte Position mit drei Einheiten zu 0,01 EUR netto und explizit eingetragenen 19 % Umsatzsteuer hat 0,03 EUR netto und 0,01 EUR Steuer. Werden eine Einheit und anschließend zwei Einheiten getrennt jeweils neu berechnet, ergeben beide Steuerrechnungen für sich 0,00 EUR. Im bisherigen Ablauf konnte die Schlussrechnung deshalb den eingefrorenen Auftrag nicht abschließen. Derselbe Fehler verhinderte, dass zwei Mengenkorrekturen zusammen den gesamten ursprünglichen Steuerbetrag zurücknehmen.

Seit `firstblick.invoice-cumulative/1.0.0` bezieht sich die Berechnung auf die ursprünglichen, eingefrorenen Positionen. Im Beispiel entstehen:

| Beleg | Menge | Netto | Steuer | Brutto |
|---|---:|---:|---:|---:|
| Teilrechnung | 1 | 0,01 EUR | 0,00 EUR | 0,01 EUR |
| Schlussrechnung | 2 | 0,02 EUR | 0,01 EUR | 0,03 EUR |
| Summe | 3 | 0,03 EUR | 0,01 EUR | 0,04 EUR |

Die bereits ausgestellte Teilrechnung bleibt unverändert. Auch Netto-Cents werden kumulativ berücksichtigt: Eine halbe Einheit zu 0,03 EUR kann zunächst 0,02 EUR netto beanspruchen. Der übrigen halben Einheit verbleiben anschließend genau 0,01 EUR netto des insgesamt vereinbarten Nettobetrags von 0,03 EUR.

## Berechnungsvertrag

`InvoiceArithmetic` verwendet `decimal` und dieselbe explizite Rundung wie die ursprüngliche Angebotskalkulation: zwei Nachkommastellen, Mittelpunkte von null weg. Währung ist weiterhin EUR; Steuersätze werden aus den eingefrorenen Eingaben übernommen.

Für Auftragsrechnungen gilt je ursprünglicher Quellposition:

1. Die kumulative Menge wird zuerst der Auftragsposition und danach den übernommenen Nachtragspositionen zugeordnet. Nachträge sind nach Annahmezeit und anschließend ihrer ID geordnet. Die ursprüngliche Reihenfolge innerhalb eines Quellbelegs bleibt erhalten.
2. Für eine unvollständig abgerechnete Quellposition wird der kumulative Nettobetrag aus ihrem eingefrorenen effektiven Einzelpreis und der kumulativen Menge gerundet. Die kumulative Steuer wird aus diesem Nettobetrag und dem eingefrorenen Steuersatz gerundet.
3. Die vollständige Menge einer Quellposition übernimmt deren exakte ursprüngliche Netto- und Steuerbeträge.
4. Die neue Rechnungsposition enthält den neuen kumulativen Betrag abzüglich der tatsächlich zuvor ausgestellten Beträge. Bereits ausgestellte Korrekturen mindern sowohl die vorherigen Mengen als auch die vorherigen tatsächlichen Beträge.

Identische Positionseingaben werden für die Mengenzuordnung zusammengefasst. Der Schlüssel umfasst alle Preis-, Steuer-, Beschreibungs-, Katalog- und Aufmaßangaben mit Ausnahme der abzurechnenden Menge. Ihre ursprünglichen Quellpositionen bleiben trotzdem getrennt. Zwei ursprünglich einzeln gerundete Positionen werden daher nicht nachträglich zu einer anders gerundeten Position zusammengezogen.

Eine Schlussrechnung muss sämtliche verbliebenen Vertragsmengen einschließlich der enthaltenen angenommenen Nachträge abschließen. Eine fremde Position, ein veränderter Steuersatz oder eine andere Aufmaßherkunft wird abgewiesen.

## Korrekturen

Korrekturen verwenden die tatsächlichen, bereits ausgestellten Netto- und Steuerbeträge des Ursprungsbelegs. Für eine kumulativ korrigierte Teilmenge werden diese Beträge entsprechend ihrem Mengenanteil gerundet. Die vollständige Originalmenge übernimmt stets die exakten ursprünglichen Centbeträge. Davon werden die tatsächlich zuvor ausgestellten Korrekturen abgezogen.

Diese Regel ist besonders für Ursprungsbelege relevant, die selbst einen Rundungsübertrag aus einer Rechnungsfolge enthalten. Sie werden nicht anhand ihres Einzelpreises neu berechnet. Die gespeicherten Korrekturbeträge sind weiterhin positiv; das Forderungsmodell berücksichtigt sie als Minderung des referenzierten Ursprungsbelegs.

## Gespeicherte Herleitung und historische Belege

Neue Rechnungen und Korrekturen speichern `Invoice.Calculation` mit:

- Verfahrensversion, Berechnungsgrundlage und Quellenidentitäten;
- vollständigen Kopien der verwendeten ursprünglichen Positionsbeträge;
- IDs, Belegnummern und Digests der berücksichtigten vorherigen Rechnungen und Korrekturen;
- vorherigen tatsächlichen Mengen-, Netto- und Steuerständen;
- vorherigen Vergleichswerten und neuen kumulativen Beträgen je Rechnungs- und Quellposition;
- einem Digest über diese Herleitung, der wiederum in den Rechnungsdigest eingeht.

Beim Validieren werden die Positionsbeträge aus dieser gespeicherten Grundlage erneut hergeleitet. Änderungen an der Herleitung oder den ausgestellten Beträgen werden abgewiesen. Die Referenzen halten die Quellbelege in der Datensatzhistorie fest. Die berechneten Werte werden in API/JSONB weiterhin als Dezimalzeichenfolgen transportiert.

Historische Rechnungen ohne `Calculation` behalten ihren ursprünglichen Digest und ihr bisheriges Prüfverfahren. Sie werden nicht umgeschrieben. Neue Rechnungen können ihre tatsächlich ausgestellten Werte berücksichtigen. Eine Abweichung zwischen dem historischen Betrag und dem heutigen kumulativen Vergleichswert bleibt in der neuen Herleitung sichtbar.

Der PDF-Renderer `firstblick-business-pdf/1.1` zeigt für neue Belege den Einzelpreis, den tatsächlichen Positionsbetrag und die vorherigen/neuen kumulativen Stände getrennt an. Er stellt einen gerundeten kumulativen Differenzbetrag nicht als unmittelbares Ergebnis von Menge mal Einzelpreis dar. Die Originalbeträge kommen ausschließlich aus dem validierten Rechnungssnapshot.

## Grenzen und Fehlerverhalten

Eine **Schlussrechnung mit 0,00 EUR** kann nun ausschließlich als technisch belegter Mengenabschluss ausgegeben werden, wenn alle folgenden Voraussetzungen gleichzeitig vorliegen: Die kumulative Berechnung ist an den angenommenen Auftrag gebunden; es existiert bereits mindestens ein berücksichtigter früherer Beleg mit tatsächlich positiv abgerechneten Centbeträgen; die Schlussrechnung erfasst positive verbleibende Mengen und schließt *alle* angenommenen Vertragsmengen vollständig ab; für jede neue Position sind die zusätzlichen Netto- und Steuerbeträge genau null. Die Herleitung, die vorherigen Belege, die vollständigen Mengen und der unveränderliche Rechnungsdigest werden gespeichert. Das PDF kennzeichnet den Mengenabschluss und den fehlenden zusätzlichen Zahlbetrag ausdrücklich. Eine solche Schlussrechnung erzeugt eine neue Belegnummer, aber keine zusätzliche Forderung.

Eine eigenständige Null-Euro-Erstrechnung, eine Null-Euro-Teilrechnung und eine Null-Euro-Korrektur bleiben verboten. Ebenso bleiben negative Centdifferenzen, Übermengen und unvollständig abgerechnete Schlussmengen unzulässig. Das Verfahren entscheidet weder über die rechtliche Notwendigkeit noch die umsatzsteuerliche Anerkennung eines Null-Euro-Schlussbelegs; diese Abnahme bleibt ausstehend.

Überschreiten historische Rechnungen oder Korrekturen die verfügbaren ursprünglichen Mengen oder Centbeträge, wird die Folge ebenfalls abgewiesen. Wiederholte Quellen, doppelte Belege und Korrekturen ohne ihren Originalbeleg dürfen die Berechnungsgrundlage nicht verändern.

Der Rechenweg begrenzt Quellen auf 1.000 Belege, Quellpositionen und expandierte Zuordnungen auf jeweils 10.000, frühere Belege auf 10.000 und deren in der Berechnung zu berücksichtigende Positionen auf 100.000. Zusätzlich gilt das vorhandene Speicherbudget von 1,9 MB je Entität. Ein überschrittener Grenzwert beendet den API-Befehl ohne Teilbuchung; Idempotenz, Nummernvergabe, Audit und aktuelle Mengenprüfung gehören zur selben mandantengebundenen Transaktion.

Diese Budgets begrenzen die Berechnungsgrundlage und deren expandierte Evidenz. Die Ausgabe-Endpunkte laden Rechnungen und Nachträge derzeit noch über mandantenweite `Store.All`-Abfragen; der Workflow validiert relevante historische Rechnungen vor dem Budgetcheck in `InvoiceArithmetic.ForOrder`. Damit sind weder der anfängliche SQL-Ladeaufwand noch der vorgelagerte Validierungsaufwand durch diese Grenzwerte vollständig begrenzt. Auftragsbezogene, begrenzte Quellabfragen bleiben eine eigene Folgeaufgabe.

Dieses Verfahren definiert die implementierte kaufmännische Arithmetik. Es behauptet keine Umsetzung eines XRechnung-/ZUGFeRD-Profils und entscheidet nicht, welche steuerliche oder vertragliche Abrechnungsmethode im konkreten Fall erforderlich ist. Solche Profile und deren unabhängige Referenzfälle bleiben gesonderte Roadmap-Aufgaben.

## Prüfstellen

- [Kumulativer Rechenkern](../src/FirstBlick.Core/Business/InvoiceArithmetic.cs) und [Rechnungsworkflow](../src/FirstBlick.Core/Business/Finance.cs)
- [Domänenregressionen](../tests/FirstBlick.Core.Tests/Business/InvoiceCumulativeRoundingTests.cs): Centüberträge, getrennte Quellpositionen, Steuersätze, Korrekturen, Altrechnungen, Null-Euro-Mengenabschluss mit Negativabgrenzung, Duplikate, Änderungen und Budgets
- [Echte HTTP-/PostgreSQL-Fälle](../tests/FirstBlick.Api.Tests/InvoiceRoundingApiTests.cs): JSONB, Idempotenz, konkurrierende Schlussrechnungen, Korrekturen, Manipulation und Mandantengrenze
- [PDF-Regressionsfälle](../tests/FirstBlick.Api.Tests/InvoicePdfTests.cs): Aufmaßherkunft, Netto-/Steuerübertrag und Ablehnung veränderter ausgestellter Werte

Die Ausführungsergebnisse werden jeweils für den tatsächlich geprüften Quellstand festgehalten; vorhandene Testdateien sind für sich kein Ausführungsnachweis.

### Fokussierte Ausführung am 20.10.2026

Die wiederhergestellte Implementierung wurde mit .NET SDK 10.0.402 in Release-Konfiguration geprüft. Die API-Fälle verwendeten eine isolierte PostgreSQL-16-Datenbank mit verpflichtend aktivierter Datenbankausführung.

| Ausgeführter Filter | Bestanden | Fehlgeschlagen | Übersprungen |
|---|---:|---:|---:|
| `InvoiceCumulativeRoundingTests` | 21 | 0 | 0 |
| `InvoiceRoundingApiTests` | 5 | 0 | 0 |
| `InvoicePdfTests` | 3 | 0 | 0 |

Die 21 Core-Fälle prüfen unter anderem Netto- und Steuerüberträge, ursprüngliche Einzelpositionsrundung, Korrekturen tatsächlicher ausgestellter Beträge, unveränderte historische Digests und die Evidenzbudgets. Die fünf HTTP-Fälle prüfen zusätzlich JSONB-Roundtrips, Idempotenz, konkurrierende Schlussrechnungen, echte Korrekturbuchungen, Mandantentrennung und die Ablehnung manipulierter gespeicherter Berechnungsevidenz bei Forderungs- und PDF-Abfragen. Die drei PDF-Fälle umfassen die bereits bestehende Teilaufmaßregression sowie jeweils einen Netto- und Steuerübertrag.

Die beiden neu erzeugten synthetischen Übertrags-PDFs wurden mit `pdftotext -layout` ausgelesen und mit `pdftoppm` gerendert. Die gerenderten Seiten wurden visuell geprüft: jeweils eine Seite, sichtbare tatsächliche Centbeträge und kumulative Stände, keine abgeschnittenen Zeilen. Der [maschinell lesbare Nachweis](evidence/invoice-cumulative-rounding.json) hält die tatsächlich ausgeführten Filter, TRX-Zähler, Dateihashes und diese begrenzte PDF-Prüfung fest. Diese fokussierten Ergebnisse sind keine Aussage über die gesamte Backend-Suite oder eine fachrechtliche Zertifizierung.

## Mandantenbezogene Quellenabfragen (Folgecheckpoint)

Die Rechnungsausgabe, die auftragsbezogene Kostenvergleichsvorschau und die Rechnungs-Saldenabfrage lesen ihre Ausgangsbelege nicht mehr über `Store.All` für den gesamten Mandanten. Stattdessen werden die gespeicherten, mandantengebundenen Entitätslinks zu Auftrag bzw. Ursprungsrechnung auf der Datenbankseite abgefragt. Die Leseseite lädt nur unverfallene, nicht archivierte, zugehörige Datensätze. Bevor eine Rechnung erstellt wird, werden die referenzierten Datensätze auf ID, Tenant, Revision, Link und Domain-Invarianten geprüft.

Dies ist bewusst **keine** unvollständige Paginierung: Eine Überschreitung des 10.000er-Rechnungsbudgets bzw. 10.000er-Zuordnungsbudgets bricht mit `financial_source_budget` ab. Eine Abweichung zwischen Metadaten und gespeicherter Entität bricht mit `financial_source_metadata` ab. Die Prüfung erfolgt innerhalb der bisherigen mandantenbezogenen Schreibtransaktion, sodass die Nummernvergabe bei Ablehnung zurückgerollt wird. Fremde Aufträge und andere Betriebe dürfen die Rundung, Korrekturen oder den Saldo nicht verändern.

**Weiterhin offen:** Zahlungszuordnung und weitere Bank- und Lieferantenabläufe verwenden teilweise mandantenweite Historien; die vorhandenen Link-Schlüssel benötigen eine gemessene Datenbankindex-/Lastprüfung bei großen realistischen Beständen. Eine grüne Testsuite allein ist keine Lastabnahme.
