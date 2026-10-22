# Erweiterte Fachmasken: Eingaben, Integrität und Prüfnachweise

Diese Ergänzung betrifft die vorhandenen vier Verfahren in „Dach & Berechnung“: ISO-Monatswasserbilanz, Austrocknungsvergleich, einachsige Gefälleplatten und mehrere angrenzende Gefällefelder. Die fachlichen Anwendungsgrenzen in [Verfahren und Austausch](advanced-exchange-and-physics.md) bleiben maßgeblich. Die Änderung ergänzt Bedienung und Clientprüfung; sie erweitert keinen normativen Verfahrensumfang.

## Normaler Ablauf und Korrektur

Ein berechtigter Bearbeiter erfasst eigene Klima-, Material-, Plan- und Katalogquellen vollständig. Die Antwort muss zu diesen Eingaben passen. Danach kann er die geprüfte Evidenz als JSON herunterladen oder ausdrücklich einem aktuell gesuchten Projekt zuordnen. Die Ablage rechnet weiterhin serverseitig neu. Ein PDF muss zur gespeicherten Quelle, Revision und SHA-256 passen; die heruntergeladenen Bytes werden vor der Ausgabe geprüft.

Eingabeänderung oder Verfahrenswechsel verwirft die Vorschau. Eine neue Projektsuche, Projektauswahl oder Titeländerung verwirft die vorherige Speicher-/PDF-Verknüpfung. Ein Wechsel von Betrieb, Sitzung oder Berechtigungen invalidiert laufende Antworten. Bei einer Antwort mit 401, 403 oder 423 verschwinden auch die vorherige Vorschau und Projektbindungen. Die Invalidierung einer laufenden Antwort ist kein serverseitiger Abbruch eines bereits angenommenen Speicherauftrags.

[Clientvertrag](../web/src/app/core/advanced-technical.models.ts), [Komponente](../web/src/app/pages/advanced-technical.component.ts) und [Ansicht](../web/src/app/pages/advanced-technical.component.html) implementieren diesen Ablauf. Verglichen werden Werte und Quellen, nicht allein ein vom Server geliefertes Hashfeld. Die Eingabeprüfung erlaubt ausschließlich die dokumentierte serverseitige Sortierung von Monaten, SKUs und Feldkennungen sowie die Zahlenserialisierung eines expliziten Plattenpreises. Beim Vergleich gespeicherter Ergebnisse werden außerdem ausschließlich `pricePerBoardEur`, `estimatedPackCostEur` und `totalEstimatedPackCostEur` als centgenaue Dezimalwerte verglichen: Der HTTP-Preview verwendet den vorhandenen Dezimalstring-Converter, eingebettete Ergebnis-JSON bewahrt numerische Geldfelder. Keine allgemeine String-/Zahlenumwandlung wird zugelassen. Sie toleriert keine still geänderten Maße, Klima-, Quellen- oder Freigabewerte.

## Geprüfte Invarianten

| Bereich | Clientprüfung |
| --- | --- |
| Verfahren | Passende ID/Version, vollständige Hashkennung, nichtnormative Kennzeichnung und vorhandene Fachgrenzen |
| Monatsbilanz | Zwölf geordnete Rechenschritte, Kalendermonate/-tage, endliche Werte, fortgeschriebene Gesamt- und Grenzflächenwassermassen, Kondensation/Verdunstung, Jahresreste und Bilanzklasse |
| Grenzflächen | Vorhandene Knoten, keine doppelten Wasserzeilen, Höchstmengen und erster Höchstmonat, Restwasser; die Oberfläche zeigt diese Werte mit kg/m² |
| Austrocknung | Explizit ausgewählte homogene Schicht, 1 kg/m² am Schichtmittelpunkt, höchstens 120 Schritte, Rest-/Zeit-/Umlagerungsklasse |
| Gefälleplatten | Fläche, Koordinatengrenzen, Volumen und Verschnitt, SKU-Zugehörigkeit, Katalogmaße, Zuschnittkennzeichen und Packungsrundung |
| Mehrfeldplan | Eingabebindung der einzelnen Felder und ihrer Berechnungen, gemeinsame Positionen, quellengleiche SKU-Zusammenführung vor der Packungsrundung |
| Kosten | Stückpreis und Packungsmenge, Summenabgleich; fehlender Preis bleibt unbekannt, nicht null Euro |
| Projektakte | Betrieb, Projekt, Methode, Titel, Revision und Originaleingabe; vollständiges Ergebnis muss mit der Vorschau übereinstimmen |

