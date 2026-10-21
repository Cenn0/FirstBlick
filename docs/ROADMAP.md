# FirstBlick – Rework-Roadmap

> Übernommen aus der Projektanalyse „DachPlaner Rework Roadmap“, 19.10.2026 (34 Seiten). Der Originalbericht umfasst 100 Aufgaben in 15 Epics. Diese Fassung erhält alle Aufgabenkennungen und ursprünglichen Abnahmekriterien. Ein implementierter Teilablauf bedeutet nicht automatisch, dass das gesamte Epic abgeschlossen ist.

## Umsetzung der geprüften Fachmodule (21.10.2026)

Die [erweiterte Fach- und Schnittstellendokumentation](advanced-exchange-and-physics.md) beschreibt die neuen, separat überprüfbaren Abläufe und Grenzen:

- **DP-08-03:** Geprüftes XRechnung-XML anhand der gepinnten KoSIT-Konfiguration, manuell bestätigte Lieferantenrechnung mit Original-/Prüfbericht-/Prüfbelegversionen, Freigabe-/Zahlungsabgleich sowie validierter UBL-Rechnungsausgabe aus dem eingefrorenen Ist-Rechnungsstand. Keine automatische Banküberweisung, Peppol-Zustellung oder generelle ZUGFeRD-/DATEV-Freigabe.
- **DP-12-02:** GAEB-X83/X84 anhand der originalen XSD 3.3/2021-05 prüfen; Angebote mit quell- und projektgebundener Prüffreigabe als X84 mit archiviertem X83-Original und eigener, gegen Datenbank-Fremdschlüssel referenzierter Exportakte ausgeben. Nur reguläre Positionen; phasenbedingt nicht darstellbare Freitexte sind ausdrücklich ausgewiesen.
- **DP-11-04:** Monatsbilanzen für Kondensation/Verdunstung und mehrjährige Austrocknungsvergleiche nach dem bezeichneten ISO-13788:2012-Verfahren, mit Anwendungsgrenzen, Klimanachweisen und prüfsummengebundenen Projektläufen. Kein umfassender Nachweis der DIN 4108-3 oder unabhängige Fachabnahme; der dokumentierte Referenzwiderspruch bleibt offen.
- **DP-11-05:** Rechteckiges, einachsiges Gefälleplattenraster mit expliziten Hersteller-/Katalogversions-/SKU-Eingaben, Stücklisten, Packmengen und Verschnitt. Kein bestätigter Herstellerkatalog, keine Innenentwässerung, keine normative Verlegefreigabe.

Diese Fähigkeiten sind **technisch implementierte Teilumfänge**, keine pauschale Erledigung sämtlicher ursprünglicher Aufgaben und keine Zusage umfassender Norm-/Herstellerkonformität. Maßgeblich sind die **CI-, Standards- und Operations-Prüfergebnisse desselben finalen GitHub-Commits**. Ältere Checkpoints behalten ihre historische Aussage zum jeweiligen Quellstand; nachfolgende Abschnitte dokumentieren den früheren Stand.

## Weiterführung – Originale XML-Akten

Die [Originalakten-Dokumentation](xml-originals.md) ergänzt **DP-08-03, DP-12-02 und DP-02-05** um einen zusammenhängenden Ablauf: strukturell geprüfte Originaldatei → bewusste Ablage → konkrete S3-Version und Eingangsbeleg → mandantengebundene Dublettenprüfung → wiederauffindbare Akte → erneut verifizierter Download oder Wiederaufnahme im Prüfbereich. GAEB-Akten zeigen die tatsächlich daraus übernommenen Katalogpositionen; ungespeicherte Kalkulationswerte werden bei der Wiederaufnahme nicht ohne Bestätigung verworfen.

