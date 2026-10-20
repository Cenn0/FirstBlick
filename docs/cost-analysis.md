# Nachkalkulation aus Auftrags- und Projektbelegen

Vertrag `1.0.0`, `POST /api/v1/orders/{id}/cost-analysis`. Dieser Bericht verbindet den angenommenen Auftrag und angenommene Nachträge mit den aktuell erfassten Projektzeiten, gebuchtem Materialverbrauch und ausdrücklich zugeordneten Eingangsbelegen. Kostenansätze, Belegzuordnung, Vollständigkeit und Restkostenprognose sind benannte Eingaben des prüfenden Anwenders.

## Anfrage und Berechtigung

Der Bericht ist für Geschäftsführung und Büro verfügbar. Allgemeiner Auftragslesezugriff genügt wegen der enthaltenen personenbezogenen Kostenansätze nicht. Der Server wartet auf die Mandantensperre und prüft danach aktuelle Mitgliedschaft und Rolle erneut. Alle Quellabfragen sind mandanten- und projektgebunden. Fremde oder zu einem anderen Bestellprojekt gehörende Belege werden zurückgewiesen. Antworten verwenden `Cache-Control: no-store, private`.

Beispiel für einen bewusst noch unvollständigen ersten Bericht:

```json
{
  "timeCosts": [],
  "supplierCosts": [],
  "zeroStockCosts": [],
  "remainingForecastCost": null,
  "forecastEvidence": "",
  "recordedCostsCoverage": ""
}
```

Die Antwort enthält `capturedAt`, `report` und eine Scope-Erklärung. `contractSources` nennt Auftrag und angenommene Nachträge mit Art, ID, Revision, Inhaltsdigest, Titel, Vertragsnetto und kalkulierten Kosten. `sources` nennt die Kostenbelege mit denselben Herkunftskennungen, dem ermittelten Nettoansatz oder `null`, dem benannten Bewertungsbeleg und einem gegebenenfalls fehlenden Prüfschritt. Die Antwort bewahrt sämtliche `reviewedInputs`; ihr eigener Digest umfasst Bericht, Quelle und Bewertungen, aber nicht die separat ausgegebene Abrufzeit.

## Kostenbewertung

| Quelle | Berechnung und Voraussetzungen |
| --- | --- |
| Freigegebene Zeitbuchung | Exakte verstrichene Zeit abzüglich ihrer Pausen, multipliziert mit `timeCosts[].hourlyCost`; einmalige kaufmännische Cent-Rundung. `timeEntryId` und `evidence` sind Pflicht. Unfreigegebene oder unbewertete Zeit bleibt unbekannt. |
| Gebuchter Materialverbrauch | Verbrauchsmenge × dokumentierter Stückkostenansatz. Rückgaben reduzieren Kosten. Wareneingänge werden nicht zusätzlich als Projektverbrauch gezählt. |
| Material mit Stückkosten null | Bleibt ohne eine ausdrückliche `zeroStockCosts`-Begründung unbekannt. Ein dokumentiertes kostenloses Ersatzmaterial kann als tatsächlich null bewertet werden. |
| Freigegebener Eingangsbeleg, `ExternalService` | Expliziter Projekt-Nettoanteil `projectNet`, höchstens Netto des Belegs. Korrekturbelege reduzieren diesen Ansatz. `supplierInvoiceId` und `evidence` sind Pflicht. |
| Freigegebener Eingangsbeleg, `StockValuation` | `projectNet` muss null sein: Die Materialkosten werden bereits über den gebuchten Projektverbrauch bewertet. |

Eingangsbelege der zum Projekt gehörenden Bestellungen werden automatisch als zu prüfende Quellen aufgenommen. Belege ohne Bestellverknüpfung können durch eine ausdrückliche `supplierCosts`-Auswahl einbezogen werden. Fehlende, noch nicht freigegebene oder nicht klassifizierte Eingangsbelege bleiben als offene Prüfung sichtbar. Der Bericht erfindet weder Lohn- oder Gemeinkostensätze noch eine Projektverteilung.

