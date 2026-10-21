# GAEB DA XML 3.3 – strukturelle LV-Sichtung und überprüfter Leistungskatalogimport

Dieses Dokument beschreibt den **v1-Katalogweg**. Daneben existiert bereits die [v2-Kette mit Original-XSD-Prüfung und archiviertem X84-Rückexport](advanced-exchange-and-physics.md). Aussagen über fehlende XSD-Prüfung weiter unten beziehen sich ausschließlich auf v1, nicht auf den gesamten Implementierungsstand von DP-12-02. Die [quellgebundene Preisarbeitsliste](gaeb-price-worksheet.md) ergänzt den v2-Arbeitsplatz um gesammelte Bepreisung mit gesonderter Änderungsprüfung.

Dieses Modul verwendet ein ausdrücklich eingegrenztes Formatfenster: **GAEB DA XML 3.3 / 2021-05, X83 (Angebotsaufforderung) und X84 (Angebotsabgabe)**. Die Ausgabe 2023-01 betrifft bei 3.3 das Mengenermittlungspaket X31; GAEB 3.4 aus 2026-03 ist derzeit eine **Beta-Fassung** und wird nicht stillschweigend importiert.

## Datenflüsse des v1-Katalogwegs

1. `POST /api/v1/integrations/v1/gaeb/preview` mit Original-XML (`application/xml`, UTF-8, maximal 1 MiB) zeigt SHA-256, Phase, Namespace, Version, LV-Titel, Positionen und ihre Gliederungskennungen (`RNoPart` inklusive übergeordneter `BoQCtgy`-Gruppen), Beschreibung, Einheit, Menge und ggf. Angebots-Einheitspreis.
2. `POST /api/v1/integrations/v1/gaeb/catalog-preview` mit `sourceBase64` (exakte XML-Bytes), `selections` (höchstens 100 eindeutig identifizierte Positionen), jeweils **explizit eingegebenen** Materialkosten, Lohnzeiten, Stundensatz, Zuschlag, Steuersatz und einer belegten `priceEvidence`. Die Antwort enthält die komplette überprüfte interne Zuordnung und einen `digest`.
3. `POST /api/v1/integrations/v1/gaeb/catalog-import` mit demselben JSON plus `previewDigest` und stabilem `Idempotency-Key` schreibt die ausgewählten `CatalogItem`-Einträge atomar. Das Backend liest die XML-Bytes unter dem mandantenbezogenen Transaktionslock erneut, berechnet den Digest, validiert jede Auswahl und erzeugt pro Katalogposition Auditeinträge. Ein erneut verwendeter Quellpositionsschlüssel wird durch den tenantbezogenen PostgreSQL-Unique-Key abgewiesen.

Importierte Katalogeinträge behalten einen unveränderlichen `GaebOrigin` mit Quell-SHA-256, Gliederungsschlüssel, ursprünglichem Positionsdigest, Austauschphase und der bei der Übernahme benannten Preisgrundlage. Reguläre Katalogänderungen dürfen diese Herkunft nicht neu schreiben. X83 ist üblicherweise unbepreist; selbst wenn X84 einen `UP` liefert, wird er **nicht** heimlich als eigener Material-/Lohnkostenwert oder Verkaufspreis übernommen.

Der v1-Katalogimport erzeugt **keine** bepreisten Angebote, verbindlichen Leistungsverzeichnisse, Bestellungen oder Aufträge. LV-Mengen sind in Katalogpositionen grundsätzlich keine Mengenzusagen; für die Angebotsbildung werden sie erst nach gesonderter Fachprüfung verwendet.

## Schutz- und Kompatibilitätsgrenzen von v1

- XML-DTDs/externe Entitäten gesperrt, maximal 1 MiB Originaldokument, 25.000 Elemente, XML-Tiefe 40, maximal 1.000 reguläre Vorschau-Items bzw. 100 ausgewählte Katalogpositionen pro Import.
- Unterstützt werden nur eindeutige `Item`-Positionen in `Itemlist` mit explizitem `RNoPart`, der offiziellen `DA83/3.3`- oder `DA84/3.3`-Namespacekennung und übereinstimmendem `DP` und `VersDate`. Fehlende/zu lange Beschreibungen oder Einheiten sperren die Übernahme; nicht bekannte Sonderpositionen werden nicht als garantiert importierbar dargestellt.
- **Keine vollständige XSD-Validierung im v1-Katalogweg**, keine Garantie über Verlustfreiheit von Langtext-Rich-Text, STLB, Anlagen, Mengenansätzen, Formeln, GAEB-Preisregeln oder Angebotsabgabe-Roundtrip. Katalogherkunft ist eine technische, nicht zertifizierte Ursprungskontrolle.
- Das Verfahren ist intern eindeutig und prüfbar, stellt aber keinen allgemeinen GAEB-Konverter dar.

## Ergänzender v2-Angebotsweg

Die [erweiterte Austauschdokumentation](advanced-exchange-and-physics.md) beschreibt `integrations/v2/gaeb/validate`, `bid-preview`, `bid-export` und die unveränderliche Exportakte. Original und X84-Ausgabe werden dort gegen die vorgesehenen Original-XSD geprüft. Die Ausgabe ist auf reguläre Positionen beschränkt; phasenbedingt ausgelassene Inhalte werden offengelegt und das Original bleibt separat erhalten.

Die [Preisarbeitsliste](gaeb-price-worksheet.md) exportiert bis zu 1000 Positionen aus diesem Arbeitsplatz und liest explizite Preisentscheidungen nach unveränderter Quell-/Positionsprüfung zurück. Ein lokaler CSV-Import ersetzt nicht die serverseitige Prüfung und erzeugt keine automatische X84, Dokumentablage oder Übermittlung. Beide v1- und v2-Abläufe bleiben in Berechtigung, Zweck und Validierungsumfang unterscheidbar.

## Nachweise und Quellen

Core v1: `src/FirstBlick.Core/Integrations/GaebXml.cs`, `tests/FirstBlick.Core.Tests/Integrations/GaebXmlTests.cs`. HTTP-/PG v1: `src/FirstBlick.Api/GaebCatalogEndpoints.cs`, `tests/FirstBlick.Api.Tests/GaebCatalogApiTests.cs`.

Core/API v2: `src/FirstBlick.Core/Integrations/GaebBidWriter.cs`, `src/FirstBlick.Api/OfficialGaebExchangeEndpoints.cs` und `tests/FirstBlick.Api.Tests/OfficialGaebEndpointTests.cs`. UI-Arbeitsliste: `web/tests/gaeb-price-worksheet.test.ts` und `web/tests/browser/gaeb-price-worksheet.spec.ts`.

Offizielle GAEB-Seite mit Version und Schemata: https://www.gaeb.de/de/service/downloads/gaeb-datenaustausch/ ; Austauschphasen X83/X84: https://www.gaeb.de/de/produkte/gaeb-datenaustausch/versionen/gaeb-da-xml-version-3-3-stand-2023-01/ .

Für den vollständigen Umfang von **DP-12-02** fehlen weiterhin repräsentative externe Austauschfälle, weitergehende Preis-/Positionsformen, vollständig geregelte Text-/Anlagenübernahme und lizenzierte Katalog-/STLB-Inhalte. Original-XSD-Prüfung und begrenzter X84-Rückexport sind im v2-Weg hingegen bereits implementiert und dürfen nicht mehr pauschal als fehlend bezeichnet werden. Die Katalog- und Angebotsabläufe sind überprüfbare Teilumfänge, keine allgemeine GAEB-Kompatibilitätszertifizierung.
