# Berechnungs- und XML-Arbeitsplätze

Stand: 20.10.2026. Bedienung und Verträge des in [Checkpoint 13](checkpoint13.md) beschriebenen Entwicklungsstands. Die bestehenden Rechenkerne und XML-Adapter erhalten bedienbare Angular-Abläufe; ihre fachlichen Grenzen werden dadurch nicht erweitert.

## 1. Dach & Berechnung

Die Route `/technical` bietet **Dachgeometrie**, **Schichtaufbau & Wärme**, **Feuchte – Einzelklima**, **Feuchte – 12 Monate** und **Gefälledämmung**. Sie benötigt ein aktives Betriebskonto mit `technical-results.write`. Die serverseitige Autorisierung bleibt maßgeblich; die Navigation ersetzt sie nicht.

Materialkennwerte, Maße, Quellen, Oberflächenwiderstände und Klimawerte sind bewusst **nicht vorbelegt**. Fehlende Werte sind nicht gleich null. Tatsächlich erlaubte Nullwerte, etwa ein eingegebenes Nullgefälle, bleiben dagegen erhalten. Schichten werden von innen nach außen eingegeben. Für Feuchteverfahren ist je Schicht zusätzlich die Diffusionswiderstandszahl erforderlich; der reine Wärmevertrag enthält dieses Feld ausdrücklich nicht. Der Monatsmodus verlangt genau zwölf unterschiedliche Kalendermonate mit allen 48 Temperatur-/Feuchteangaben.

Die Antwort wird erst angezeigt, wenn sie zur aktuellen Eingabe, bekannten Verfahrenskennung und Version passt. Eine Antwort mit fehlenden Grenzen, falschem Eingabebezug, unvollständigen Monatswerten oder `isNormative=true` wird verworfen. Der Client prüft diese Struktur und den Eingabebezug; er behauptet **keine unabhängige numerische Verifikation** des Servers. Die eigentliche Mathematik wird durch die getrennten C#-Referenztests geprüft.

### Ergebnis und Projektablage

Die Oberfläche zeigt Größen mit Einheiten, Grenzflächen, Feuchtehinweise oder vier Eckdicken des linearen Gefälles. Monatsergebnisse sind zwölf unabhängige Profile: keine Jahrestauwasserbilanz, keine Wasserakkumulation und keine Austrocknungsreserve. Auch ein nicht markierter Aufbau ist kein Unbedenklichkeitsnachweis.

Die Ablage erfolgt bewusst: Projekt serverseitig suchen, aus der aktuellen Trefferliste auswählen, Bericht benennen und **Berechnung im Projekt speichern** betätigen. Projektsuche ist seitenweise statt eines still abgeschnittenen Gesamtabzugs. Der Server berechnet erneut und bindet Eingabe und Ergebnis an Betrieb, Projekt, handelnde Person, Zeitpunkt und Digest. Es wird keine ungewählte Dachrevision unterstellt (`roofRevisionId=null`). Der vorhandene Berechnungsverlauf bleibt über `/technical-results` erreichbar.

Ein gespeicherter Bericht kann über den bestehenden Dokumentendienst als PDF erzeugt werden. Der Browser prüft zuerst dessen Projekt-, Quellen- und Digestbindung sowie den Bereitstellungszustand. Beim Abruf über `documents/{id}/download` werden Größe, PDF-Kennung und die SHA-256 der empfangenen Bytes gegen die Servermetadaten geprüft. Eine beschädigte Datei wird nicht zum Download angeboten. JSON enthält entweder die aktuelle Rechenantwort oder den vollständigen Speicherbeleg.

Jede fachliche Eingabeänderung entfernt das vorherige Ergebnis und seine Speicher-/Downloadaktionen. Betriebs-, Rollen- oder Sitzungswechsel entwerten geladene Daten und noch laufende Antworten. Für bekannte Ablehnungen 401/403/423 werden zugriffsabhängige Ergebnisse entfernt. Die fünf Reiter sind zusätzlich mit Pfeiltasten, Pos1 und Ende bedienbar.

