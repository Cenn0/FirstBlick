# Verifizierbare Austausch- und Bauphysik-Abläufe (2026-10-21)

Diese Beschreibung erläutert den implementierten Quellstand. Ihre Aussagen gelten unabhängig vom Branch nur für Commits mit abgeschlossenen, erfolgreichen Qualitätsprüfungen; sie ist keine Freigabe eines konkreten Bauwerks. Sie trennt Implementierung, XSD-/Normverfahren und tatsächliche Projekt-/Ausführungsfreigabe. Reale Geschäfts-, Klima- und Herstellerdaten werden nicht angenommen oder ergänzt.

## E-Rechnungen und Buchungs-/Ausgabeketten (bereits im Basisbranch)

* `POST /api/v1/integrations/v2/e-invoice/validate`: originale, lokal fixierte KoSIT-Validator-/XRechnung-Konfiguration 3.0.2, insbesondere UBL 2.1, mit archivierungsfähigem XML-Prüfreport und Quellprüfsumme.
* `.../e-invoice/import-preview` und `.../e-invoice/import`: bindet originale Bytes, Verkäufer-/Empfängerangaben, ausgewählten Lieferanten und optionalen Bestellbezug an einen explizit bestätigten Freigabedigest. Erst eine akzeptierte und aktuell validierte Rechnung wird als **Eingangsrechnungsentwurf** mit versionierten Original-/Prüfbericht-/Prüfbelegobjekten übernommen. **Keine** automatische Zahlungsfreigabe.
* Lieferantenrechnungen werden erst im eigenen Freigabeablauf gebucht. Zahlungseingänge/-ausgänge aus dem Bankimport lassen sich geprüft zuordnen und reversieren; negative Buchungen/Skonto müssen als eigene Zahlungsrichtung und Ereignisse erhalten bleiben. Die API startet keine Banküberweisung.
* `POST /api/v1/invoices/{id}/e-invoice/preview` sowie `.../export`: XRechnung-UBL aus tatsächlich ausgestelltem, eingefrorenem Rechnungsstand; ausdrücklich geprüfte Umsatzsteuerkategorien und Kontodaten, externe Validierung und versionierte Ausgabe-/Prüfreport-/Prüfbelegobjekte.

Grenzen: Es handelt sich um eine implementierte technische Prozesskette, nicht um eine Steuerberaterfreigabe. Die vorhandene Ausgabe ersetzt keine Peppol-Zustellung, kein ZUGFeRD-PDF/A-3 und keine DATEV-Verfahrensabnahme. KoSIT-Upgrades benötigen ein erneutes Testset, nicht nur einen Versionsstring.

## GAEB DA XML 3.3 / Paket Leistungsverzeichnis 2021-05

Die GAEB-Webanwendung unterscheidet **v1 strukturelle Katalogübernahme (ohne XSD)** von **v2 offizieller XSD-/X84-Exportkette**:

1. `POST /api/v1/integrations/v2/gaeb/validate` mit originaler UTF-8-Datei `application/xml` prüft die unveränderten offiziellen X83- oder X84-XSD; Prüffehler werden ohne automatische Katalogänderung zurückgegeben.
2. `POST /api/v1/integrations/v2/gaeb/bid-preview` mit `{ projectId, expectedProjectRevision, sourceBase64, mapping }` prüft zuerst das Original gegen XSD, dann jede Angebotsposition auf Quelle-/Positionsdigest, Menge, Währung, Einzelpreis oder `notOffered`. Der X83→X84-Writer erhält Positionsidentifikatoren, Hierarchie, Preise und nachgerechnete Summen. Das originale X84-Schema gestattet bei diesen Beschreibungselementen nur Bieter-Textergänzungen, nicht die freien X83-Absätze. Deshalb wird X83-Freitext **nicht in erfundene X84-Absätze umgeschrieben**, sondern pro Position als ausgeschlossene Phaseninformation protokolliert und über das **unverändert archivierte X83-Original** erhalten. Vorhandene Bieter-Textergänzungen oder Anlagen werden ohne einen gesonderten Eingabe-/Abgleichvertrag **abgewiesen**. Die Ausgabe ist keine verlustfreie Einzeldatei-Kopie des X83-LVs. Besondere GAEB-Preis- und Positionsformen werden ausdrücklich abgelehnt. Auch die resultierende X84 muss die passende Original-X84-XSD bestehen.
3. `POST /api/v1/integrations/v2/gaeb/bid-export` verlangt **identische** Preis-/Projekt-/Original-/Validierungsdaten und den durch den Bearbeiter bestätigten `previewDigest`. Der idempotente Auftrag speichert drei getrennte, read-back-verifizierte, **versionierte** Artefakte: Original-XML, X84-XML und JSON-Evidenz. Die Buchung ist auf Projekt, Mandant und Prüfbelege festgelegt. Eine gesonderte, unveränderliche, mandantengebundene GAEB-Exchangereferenz wird vor den drei Dokumenten als transaktionaler Fremdschlüssel-Datensatz gespeichert. Allgemeine CRUD-/Dokumentenrouten gewähren dafür keinen Zugriff.
4. `GET /api/v1/integrations/v2/gaeb/exports/{exchangeId}` und `.../documents/{documentId}/download` erfordern **erneut** aktuelle Katalog-Schreibberechtigung sowie Projektzugriff. Standard-Dokumentenendpunkte verweigern diese spezialisierte, preisvertrauliche `gaeb-exchanges`-Quelle. HTTP-Antwort und gespeicherte Objekte tragen SHA-256-Prüfsummen.

