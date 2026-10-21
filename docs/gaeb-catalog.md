# GAEB DA XML 3.3 – strukturelle LV-Sichtung und überprüfter Leistungskatalogimport

Dieses Modul verwendet ein ausdrücklich eingegrenztes Formatfenster: **GAEB DA XML 3.3 / 2021-05, X83 (Angebotsaufforderung) und X84 (Angebotsabgabe)**. Die Ausgabe 2023-01 betrifft bei 3.3 das Mengenermittlungspaket X31; GAEB 3.4 aus 2026-03 ist derzeit eine **Beta-Fassung** und wird nicht stillschweigend importiert.

## Datenflüsse

1. `POST /api/v1/integrations/v1/gaeb/preview` mit Original-XML (`application/xml`, UTF-8, maximal 1 MiB) zeigt SHA-256, Phase, Namespace, Version, LV-Titel, Positionen und ihre Gliederungskennungen (`RNoPart` inklusive übergeordneter `BoQCtgy`-Gruppen), Beschreibung, Einheit, Menge und ggf. Angebots-Einheitspreis.
2. `POST /api/v1/integrations/v1/gaeb/catalog-preview` mit `sourceBase64` (exakte XML-Bytes), `selections` (höchstens 100 eindeutig identifizierte Positionen), jeweils **explizit eingegebenen** Materialkosten, Lohnzeiten, Stundensatz, Zuschlag, Steuersatz und einer belegten `priceEvidence`. Die Antwort enthält die komplette überprüfte interne Zuordnung und einen `digest`.
3. `POST /api/v1/integrations/v1/gaeb/catalog-import` mit demselben JSON plus `previewDigest` und stabilem `Idempotency-Key` schreibt die ausgewählten `CatalogItem`-Einträge atomar. Das Backend liest die XML-Bytes unter dem mandantenbezogenen Transaktionslock erneut, berechnet den Digest, validiert jede Auswahl und erzeugt pro Katalogposition Auditeinträge. Ein erneut verwendeter Quellpositionsschlüssel wird durch den tenantbezogenen PostgreSQL-Unique-Key abgewiesen.

Importierte Katalogeinträge behalten einen unveränderlichen `GaebOrigin` mit Quell-SHA-256, Gliederungsschlüssel, ursprünglichem Positionsdigest, Austauschphase und der bei der Übernahme benannten Preisgrundlage. Reguläre Katalogänderungen dürfen diese Herkunft nicht neu schreiben. X83 ist üblicherweise unbepreist; selbst wenn X84 einen `UP` liefert, wird er **nicht** heimlich als eigener Material-/Lohnkostenwert oder Verkaufspreis übernommen.

Der Import erzeugt **keine** bepreisten Angebote, verbindlichen Leistungsverzeichnisse, Bestellungen oder Aufträge. LV-Mengen sind in Katalogpositionen grundsätzlich keine Mengenzusagen; für die Angebotsbildung werden sie erst nach gesonderter Fachprüfung verwendet.

## Schutz- und Kompatibilitätsgrenzen

- XML-DTDs/externe Entitäten gesperrt, maximal 1 MiB Originaldokument, 25.000 Elemente, XML-Tiefe 40, maximal 1.000 reguläre Vorschau-Items bzw. 100 ausgewählte Katalogpositionen pro Import.
- Unterstützt werden nur eindeutige `Item`-Positionen in `Itemlist` mit explizitem `RNoPart`, der offiziellen `DA83/3.3`- oder `DA84/3.3`-Namespacekennung und übereinstimmendem `DP` und `VersDate`. Fehlende/zu lange Beschreibungen oder Einheiten sperren die Übernahme; nicht bekannte Sonderpositionen werden nicht als garantiert importierbar dargestellt.
- **Keine vollständige XSD-Validierung** mit offiziellem GAEB-Prüfer, keine Garantie über Verlustfreiheit von Langtext-Rich-Text, STLB, Anlagen, Mengenansätzen, Formeln, GAEB-Preisregeln oder Angebotsabgabe-Roundtrip. Katalogherkunft ist eine technische, nicht zertifizierte Ursprungskontrolle.
- Das Verfahren ist intern eindeutig und prüfbar, stellt aber keinen allgemeinen GAEB-Konverter dar.

## Nachweise und Quellen

Core: `src/FirstBlick.Core/Integrations/GaebXml.cs`, `tests/FirstBlick.Core.Tests/Integrations/GaebXmlTests.cs`. HTTP-/PG: `src/FirstBlick.Api/GaebCatalogEndpoints.cs`, `tests/FirstBlick.Api.Tests/GaebCatalogApiTests.cs`.

Offizielle GAEB-Seite mit Version und Schemata: https://www.gaeb.de/de/service/downloads/gaeb-datenaustausch/ ; Austauschphasen X83/X84: https://www.gaeb.de/de/produkte/gaeb-datenaustausch/versionen/gaeb-da-xml-version-3-3-stand-2023-01/ .

Für eine **vollständige** DP-12-02-Abnahme fehlen repräsentative Original-X83-/X84-Dateien, offizieller XSD-Abgleich, garantierte Text-/Anlagenübernahme und Rückexportprüfung sowie lizenzierte Katalog-/STLB-Inhalte. Der geprüfte interne Katalogimport ist implementiert, aber nur ein begrenzter Teil des Gesamtepics.