Alle Mengen und Geldwerte sind serverseitig `decimal`; das API überträgt sie als invariante Dezimalstrings. `recordedCostsCoverage` dokumentiert, welche erfassten Quellen der Anwender abgeglichen hat. Diese Aussage bestätigt keine unbekannten, außerhalb des Systems liegenden Kosten.

## Ergebnis und fehlende Werte

`knownActualCost` ist die Summe der bereits bewerteten Quellen. Sie ist auch dann sichtbar, wenn weitere Kosten ungeklärt sind. `actualCost` bleibt dagegen `null`, bis alle aufgenommenen Quellen bewertet und freigegeben sind und der Anwender ihren Abdeckungsumfang benannt hat. Ein negativer Kostensaldo verlangt eine gesonderte Abstimmung und liefert ebenfalls keine vollständigen Istkosten.

`remainingForecastCost` ist eine ausdrückliche, nicht negative Restkostenschätzung mit `forecastEvidence`. Ohne diese Eingabe bleibt die Fertigstellungsprognose unbekannt, auch wenn die bisherigen Istkosten vollständig bewertet sind.

| Kennzahl | Definition |
| --- | --- |
| `contractNet` | Angenommener Auftrag plus angenommene Nachträge, netto. |
| `plannedCost` | In diesen angenommenen Kalkulationen dokumentierte Material- und Lohnkosten vor Verkaufspreiszuschlag, positionsweise auf Cent gerundet. |
| `forecastCost` | Vollständig bewertete bisherige Istkosten plus ausdrückliche Restkostenprognose. |
| `forecastMargin` | Vertragsnetto minus prognostizierte Gesamtkosten. |
| `marginPercent` | Prognostizierter Überschuss geteilt durch Vertragsnetto × 100; bei Umsatz null unbekannt. |
| `markupPercent` | Prognostizierter Überschuss geteilt durch prognostizierte Gesamtkosten × 100; bei Kosten null unbekannt. |

Prozentwerte werden auf sechs Nachkommastellen gerundet. Marge und Zuschlag verwenden unterschiedliche Nenner. Die synthetische Rechenprobe mit 1.100 EUR Vertragsnetto, 464 EUR bewerteten Istkosten und 100 EUR Restkosten ergibt 536 EUR prognostizierten Überschuss, 48,727273 % Marge und 95,035461 % Zuschlag.

## Grenzen

Dieser Aufruf schreibt keine Rechnung, Zahlungszuordnung, Lohnabrechnung oder verteilte Kostenbuchung. Die vom Anwender gewählte Verteilung eines Eingangsbelegs wird nur im zurückgegebenen Bericht festgehalten; sie reserviert keinen Betrag gegenüber Berichten anderer Projekte. Eine betriebsweite verbindliche Kostenstellenverteilung benötigt einen eigenen gebuchten Verteilungsvorgang mit kumulativer Prüfung. Der aktuelle Bericht darf dafür nicht als Ersatz verwendet werden.

Die Betrachtung umfasst aktuelle, nicht archivierte Projektquellen und hat pro Quellart sowie Eingabeliste eine Grenze von 5.000 Datensätzen. Sie ist kein historischer Bericht zu einem beliebigen Stichtag. Der vollständige Bericht kann mit seinem Digest für die jeweilige Bewertung aufbewahrt werden; der Endpoint legt selbst keinen dauerhaften Berichtssnapshot an. Steuerliche Behandlung, Gemeinkostenschema, Lohnkostenbasis und fachliche Abnahme des Betriebs werden durch diese technische Berechnung nicht bestätigt.

## Nachweise

- `tests/FirstBlick.Core.Tests/Business/CostAnalysisTests.cs`: unabhängig vorgegebene Summen, exakte Zeitrechnung, Rückgaben, Teilzuordnungen, Kosten null, fehlende Bewertungen, doppelte und fremde Quellen, Marge/Zuschlag und Quellverweise für Nachträge.
- `tests/FirstBlick.Api.Tests/CostApiTests.cs`: tatsächliche HTTP-/PostgreSQL-Abfragen, alle sieben Geschäftsrollen, Mandantengrenzen, unveränderte Daten-/Audit-/Job-/Idempotenzbestände und Rollenentzug während eines beobachteten PostgreSQL-Sperrwartens.