Die X84-Datei ist eine **Angebotsausgabe**, keine automatische Submission an die Vergabestelle und keine erfolgreiche Angebotsannahme. `GAEBInfo/ProgSystem` kennzeichnet stets das tatsächlich exportierende System; bei X83→X84 werden fehlende phasenfremde Felder dokumentiert. Kein Anspruch auf vollständige Unterstützung der GAEB-Phasen X80–X97 oder GAEB-Kompatibilitätszertifizierung.

Primärquelle: GAEB, aktuelle Datenpakete: https://www.gaeb.de/de/produkte/gaeb-datenaustausch/versionen/gaeb-da-xml-version-3-3-stand-2023-01/ ; X83 und X84 verbleiben 2021-05. GAEB 3.4/2026-03 ist als **Beta** eingestuft und wird nicht als gültiger Standard ausgegeben.

## ISO-13788-Tauwasser und Austrocknung

Neue Endpunkte:
* `POST /api/v1/technical/moisture/iso13788/balance`
* `POST /api/v1/technical/moisture/iso13788/drying`

`technical/runs` speichert Rechenläufe zusätzlich mit `method=moisture.iso13788.balance` bzw. `method=moisture.iso13788.drying` mandanten-/projektgebunden, vollständig reproduzierbar anhand normalisierter Eingaben und Digest.

Die ISO-13788:2012-Implementierung verfügt bereits über monatliche Grenzflächendiffusion, Kondensation, Verdunstung, Wasser-Monatsüberträge, Jahres-Restmenge, Feuchteumlagerung, 1-kg/m²-Abschnitt-7-Austrocknungsvergleich und mehrjähriges Verhalten (maximal 120 Monate). Eine fachliche `Applicability`-Bestätigung und zwölf explizite Klimadatenpunkte sind zwingend. Im UI erscheinen Werte ohne automatische Klimakopie oder erfundene Materialkennwerte.

**Normative Trennung**: Das implementierte **ISO-Verfahren** ist nicht gleichbedeutend mit einem **normativ abgenommenen Feuchteschutznachweis** nach DIN 4108-3:2024-03. National geforderte Bauartbedingungen, schadensrelevante Feuchtegrenzen und Verfahrensanwendungsbereiche sowie reale Schichten-/Klimawerte müssen gesondert geprüft werden. Der ISO-13788-Kern selbst nennt offen ein ungeklärtes Zahlenbeispiel aus dem informativen Anhang C.4/C.5; das darf nicht als bestandene Referenz behauptet werden. Keine Zertifizierungskennzeichnung.

Normbasis: https://www.iso.org/standard/51615.html und https://www.dinmedia.de/de/norm/din-4108-3/373925063 (offizielle Normmetadaten, nicht Verbreitung lizenzierter Normtexte).

## Rechteckige, einachsige herstellerbezogene Gefälleplattenplanung