### Methodenumfang

| Auswahl | Vertrag | Grenze |
|---|---|---|
| Dachgeometrie | `roof.rectangle` | Eine rechteckige Fläche, keine automatisch angenommene zweite Dachseite |
| Wärme | `thermal` | Stationäre homogene feste Schichten, keine vollständige Normbewertung |
| Feuchte, Einzelklima | `moisture.screening` | Ein stationärer Dampfdruck-/Temperaturvergleich, keine Kondensatmenge |
| Feuchte, Monate | `moisture.monthly-screening` | Zwölf voneinander unabhängige Klimaprüfungen, keine Periodenbilanz |
| Gefälledämmung | `roof.tapered-insulation` | Kontinuierliches lineares Volumen über rechteckigem horizontalem Grundriss; keine Herstellerplatten oder Verlegeoptimierung |

Für Details gelten [Feuchtescreening](moisture-screening.md), [Gefälledämmung](tapered-insulation.md) und [technische Verfahren](technical-methods.md).

## 2. GAEB & E-Rechnungen

Die Route `/trade-imports` ist aus **Import & Export → GAEB & E-Rechnungen** erreichbar. Katalogrechte erlauben den GAEB-Ablauf. Die E-Rechnungssichtung verlangt die passende Finanzrolle und Leseberechtigung für Eingangsbelege. Die bestehenden Serverprüfungen gelten unabhängig davon.

### Originaldatei

Eine Datei muss 1 Byte bis 1 MiB groß und gültiges UTF-8 sein. Die Oberfläche verändert weder BOM noch Zeilenumbrüche oder XML-Serialisierung. Sie berechnet SHA-256 der Originalbytes und verwirft eine Servervorschau, deren Quellenhash nicht übereinstimmt. Base64 für den späteren Katalogtransfer wird aus denselben Originalbytes gebildet. Die Dateiendung ist keine Konformitätsprüfung.

Betriebs- oder Berechtigungswechsel entfernen Datei, Vorschau, Auswahl und Bestätigung aus dem Seitenzustand. Verspätete Antworten können diesen alten Zustand nicht wiederherstellen. Es wird keine Offline-Warteschlange für XML-Importe eingerichtet.

### GAEB-Leistungskatalog

Der begrenzte bestehende Adapter erkennt das dokumentierte X83-/X84-Fenster. Die Vorschau zeigt Gliederungsschlüssel, Beschreibung, Einheit, LV-Menge und gegebenenfalls fremden Angebots-Einheitspreis. Positionen werden seitenweise angezeigt; höchstens 100 vollständige Positionen können bewusst ausgewählt werden. Eine fehlende explizite EUR-Währung sperrt die kaufmännische Übernahme.

Für jede Auswahl sind **eigene Materialkosten, Arbeitsstunden, Stundensatz, Zuschlag, Umsatzsteuer und eine Preisgrundlage** einzugeben. Die Oberfläche übernimmt niemals einen fremden Angebotspreis als eigenen Material- oder Verkaufspreis. Dezimalwerte bleiben Texte statt JavaScript-Gleitkommazahlen. Explizite Nullen sind erlaubt; fehlende Felder werden auch am Server nicht still als Nullkosten akzeptiert.

**Katalogzuordnung prüfen** erzeugt eine neue Vorschau des vollständigen Mappings. Erst danach werden Kosten, Herkunft und Auswahl zusammen angezeigt und die ausdrückliche Bestätigung für den aktuell gewählten Betrieb angeboten. Eine Änderung an den Eingaben entfernt diesen Prüfstand. Der Server bindet den Vorschaudigest zusätzlich an den Mandanten und prüft unter der vorhandenen Transaktionssperre erneut: Eine identische Datei mit identischen Werten benötigt in einem anderen Betrieb einen eigenen Prüfstand.

