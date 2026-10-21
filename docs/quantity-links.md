# Aufmaß, Angebotspositionen und Materialbedarf

Vertrag `1.0.0`. Die Übernahme verwendet **eingefrorene geometrische Mengen**. Faktoren, Zuschläge und ihre Quelle werden ausdrücklich eingegeben; das Verfahren behauptet keine VOB-Abzugsregel, Materialeignung oder normative Berechnung.

## Mengenbeleg

`QuantityEvidence` enthält Aufmaß-ID, dessen Digest, Positions-ID, Ausgangsmenge/-einheit, Zieleinheit, Umrechnungsfaktor, Zuschlag in Prozent, Regelquelle, Verfahrensversion, Zielmenge und eigenen Digest. Der Server berechnet:

`Zielmenge = round(Ausgangsmenge × Faktor × (1 + Zuschlag / 100), 6, AwayFromZero)`.

Die Ausgangsmenge ist bereits die im Aufmaß dokumentierte Nettomenge einschließlich ihrer einzelnen begründeten Abzüge. Der Zuschlag liegt zwischen 0 und 100 Prozent. Einheiten werden nicht aus ähnlich geschriebenen Bezeichnungen abgeleitet. Für Stück, Packung oder Rollen muss der Anwender eine fachlich passende ausdrückliche Umrechnung angeben; das Verfahren rundet nicht automatisch auf ganze Verkaufspackungen auf.

Alle Dezimalwerte werden im API-Vertrag als invariante Dezimalstrings übertragen. Geschäftskennungen und technische Digests bleiben unverändert. Der Server prüft die vollständige Quelle erneut; ein selbst neu berechneter Digest über erfundene Ausgangswerte reicht nicht aus.

## Angebot: Vorschau und Anwendung

`POST /api/v1/quotes/{id}/measurement-lines/preview` und anschließend `/apply` verwenden denselben fachlichen Körper:

```json
{
  "measurementId": "UUID des eingefrorenen Aufmaßes",
  "measurementDigest": "SHA-256 des vollständigen Aufmaßbelegs",
  "lines": [{
    "measurementLineId": "UUID der Aufmaßposition",
    "catalogItemId": "UUID der Leistungsposition",
    "conversionFactor": "1",
    "wastePercent": "5",
    "ruleSource": "Explizit geprüfter Zuschnittzuschlag für diese Position"
  }],
  "previewDigest": null
}
```

Beide Aufrufe benötigen `If-Match` mit der Angebotsrevision. Zur Anwendung wird `previewDigest` durch den Digest der geprüften Vorschau ersetzt; dafür ist außerdem ein stabiler `Idempotency-Key` erforderlich. Vorschauen enthalten die hinzugefügten Positionen, ihre Katalogrevisionen und `beforeNet`, `addedNet`, `afterNet`. Eine Änderung an Katalog, Auswahl, Quelle oder Angebotsrevision verlangt eine neue Vorschau. Der Server ersetzt keine vorhandenen Positionen und übernimmt jedes Aufmaß-/Katalogpaar höchstens einmal in denselben Entwurf.

Nur Entwürfe können Mengen übernehmen. Herausgegebene und angenommene Angebote behalten ihre Menge, Preise und vollständige Parteidarstellung. Neue Katalogpreise wirken sich nicht auf alte Verkaufsbelege aus. Eine bewusste freie Bearbeitung eines Angebotsentwurfs darf einen Mengenlink entfernen; sie darf keine geänderte Menge weiterhin als unveränderten Aufmaßbeleg ausgeben. Alte Kalkulationsdigests bleiben kompatibel, da das optionale Feld bei nicht verknüpften historischen Positionen nicht serialisiert wird.

## Teilrechnung, Schlussrechnung und Korrektur

Ein Rechnungsentwurf übernimmt die vollständige `QuantityEvidence` des angenommenen Auftrags. Seine abzurechnende `quantity` darf eine positive Teilmenge dieser belegten Zielmenge sein; Aufmaßbeleg, Einheit, Herleitung und ursprüngliche Zielmenge bleiben unverändert. Diese Ausnahme gilt ausschließlich für Rechnungen und Rechnungskorrekturen. Angebots-, Auftrags- und Nachtragskalkulationen behalten die vollständige abgeleitete Menge.

Vor der Ausgabe vergleicht der Server jede Rechnungsposition mit dem angenommenen Auftrag und angenommenen Nachträgen. Die Positionsidentität enthält neben Preis- und Steuerinputs den vollständigen Mengenbeleg. Ein entfernter oder selbst neu berechneter Ersatzbeleg stimmt nicht mit der angenommenen Position überein. Sämtliche bisherigen Rechnungen und referenzierten Korrekturen fließen in die kumulative Mengen- und Betragsprüfung ein. Die Schlussrechnung muss die verbleibenden Vertragsmengen und den verbleibenden Betrag genau ausgleichen. Teilkorrekturen dürfen die Mengen und den Betrag ihrer Originalrechnung nicht überschreiten.