`POST /api/v1/technical/roof/tapered-board-layout` und `technical/runs` mit `method=roof.tapered-board-layout` liefern Rasterkoordinaten, Orientierung, örtliche Dicken, Zuschnitte, Verschnittvolumina, Stück-/Pack-Bestellmengen und optional Kosten je SKU. Einbaurate und analytisch integriertes Keilvolumen müssen übereinstimmen. Der Rechenkern akzeptiert nur vom Anwender **explizit gelieferte** Hersteller-/Produkt-/Katalogrevisions-/SKU-Werte und lässt keine Substitution von fehlenden Dickenstufen zu. Fehlende Preise bleiben unbekannt.

**Weder herstellerzertifiziert noch vollständiger Verlegeplan.** Unterstützt ist ausschließlich ein rechteckiges Dachfeld, konstanter einachsiger Neigungsgradient und durchgehende Tiefkante; keine innenliegenden Abläufe, Sicken, Schnitt-Optimierung, Wiederverwendung von Verschnitt, wechselnde Rohdachdicken, statische Nachweise, Windsog/Brandverhalten, Befestigungsnachweise oder werkseitig geprüfte Kombinationen. Echte Herstellerkataloge inklusive gültiger Planungsvorschriften müssten rechtssicher eingebunden, revisionsgeprüft und per Referenzfall abgenommen werden, bevor verbindliche Bestellungen erzeugt werden dürfen.

## Qualitätsgates und Betrieb

In GitHub: `dotnet test FirstBlick.slnx -c Release`, `npm run build`, Angular-/API-Tests, Original-XSD-/KoSIT-Validierung, PostgreSQL-/S3-Integration und Operations-/Container-Stack-Tests. Neue Regressionen verwenden ausschließlich synthetische GAEB-/Hersteller-/Feuchtedaten; echte Herstellerfreigaben oder bauaufsichtliche Konformität lassen sich daraus nicht ableiten.

Die Implementierung ist erst dann technisch belastbar, wenn **CI, Operations und Original-Standards-Validierung auf demselben endgültigen Commit erfolgreich abgeschlossen** sind. Ein grüner CI-Status ist dabei eine notwendige technische Prüfung, nicht die fachliche Freigabe durch Hersteller, Fachplaner oder Auftraggeber.

## Ergänzung: mehrfeldrige Gefälleplanung (21.10.2026)

Für rechteckige Dächer mit mehreren angrenzenden **einachsigen** Gefällefeldern steht außerdem
`POST /api/v1/technical/roof/tapered-assembly` bzw. das persistierbare Verfahren
`roof.tapered-assembly` bereit. Jedes Feld enthält den explizit erfassten Herstellerkatalog,
die nominalen SKU-Kanten und ein eigenes gerichtetes Gefälle. Gemeinsame Dachkoordinaten,
Planrevision und Quellenbelege sind Bestandteil des Eingabedigest. Überlappungen, Lücken,
Dachüberschreitungen und Unstetigkeiten der Dicke an gemeinsamen Feldgrenzen werden abgewiesen.
Der Server erzeugt je Feld eine überprüfbare Positionierung und eine **gemeinsame**
Bestellmengenrechnung, die identische Hersteller-, Produkt-, Katalogversions-, Quellen- und
SKU-Kombinationen vor der Packungsrundung zusammenführt. Unterschiedliche Quellen oder
Widersprüche bei Artikelmaßen, Preisen und Packungsgrößen werden nicht still vereinheitlicht;
fehlende Preise bleiben unbekannt. Die responsive Angular-Fachoberfläche erlaubt bis zu 32
Felder und eine Projektablage mit serverseitiger Neuberechnung, Audit und PDF-Bericht.

**Keine zertifizierte Verlegeplanung:** Das Modell deckt nur lückenlose rechteckige
Gesamtgrundrisse mit aneinanderliegenden linearen Gefällefeldern ab, ohne innenliegende
Abläufe, Kehlen, Durchdringungen, Oberflächenstrukturen oder Verschnittoptimierung.
Auch die theoretisch stetige Fugendicke garantiert keine geeignete Dachentwässerung.
Eingetragene Hersteller-/SKU-Kennwerte sind ohne unabhängig verifizierte Katalogquellen
und Herstellerfreigabe keine ausführungs- oder bestellverbindliche Vorgabe.