**Geprüfte Leistungen übernehmen** legt den gesamten ausgewählten Satz atomar und auditiert im internen Katalog an. Ein bereits vorhandener Quellpositionsschlüssel lässt die gesamte neue Übernahme scheitern; ein früher bearbeitetes Element derselben Transaktion bleibt nicht teilweise stehen. Der Importbeleg wird nur als vollständig angezeigt, wenn Quellenhash, Prüfdigest, Positionsliste und Katalogreferenzen zur bestätigten Vorschau passen.

Bei unklarer Transportantwort verwendet eine Wiederholung der unveränderten Anfrage dieselbe Idempotenzkennung. Ein formal erfolgreicher, aber unvollständiger Importbeleg wird nicht als Erfolg ausgegeben; eine weitere Übernahme wird auf dieser Seite gesperrt und die Prüfung des Katalogs verlangt. Der Benutzer darf nicht aus einer unklaren Antwort auf eine sicher fehlgeschlagene Buchung schließen.

Die Übernahme erzeugt **keine** Angebote, Aufträge, Materialreservierungen oder Bestellungen. Die Originaldatei wird hierfür erneut übertragen, aber **nicht zusätzlich als dauerhaftes Dokument archiviert**. Im Katalog bleibt die bereits vorhandene Herkunft mit Quellen-SHA und Positionsbezug erhalten. Vollständige XSD-Prüfung, Anlagen-/Langtextverlustfreiheit, Rückexport und externe Kataloglizenzen bleiben eigenständige offene Arbeiten; siehe [Adaptervertrag](gaeb-catalog.md).

### E-Rechnungssichtung

UBL-/CII-XML zeigt Kopf- und Partnerangaben, Profilkennung, Datum, Positionen, deklarierte Netto-/Brutto-/Zahlbeträge sowie Prüfhinweise. Große Dezimalbeträge werden ohne Umwandlung in binäre Gleitkommazahlen dargestellt. XML-Text wird als Text ausgegeben, nicht als HTML interpretiert.

Die dargestellte Steuerdifferenz ist **Brutto minus Netto**, nicht das Ergebnis einer geprüften Umsatzsteuerkategorienrechnung. `externalValidationPerformed=false` und `mayPostAutomatically=false` müssen unverändert vorliegen. Es gibt hier keinen Buchungs- oder Versandknopf. PDF/A-3-Anhangextraktion, vollständige XRechnung-/ZUGFeRD-Profilvalidierung, Lieferantenmatching, Dublettenerkennung und strukturierte Ausgabe sind nicht implementiert; siehe [E-Rechnungsvertrag](e-invoice.md).

## 3. Prüfumfang und verbleibende Grenzen

Unit-Tests prüfen Eingabebudgets, Byte-/Dezimalbehandlung, vollständige Quellenbindung und Ablehnungsfälle. Browser-Fixtures prüfen die Bedienung auf Desktop, Tablet und Mobil, einschließlich Betriebswechsel während laufender Anfragen, Rechteentzug, falscher Antworten, korruptem PDF, manueller Bestätigung und wiederholter Übernahme. Diese Fixtures simulieren HTTP-Antworten; sie sind **keine** zusätzlichen Live-Browserläufe gegen PostgreSQL.

Die neuen C#-HTTP-/PostgreSQL-Tests prüfen reale Mandantenbindung des GAEB-Digests, JSON-Pflichtfelder, atomaren Batch-Rollback und normalisierte, idempotente Monatsberichtablage. Die vorhandenen Parser-, Rechenkern- und Operations-Suites werden ebenfalls ausgeführt. Genaue Commit- und Laufbelege stehen in [Checkpoint 13](checkpoint13.md). Fachliche Normkonformität, echte Material-/Betriebsdaten und eine unabhängige Gesamtsicherheitsprüfung werden durch diese Tests nicht behauptet.
