# ISO-13788-Rechenkern: unabhängige Gleichungsprüfungen und Referenzabweichungen

Die unabhängige Implementierung der Monats-Diffusionsbilanz nutzt gespeichertes Grenzflächenwasser, mehrere Kondensationsebenen und einen Wechsel der Druckverteilung beim tatsächlichen Austrocknen einer Ebene innerhalb eines Monats. Sie ist keine dynamische Wärme-/Feuchtesimulation und keine vollständige DIN-4108-3-Beurteilung. Originalnormen, Tabellenabbildungen und deren Texte werden hier nicht weiterveröffentlicht.

## Akzeptanzgrundlage

Die Tests vergleichen nicht nur zwei Aufrufe desselben Lösers. Für einen einzelnen Kondensationspunkt werden monatliche Zu- und Abflüsse unmittelbar aus den beidseitigen Dampfdruckdifferenzen und sd-Werten hergeleitet. Ein unabhängiger Vier-Punkt-Fall prüft zwei gleichzeitig kondensierende Ebenen über ein vollständiges Jahr. Ein weiterer Fall prüft gleichzeitige Kondensation an einer und Verdunstung an einer anderen Ebene. Masseverluste, negatives gespeichertes Wasser und ein bloßes Zurücksetzen jedes Monats werden dadurch erkannt.

Die Austrocknungsprüfung aus Abschnitt 7 beginnt mit der vorgegebenen Vergleichsmenge von 1 kg/m², nicht mit einer erfundenen tatsächlichen Anfangsfeuchte. Der Vergleich aus Anhang D erreicht die vollständige Austrocknung nach 83 Monaten. Im ersten Monat wird der Zeitpunkt, an dem die ursprünglich nasse Schichtmitte trocken ist, zusätzlich unabhängig berechnet; für den Restmonat bleibt Wasser an einer anderen Grenzfläche bestehen. Das Austrocknen der zuerst gewählten Ebene ist daher ausdrücklich kein Austrocknen des gesamten Aufbaus.

## Informative Tabellen nicht als exakte Gleichungswerte ausgeben

Die auf fünf Dezimalstellen gedruckten Ergebnisse des Ein-Ebenen-Beispiels in Anhang C werden nicht als exakte Rechenwerte eingesetzt. Für die ausdrücklich verwendeten Randdaten und kalendarischen Monatstage erhält die Gleichungsprüfung einen Spitzenwert von etwa 0,000567 kg/m² bei normaler Feuchte und einen Jahresrest von etwa 0,000930 kg/m² bei hoher Feuchte. Die publizierten gerundeten Vergleichswerte weichen geringfügig ab. Diese Abweichungen werden in den Tests ausgegeben; die strenge Akzeptanz erfolgt gegen die unabhängige Flussgleichung, zusätzlich bleibt ein ausdrücklich ausgewiesener kumulierter Tabellen-Rundungsvergleich erhalten. Es wird keine exakte Tabellenreproduktion behauptet.

**Ungeklärter Referenzwiderspruch:** Mit den wörtlich als Zahlen übernommenen Schichtwiderständen des informativen Zwei-Ebenen-Beispiels C.4 und den Klimadaten C.1 liegt im Dezember der innere Dampfdruck unter dem Sättigungsdruck an der dort bezeichneten inneren Grenzfläche. Die in C.5 genannte innere Kondensation wird mit diesen Eingaben nicht reproduziert. Der Rechenkern wird nicht so verändert, dass er aus einer physikalisch untersättigten Grenze künstlich eine Kondensationsebene macht. Ein eigener Regressionstest hält genau diesen Widerspruch sichtbar. Er ist **kein bestandener C.5-Konformitätsreferenzfall**. Die Ursache ist nicht abschließend verifiziert; ein Druckfehler oder abweichende ursprüngliche Eingaben werden nicht als gesicherte Erklärung ausgegeben.

Beim Anhang-D-Vergleich unterscheidet sich die exakte erste Monatsbilanz von einer Berechnung, die die Druckverteilung erst an der Monatsgrenze neu bestimmt. Der implementierte Ereigniswechsel verwendet nach dem Austrocknungszeitpunkt die verbleibenden Sekunden; eine separate algebraische Zwei-Phasen-Rechnung prüft genau diese Menge. Die vollständig trockene Monatszahl bleibt 83.

## Bedeutung für die Ausgabe

`standardProcedure` benennt das tatsächlich implementierte ISO-Verfahren, dessen Annahmen und die verwendete Version. `constructionComplianceAssessed=false` und `din4108Part3Assessment=false` verhindern eine pauschale Aussage über einen konkreten Dachaufbau oder nationale Zulässigkeitsregeln. Die Herkunftskennzeichnung des bestehenden Systems wird nicht still in ein Bauwerkszertifikat umgedeutet. Materialschädigung, Abtropfen, reale Klimawahl und Einhaltung der Verfahrensgrenzen müssen anhand des konkreten Projekts überprüft werden. Der offene informative Referenzwiderspruch bleibt Bestandteil des Prüfnachweises.

Quellenbezug: ISO 13788:2012, Abschnitte 4, 6, 7 und Anhänge C, D, E; Metadaten: https://www.iso.org/standard/51615.html . Der vollständige Normtext wird nicht in diesem Repository bereitgestellt. Tests: `tests/FirstBlick.Core.Tests/Technical/Iso13788BalanceTests.cs`.