Die Konsistenzprüfung ist kein zweiter normativer Rechenkern. Sie beweist weder die Wahrheit der angegebenen Quellen noch eine vollständige unabhängige geometrische, bauphysikalische oder konstruktive Prüfung. Das Clientmodul prüft die Hashformate und Quellenbindung; es behauptet keine unabhängige Neuberechnung des C#-Eingabedigest. Der PDF-Bytehash wird dagegen tatsächlich im Browser berechnet.

## Reproduzierbare synthetische Fixtures

Sechs Datensätze in [advanced-technical.json](../web/tests/fixtures/advanced-technical.json) stammen aus dem aktuellen C#-Kern mit dem tatsächlichen API-Dezimalconverter; Platten- und Mehrfeldfälle enthalten zusätzlich die echte eingebettete Projekt-Ergebnisdarstellung: saisonale Kondensation, verbleibendes Jahreswasser, Austrocknung, gerichtete Platten mit Zuschnitt, unbekannte Preise und ein Zweifeldplan. Sie enthalten keine realen Kunden-, Standort- oder Herstellerdaten.

```bash
dotnet run --project tools/FirstBlick.TechnicalFixtures -- web/tests/fixtures/advanced-technical.json
dotnet test tests/FirstBlick.Core.Tests -c Release
cd web
npm test
npm run build
npx playwright test tests/browser/advanced-technical.spec.ts
```

Der [Core-Vertragstest](../tests/FirstBlick.Core.Tests/Technical/AdvancedClientContractTests.cs) vergleicht gespeicherte Fixtures mit frisch berechneten Objekten. Der [Clienttest](../web/tests/advanced-technical.test.ts) prüft passende Ergebnisse und gezielt widersprüchliche Antworten. [Browserfälle](../web/tests/browser/advanced-technical.spec.ts) erfassen die Formulare auf Desktop, Tablet und Mobil, vergleichen Anfragen, JSON-Export und PDF-Bytes und prüfen Kontextwechsel sowie Fehler.

Diese Browserfälle verwenden kontrollierte HTTP-Antworten und einen ausdrücklich synthetischen PDF-Bytebeleg. Sie sind keine echten API-/S3-Browserketten oder PDF-Renderprüfung. Der zusätzliche [echte HTTP-/Projektaktenvergleich](../tests/FirstBlick.Api.Tests/AdvancedTechnicalWireApiTests.cs) gleicht dieselben sechs Fixtures gegen beide tatsächlichen Ausgabeformen ab. Die vorhandenen [API-Regressionen](../tests/FirstBlick.Api.Tests/AdvancedTechnicalApiTests.cs) und die separate Operations-Suite bleiben erforderlich. Maßgeblich sind ausgeführte Checks desselben finalen Commits; ältere grüne Läufe ersetzen sie nicht.

## Verbleibende Facharbeit

DP-11-04 und DP-11-05 bleiben T. Der ungeklärte ISO-Anhang-C-Vergleich, DIN-Anwendungsbedingungen, reale unabhängige Klima-/Materialreferenzen, nichtrechteckige Gefällegeometrie, Innenabläufe, Kehlen, Durchdringungen, Verschnittoptimierung und Hersteller-/Fachplanerfreigaben werden hier nicht abgeschlossen. [Der fixierte Umfang der 46 früheren Teilaufgaben](roadmap-closeout.md) unterscheidet diese und weitere Implementierungsarbeiten von fehlenden externen Nachweisen.