Die Implementierung umfasst Backend, responsive Angular-Bedienung, Core-/HTTP-/PostgreSQL-Regressionen, Browser-Vertragstests und einen erweiterten tatsächlichen Containerablauf mit PostgreSQL/S3, Sicherung und Neustart. Die zugehörigen Ausführungsergebnisse werden für den konkret veröffentlichten Commit in [Folge-PR #2](https://github.com/Cenn0/FirstBlick/pull/2) nachgewiesen. Ein vorhandener Test oder Workflow ist für sich kein bestandener Lauf.

Die Dateiaufbewahrung **bucht keine Rechnung**, erzeugt ohne gesonderte bestätigte Katalogübernahme keine Leistung und behauptet weder vollständige Profil-/XSD-/Schematron-Konformität noch ein zertifiziertes Langzeitarchiv. Die weiter erforderliche vollständige E-Rechnungs- und GAEB-Verarbeitung bleibt offen. Die untenstehenden ursprünglichen Aufgaben werden deshalb nicht pauschal abgehakt; die historischen Gesamtzählungen werden durch diesen Teilumfang nicht in einen neuen Fertigstellungsprozentsatz umgedeutet.

## Vorheriger Arbeitsstand – Checkpoint 13

[Checkpoint 13](checkpoint13.md) beschreibt die neuen durchgängigen Bedienabläufe und ihre konkreten Testbelege. Die [Bedienungs- und Vertragsdokumentation](calculation-and-trade-workbenches.md) erklärt Eingaben, Speicherung, Importbestätigung und bewusst nicht unterstützte Fälle.

- **DP-11-02/03/04/05:** Fünf vorhandene Rechenverfahren sind über einen gemeinsamen Angular-Arbeitsplatz bedienbar: Dachgeometrie, Wärme, Einzelklima- und Zwölfmonats-Feuchtescreening sowie lineare Gefälledämmung. Ergebnisprüfung, ausdrückliche Projektablage, JSON-Export und quellen-/prüfsummengebundener PDF-Download bilden einen zusammenhängenden Ablauf.
- **DP-12-02:** GAEB-Datei → unveränderte Originalbytes → Positionsauswahl → ausdrücklich eingegebene eigene Kosten → geprüfte Zuordnung → bestätigte atomare Katalogübernahme. Der Prüfstand ist serverseitig an den Betrieb gebunden; fehlende Kostenfelder werden nicht als Nullwerte erfunden.
- **DP-08-03:** Eine lesende UBL-/CII-XML-Oberfläche zeigt Angaben, Positionen und Prüfhinweise. Eine vollständige Profilvalidierung, steuerliche Verarbeitung oder strukturierte Ausgabe ist weiterhin nicht vorhanden.
- **DP-03-06/07 und DP-06-08:** Zusätzliche Browserfälle decken Desktop, Tablet und Mobil, Betriebswechsel, verspätete Antworten, Rechteentzug, fehlerhafte Antworten und explizite Bestätigung ab. Die neuen HTTP-/PostgreSQL-Fälle prüfen reale serverseitige Mandanten-, Pflichtfeld- und Transaktionsgrenzen.

Die [ältere vollständige Nachweismatrix](roadmap-evidence.md) bleibt als historisches Inventar erhalten. **Für die hier berührten Aufgaben ergänzt und berichtigt die Tabelle in Checkpoint 13 den älteren Stand**, insbesondere die früher als vollständig offen beschriebenen XML-/Feuchte-Teilfunktionen. Die historischen Kategorien 49 K / 42 T / 9 O werden nicht als aktualisierte Gesamtzählung oder Fertigstellungsprozentsatz ausgegeben. Die gesamte 100-Aufgaben-Matrix wurde in diesem Checkpoint nicht neu abgenommen.

[Checkpoint 9](checkpoint9.md) dokumentiert den damaligen Gesamtstand; [Checkpoint 10](checkpoint10.md) ergänzt begrenzte Rechnungsquellen und versionierte Kostenprognosen; [Checkpoint 11](checkpoint11.md) ergänzt den Null-Euro-Mengenabschluss und abgesicherte Kostenquellen. Die [lineare Gefälledämmungs-Mengenhilfe](tapered-insulation.md) bleibt von einer herstellergebundenen Verlegeplanung getrennt. Jeder Testbeleg gilt ausschließlich für seinen angegebenen Quellstand.

Weiter offen bleiben vollständige E-Rechnungsprofile und Import-/Exportketten, normative Feuchteschutzverfahren, Hersteller-Verlegeplanung, weitere technische Fachmodule, automatisierter Providerbetrieb, repräsentative Last-/SLO-Nachweise und reale Migrationsquellen. Eine erfolgreiche Softwareprüfung ersetzt keine unabhängige Prüfung der Fachverfahren. Open-Source-Weiterentwicklung ist kein formaler Produktionsfreigabeprozess.

## Zielarchitektur

- **Frontend:** Angular 22, responsive PWA, selektive Offline-Daten in IndexedDB
- **Backend:** C# / ASP.NET Core 10, modularer Monolith mit versionierter OpenAPI
- **Daten:** PostgreSQL, EF Core, mandantenbezogene Schlüssel und Transaktionen
- **Dateien:** S3-kompatibler Objektspeicher mit Versionierung, Prüfsummen und Zugriffskontrolle
- **Hintergrundarbeit:** separater .NET Worker, transaktionale Outbox, Leasing und Klärungszustände
- **Sicherheit:** persönliche Konten, serverseitige Rollen/Fähigkeiten, HttpOnly-Cookies, CSRF, Audit und Vieraugenfreigabe
- **Betrieb:** reproduzierbare CI, Backups und Restore, Migration aus D1/R2, dokumentierter Rollout

## Meilensteine

| Stufe | Inhalt | Abnahme |
|---|---|---|
| M0 | Ausgangsbasis, Anforderungen, Fachverfahren | Quellstand, Dateninventar und Referenzfälle geklärt |
| M1 | Verlässlicher Kern, DP-01/02 und Identität | Builds, Regressionen, Berechtigungen, Audit und Jobs geprüft |
| M2 | Kunden/Objekte, Angebote, Aufträge und Aufmaß | Anfrage bis angenommenem Auftrag und Nachtrag |
| M3 | Disposition, Zeiten, Material und Offline | Reproduzierbarer Baustellenarbeitstag inklusive Neustart |
| M4 | Rechnungen, Zahlungen, Portal und Wartung | Abrechnung und lesender Zahlungsabgleich |
| M5 | Dachmodell und technische Fachmodule | Unabhängig geprüfte Flach-/Steildach-Referenzfälle |
| M6 | Migration, Restore und Gesamtabnahme | Daten/Dokumente übernommen, Endabläufe nachgewiesen |

## Vollständige Aufgabenstruktur

### DP-00 – Ausgangsbasis und Produktumfang (P0)
- [ ] DP-00-01 Aktuellen Quellstand zusammenführen
- [ ] DP-00-02 Daten und Dateien inventarisieren
- [ ] DP-00-03 Verbindliche Anforderungsmatrix erstellen
- [ ] DP-00-04 Drei reale Betriebsabläufe modellieren (Sanierung, Reparatur, Wartung)
- [ ] DP-00-05 Projektregeln und Entscheidungen dokumentieren

### DP-01 – Nachgewiesene Fehler und verlässliche Entwicklung (P0/P1)
- [ ] DP-01-01 Reproduzierbare Ausgangsbasis und neue CI herstellen
- [ ] DP-01-02 Aktivierung und Kontowiederherstellung trennen
- [ ] DP-01-03 Angebotsstatus beim Versand erhalten
- [ ] DP-01-04 Effektive Kalkulationswerte einfrieren
- [ ] DP-01-05 Eingabe- und Speicherbudgets abstimmen
- [ ] DP-01-06 Unklare Versandvorgänge auflösbar machen
- [ ] DP-01-07 Planungsinvarianten in beide Richtungen prüfen
- [ ] DP-01-08 Finale Sicherheitsheader prüfen

### DP-02 – Modularer Kern, Daten und Hintergrundarbeit (P1)
- [ ] DP-02-01 C#-/Angular-Serverbasis aufsetzen
- [ ] DP-02-02 C#-Fachmodule und OpenAPI-Vertrag einführen
- [ ] DP-02-03 PostgreSQL und EF-Core-Migrationen strukturieren
- [ ] DP-02-04 .NET-Worker, Outbox und atomaren Audit aufbauen
- [ ] DP-02-05 C#-Dokumentendienst und Dateispeicher entwickeln
- [ ] DP-02-06 API-Abfragen und Angular-Datenzugriff konsolidieren
- [ ] DP-02-07 Serverkonfiguration, Schlüssel und Beobachtbarkeit schaffen

### DP-03 – Identität, Freigaben, Module und Bediengrundlage (P1)
- [ ] DP-03-01 ASP.NET-Identität und Betriebsmitgliedschaften trennen
- [ ] DP-03-02 Funktionsrechte serverseitig ausdrücken
- [ ] DP-03-03 Mehrere Geschäftsführer und Delegation ermöglichen
- [ ] DP-03-04 Konfigurierbare Vieraugenfreigabe implementieren
- [ ] DP-03-05 Module pro Betrieb steuern
- [ ] DP-03-06 Angular-Oberfläche mit Routen und Designsystem aufbauen
- [ ] DP-03-07 Berechtigungsmatrix als Prüfsuite verankern
- [ ] DP-03-08 Externe Partner gezielt beteiligen

### DP-04 – Kunden, Gebäude, Dach und Projektakte (P1)
- [ ] DP-04-01 CRM und Ansprechpartner ausbauen
- [ ] DP-04-02 Gebäude und dauerhafte Dachakte einführen
- [ ] DP-04-03 Projekte und Arbeitsstruktur modellieren
- [ ] DP-04-04 Aufgaben, Fristen und Verlauf integrieren
- [ ] DP-04-05 Allgemeine Projektunterlagen bereitstellen
- [ ] DP-04-06 Übergreifende Suche und Archivierung ergänzen

### DP-05 – Kalkulation, Angebot, Auftrag und Nachtrag (P1)
- [ ] DP-05-01 Kalkulations- und Leistungskatalog konsolidieren
- [ ] DP-05-02 Angebots- und Vertragszustände trennen
- [ ] DP-05-03 Professionelle Angebotsbearbeitung ermöglichen
- [ ] DP-05-04 Auftrag aus angenommener Version erzeugen
- [ ] DP-05-05 Nachträge und Leistungsänderungen führen
- [ ] DP-05-06 Soll, Ist und Prognose vergleichen
- [ ] DP-05-07 Kaufmännischen Referenzablauf absichern

### DP-06 – Planung, Personal, Baustellenarbeit und Offline (P1)
- [ ] DP-06-01 Disposition mit Zeitfenstern und Ressourcen aufbauen
- [ ] DP-06-02 Zeiten, Pausen und Tätigkeiten erfassen
- [ ] DP-06-03 Bautagebuch und Rapporte erstellen
- [ ] DP-06-04 Abnahme, Mangel und Nacharbeit führen
- [ ] DP-06-05 Personal, Urlaub und Abwesenheit ergänzen
- [ ] DP-06-06 Ausbildungsmodul umsetzen
- [ ] DP-06-07 Angular-PWA und dauerhafte Offline-Synchronisierung bauen
- [ ] DP-06-08 Mobile und Tablet-Abläufe praktisch prüfen
- [ ] DP-06-09 Eigene Formulare konfigurieren

### DP-07 – Einkauf, Lager, Material, Geräte und Fahrzeuge (P1/P2)
- [ ] DP-07-01 Lieferanten und Einkaufsstammdaten modellieren
- [ ] DP-07-02 Bedarf in geprüfte Bestellung überführen
- [ ] DP-07-03 Wareneingang und Bestandsbewegungen erfassen
- [ ] DP-07-04 Projektverbrauch und Reservierung verbinden
- [ ] DP-07-05 Geräte und Fahrzeuge verwalten
- [ ] DP-07-06 Einkaufsablauf und Differenzen absichern

### DP-08 – Rechnungen, offene Posten und lesendes Banking (P1)
- [ ] DP-08-01 Beleg- und Steuerregeln spezifizieren
- [ ] DP-08-02 Ausgangsrechnungen und Korrekturen umsetzen
- [ ] DP-08-03 Strukturierte E-Rechnungen verarbeiten
- [ ] DP-08-04 Offene Posten und Mahnwesen führen
- [ ] DP-08-05 Eingangsbelege prüfen und freigeben
- [ ] DP-08-06 Bankdaten ausschließlich lesend anbinden
- [ ] DP-08-07 Zahlungsabgleich und Rücknahme implementieren
- [ ] DP-08-08 Finanzablauf und Exporte fachlich prüfen

### DP-09 – Kundenportal, Wartung und persönliche Kommunikation (P1/P2)
- [ ] DP-09-01 Kundenportal mit freigegebenen Inhalten aufbauen
- [ ] DP-09-02 Wartungsverträge und wiederkehrende Termine führen
- [ ] DP-09-03 Kommunikation an Vorgänge binden
- [ ] DP-09-04 Persönliche Benachrichtigungsregeln speichern
- [ ] DP-09-05 Push, E-Mail und WhatsApp anbinden
- [ ] DP-09-06 Morgenbriefing und Wartungsablauf absichern

### DP-10 – Dachmodell und Aufmaß (P1/P2)
- [ ] DP-10-01 Fachmodell, Einheiten und Revisionen
- [ ] DP-10-02 Einfache Dachgeometrie und Mengenkern
- [ ] DP-10-03 Aufmaß erfassen und nachweisen
- [ ] DP-10-04 Dachdetails und technische Bauteile
- [ ] DP-10-05 Externe Aufmaße und Planunterlagen
- [ ] DP-10-06 Mengen, Material und Leistungsverzeichnis verbinden

### DP-11 – Technische Fachmodule (P2; DP-11-01 früh P0)
- [ ] DP-11-01 Verfahren, Datenstände und Nutzungsrechte klären
- [ ] DP-11-02 Rechenkern und nachvollziehbare Ergebnisberichte
- [ ] DP-11-03 U-Wert und thermische Aufbauten
- [ ] DP-11-04 Feuchteschutz und Verfahrensgrenzen
- [ ] DP-11-05 Gefälledämmung und Verlegeplanung
- [ ] DP-11-06 Windlast und Befestigung
- [ ] DP-11-07 Haupt- und Notentwässerung
- [ ] DP-11-08 Steildach, Schnee und konstruktive Nachweise
- [ ] DP-11-09 Photovoltaik als Dachplanungsmodul
- [ ] DP-11-10 Fachsoftwareintegration und Gesamtprüfung

### DP-12 – Schnittstellen und Datenübernahme (P1/P2)
- [ ] DP-12-01 Integrationsverträge und Adapterbasis definieren
- [ ] DP-12-02 GAEB und Material-/Leistungskataloge anbinden
- [ ] DP-12-03 Buchhaltungs- und Lohnexporte umsetzen
- [ ] DP-12-04 Dokumentierte API und Ereignisse bereitstellen
- [ ] DP-12-05 Format- und Anbieterwartung etablieren

### DP-13 – Migration, Sicherung und dauerhafter Betrieb (P1)
- [ ] DP-13-01 Backup und vollständigen Restore implementieren
- [ ] DP-13-02 Datenschutz und Datenlebenszyklus ausarbeiten
- [ ] DP-13-03 Last, Kosten und Betriebsfehler messen
- [ ] DP-13-04 Wiederholbare Datenmigration bauen
- [ ] DP-13-05 Umschaltung und Rückkehr proben
- [ ] DP-13-06 Gesamtfälle und Wartungsdokumentation abschließen

### DP-14 – Kennzahlen und überprüfbare Assistenz (P2/P3)
- [ ] DP-14-01 Kennzahlen definieren und verlinken
- [ ] DP-14-02 Auswertungen und Datenexport ergänzen
- [ ] DP-14-03 Optionale Assistenz kontrolliert integrieren

## Fachliche und technische Abnahmebedingungen

- **Mandanten:** Zwei Betriebe, getrennte Daten, mehrere GF, Rollen, Module, Widerruf.
- **Vieraugenregel:** Keine Selbstfreigabe; Bindung an Objekt, Revision und Inhaltsdigest.
- **Dachsanierung:** Kunde → Dachrevision → Aufmaß → Angebot → Annahme → Auftrag → Nachtrag → Baustelle → Abnahme → Rechnung → Zahlung.
- **Reparatur/Wartung:** Wiederverwendung der Dachakte über mehrere Projekte.
- **Offline:** ganzer Arbeitstag einschließlich Neustart, Berechtigungsänderung und konfliktfreier Synchronisierung.
- **Finanzen:** Teil-/Sammelzahlungen, Skonto, Einbehalt, Korrekturen, Rücklastschriften; Banking strikt lesend.
- **Fachberechnung:** geprüfte Norm-/Datenversionen, unabhängige Referenzfälle und dokumentierte Anwendungsgrenzen; keine ungeprüften Ergebnisse als Nachweis ausgeben.
- **Betrieb:** Fehlerfälle, vollständiger Restore, reproduzierbare CI und reale Desktop-/Tablet-/Mobilprüfungen.

## Kritische Grenzen und offene Entscheidungen

1. Der historische Quellcode vom 07.09.2026 ist nicht nachweislich identisch mit dem zuletzt gehosteten Stand.
2. Reale D1/R2-Daten, Dokumente, Kennwörter und Konfiguration gehören **nicht** in dieses öffentliche Repository.
3. Die Open-Source-Lizenz ist vom Rechteinhaber festzulegen; nicht automatisch annehmen.
4. Normdaten, Herstellerkennwerte, Fachprüfung und Nutzungsrechte sind vor normativen Nachweisen zu klären.
5. E-Rechnungsprofile, Steuerfälle, DATEV-/GAEB-/Bank-/WhatsApp-Verträge müssen versionsbezogen geprüft werden.
6. Ein grüner Build allein ist kein Nachweis vollständiger Fachkonformität.

## Historische erste Reihenfolge

DP-00-01 bis 03, DP-01-01 und DP-11-01 beginnen; danach DP-01-02 bis 06 schließen. Parallel DP-02-01 (Serverbasis), Identität und Dokumentmodell vorbereiten. Erst nach überprüften Grundlagen Finanz- und Freigabeabläufe ausweiten. Der aktuelle Entwicklungsstand steht oben und in den Checkpoints; diese ursprüngliche Reihenfolge behauptet nicht, dass bereits implementierte Grundlagen erneut fehlen.

**Quelle:** DachPlaner – Projektanalyse und Rework Roadmap, 19.10.2026, insbesondere Seiten 17–32. Diese Markdown-Fassung übernimmt Aufgabenkennungen und Titel und verdichtet die Abnahmebedingungen. Für die vollständigen individuellen Aufgabenbeschreibungen gilt der Originalbericht.