Der synthetische Nachweis verwendet 13,5 m² zu 10 EUR netto: 5 m² Teilrechnung, 8,5 m² Schlussrechnung, danach eine referenzierte Korrektur von 1 m². Alle Belege bewahren denselben ursprünglichen 13,5-m²-Nachweis. Das ist eine technische Rechen- und Unveränderbarkeitsprüfung; steuerliche Dokumentprofile und fachliche Rechnungsabnahme bleiben eigenständige Anforderungen.

## Materialbedarf

`POST /api/v1/material-demands/preview` und `/from-measurement` verwenden `measurementId`, `measurementDigest`, `measurementLineId`, `materialId`, `title`, optional `requiredOn`, `conversionFactor`, `wastePercent`, `ruleSource` und zur Anwendung `previewDigest`. Die Vorschau bindet auch die Materialrevision. Das Projekt wird aus dem Aufmaß übernommen.

Ein neuer Bedarf hat Status `Pending`. Ein identisches aktives Aufmaß-/Positions-/Materialpaar ist durch einen PostgreSQL-Eindeutigkeitsschlüssel geschützt. `POST /material-demands/{id}/cancel` erfordert die aktuelle Revision und einen ausdrücklichen Grund. Der Beleg bleibt erhalten. Ein bereits in einen Einkaufsentwurf umgewandelter Bedarf kann nicht entfernt oder zurückgesetzt werden.

Materialstammdaten behalten ihre Einheit, sobald Bestellungen, Bewegungen, Reservierungen oder Bedarfe darauf verweisen. Für eine andere Einheit ist ein separater Materialartikel anzulegen.

## Geprüfter Einkaufsentwurf

`POST /api/v1/material-demands/purchase-preview` und `/purchase-draft` verwenden:

```json
{
  "supplierId": "UUID des Lieferanten",
  "title": "Geprüfter Materialeinkauf",
  "expectedOn": "2026-11-01",
  "lines": [{ "demandId": "UUID", "revision": 1, "unitNet": "8.25", "vatPercent": "19" }],
  "previewDigest": null
}
```

Die Vorschau bindet Lieferantenrevision, sämtliche Bedarfsrevisionen, Mengen, Preise und Steuerangaben. Alle gewählten Bedarfe müssen zu genau einem Projekt gehören. Anwendung mit dem passenden Vorschau-Digest erzeugt **einen Einkaufsentwurf** und markiert die ausgewählten Bedarfe atomar als `Converted`, einschließlich Bestellung und Positions-ID. Parallele Wiederholungen desselben Idempotenzschlüssels erzeugen keine weiteren Bestellungen.

Der Vorgang bucht keine Ware und gibt die Bestellung nicht heraus. Preise und ergänzende freie Positionen können im Entwurf weiter geprüft werden; verknüpfte Mengen, Materialien und ihre Belege bleiben erhalten. Die separate Bestellausgabe verwendet die bestehende Freigabepolitik. Bei zwei aktiven Geschäftsführern verlangt die konfigurierte Vieraugenregel eine unabhängige, an Revision und Inhalt gebundene Freigabe. Ein Einzelgeschäftsführer verwendet den dokumentierten Einzelbetriebspfad. Wareneingang und Bestandsbewegungen folgen erst als eigene Vorgänge.

## Grenzen und Nachweise

Bis zu 200 ausgewählte Positionen je Übernahme bzw. Einkaufsentwurf; insgesamt höchstens 1.000 Angebots-/Bestellpositionen und die gemeinsamen JSON-/Speicherbudgets. Eine kommerzielle Zielmenge muss positiv sein und höchstens 1.000.000 betragen. Kleine berechnete Mengen, die nach Rundung null wären, werden abgewiesen.

Core-Prüfungen: `tests/FirstBlick.Core.Tests/Business/QuantityLinkTests.cs`. Echte HTTP-/PostgreSQL-Prüfungen: `tests/FirstBlick.Api.Tests/QuantityApiTests.cs`. Sie prüfen Katalogänderung zwischen Vorschau und Anwendung, parallele Wiederholung, fremde Betriebe/Projekte, gefälschte Belege, unveränderte ausgegebene Angebote, Materialeinheiten, Bestellentwurf mit Freigabegrenze, Löschversuche und entzogene Rechte.
