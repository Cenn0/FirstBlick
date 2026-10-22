# Abschlussregister der 46 früheren Teilumfänge

Der Nutzerauftrag bezieht sich auf die **46 T-Einträge bei Commit `7cf77c8976e96e07a64a3c22ec4b7eac312f53e2`**. Diese feste Menge wird hier erhalten. Der aktuelle Bestand bei Ausgangscommit `324f495691c25e7d405e4762c3cf8cfaf0bda8fe` enthält zusätzlich DP-11-06/07/08 als T: **49 K / 49 T / 2 O**. K bezeichnet einen technischen Kern, keine vollständige ursprüngliche Fachabnahme. Kein Eintrag wird durch dieses Register als erledigt umklassifiziert.

Die folgenden nächsten Schritte sind aus den Restlücken der [aktuellen Nachweismatrix](roadmap-evidence.md) abgeleitet. **Auch softwareseitig bestehen erhebliche Restarbeiten.** Fehlende externe Nachweise werden separat genannt; sie sind kein Ersatz für Implementierung. Reine Code-/Fixture-Prüfungen erledigen weder reale Migrationen noch rechtliche, normative oder betriebliche Abnahmen.

## Abhängige Arbeitspakete

| Paket | Inhalt | Abhängigkeit für Gesamtabnahme |
| --- | --- | --- |
| A | Tatsächliche Quellen, Anforderungen, Norm-/Datenrechte | Voraussetzung für verbindliche Baseline und fachliche Referenzabnahme; unabhängige Softwarearbeit kann beginnen |
| B | API-Verträge, begrenzte Datenzugriffe, Rechte und Betrieb | Basis für C–I |
| C | Stammdaten, Projekte, Katalog und Angebot | B; A für reale Import-/Fachfälle |
| D | Personal, Baustelle, Einkauf und Geräte | B und C |
| E | Finanz- und Austauschprofile | B und C; freigegebene Steuer-/Anbieter-/Katalogdaten |
| F | Kommunikation, Kalender und Zustellung | B und C; gewählte Anbieter |
| H | Dach- und Fachverfahren | B und C; A für normative und Herstellerprüfung |
| I | Berichte und Kennzahlen | Historisierte Quellen aus C–F |
| G | Reale Gesamtfälle, Datenschutz, Last, Migration und Umschaltung | Zugehörige Implementierung sowie reale Daten-/Betriebsfreigaben |

Buchstaben kennzeichnen Abhängigkeitspakete, keine Prioritäts- oder Fertigstellungsquote. Die drei inzwischen hinzugekommenen Fachteilumfänge und die beiden O-Aufgaben bleiben zusätzlich in der 100-Aufgaben-Matrix sichtbar.

## Fixierter Arbeitsbestand

| ID | Paket | Nächster Implementierungs-/Prüfschritt | Externe Grundlage für vollständigen Abschluss |
| --- | --- | --- | --- |
| DP-00-01 | A | Gehosteten Altstand gegen den Quellstand vergleichen | Tatsächlicher gehosteter Quell-/Versionsstand |
| DP-00-02 | A | Daten-/Objektinventar gegen reale Exporte und verfügbare Originalbytes prüfen | D1-/R2-Export; vier im aktuellen Upload fehlende Demos |
| DP-00-03 | A | Individuelle Kriterien und Prioritäten den 100 IDs zuordnen | Bestätigung der fachlich verantwortlichen Personen |
| DP-00-04 | G | Reparatur-/Wartungsketten als echte Browserabläufe ergänzen | Drei reale Betriebsfälle mit Ausnahmen und Fachabnahme |
| DP-02-02 | B | Vollständige Erfolgs-/Fehlerschemas und automatischen Vertragsvergleich ergänzen | Verbindliche Kompatibilitätspolitik für externe Verbraucher |
| DP-02-06 | B | Verbleibende unbeschränkte Mandantenhistorien in RecordPolicy/Finanzpfaden begrenzen | Repräsentative Datenmengen und Latenzziele |
| DP-02-07 | B | Metrikexport, Tracing, Alarmierung und Rotationspfade vervollständigen | Betriebsplattform, Empfänger, SLOs und reale Rotation |
| DP-03-03 | B | Befristete, feingranulare Vollmachten mit Widerruf und Regressionen implementieren | Bestätigte Delegationsregeln |
| DP-04-01 | C | Stammdatenhistorie, bewusste Zusammenführung und externe Kontaktsynchronisierung ergänzen | Freigegebene Postfach-/Kontaktanbieter und reale CRM-Fälle |
| DP-04-03 | C | Mehrstufige Arbeitspakete, allgemeine Kalender und Terminvor-/nachläufe ergänzen | Betriebliche Projekt-/Arbeitskalenderregeln |
| DP-05-01 | C | Katalogversionen und Lieferantenpreis-/Importmapping vervollständigen | Rechtssicher nutzbare reale Kataloge und Pflegeverantwortung |
| DP-05-03 | C | Angebotslayout, Textbausteine, Positionsbearbeitung und Varianten vervollständigen | Repräsentative Angebotsmuster und Fachfreigabe |
| DP-05-07 | G | Gesamtfall um verbleibende Planungs-, Material- und Zahlungsvarianten erweitern | Realer vollständig fachlich abgenommener Geschäftsvorfall |
| DP-06-03 | D | Kundenabnahme-/Signaturpfad und Aufbewahrungskette vervollständigen | Abgenommenes Signatur-/Archivverfahren und realer Baustellentag |
| DP-06-05 | D | Urlaubskonten, Überträge und explizite Kalender-/Tarifregeln implementieren | Gültige Betriebsregeln und freigegebene Personalbestandsdaten |
| DP-06-06 | D | Ausbildungs-/Schulabläufe und offizielles Berichtsheftformat vervollständigen | Zuständiges Format und Ausbildungsabnahme |
| DP-07-02 | D | Bedarfsableitung, Freigabeketten und Bestelladapter ausbauen | Lieferantenvertrag und Format-/Anbieterreferenzfälle |
| DP-07-05 | D | Kalender je Prüfart, Belegprüfung, Speicherrestbereinigung und Integrationen ergänzen | Prüf-/Fristregeln, Telematikvertrag und steuerliche Fahrtenbuchabnahme |
| DP-07-06 | D | Lieferantengutschriften, Schadensabläufe und externe Retourenketten ergänzen | Reale Einkaufsfälle und Anbieterbestätigung |
| DP-08-01 | E | Steuer-/Pflichtfeldfälle als versionierte Belegprofile spezifizieren | Aktuelle Rechts-/Steuergrundlagen und Fachfreigabe |
| DP-08-03 | E | Weitere EN-16931-Sonderfälle, PDF/A-3-Profile und Zustelladapter ergänzen | Reale Referenzbelege, Providerverträge und Finanzabnahme |
| DP-08-05 | E | Vollständigen Original-/OCR-/Profilprüfprozess mit bewusster Freigabe ergänzen | Echte Lieferantenbelege und Fachabnahme |
| DP-08-06 | E | Nur lesenden Bankadapter mit sicherer Tokenverwaltung implementieren | Ausgewählter Bankanbieter, Vertrag und Testzugang |
| DP-08-08 | G | Anbieter-/Buchhaltungs-Abnahmesuite mit Referenzdaten vervollständigen | Steuerberater-/Anbieterabnahme und produktiver Kontoabgleich |
| DP-09-02 | F | Kalenderintegration, Zustellung und kaufmännische Folgekette ergänzen | Gewählter Kalender-/Versandprovider und realer Wartungsfall |
| DP-09-03 | F | Postfach-/Chatanbieter mit Vorgangszuordnung und Originalhistorie anbinden | Freigegebene Anbieter und Testpostfächer |
| DP-09-05 | F | Push-/WhatsApp-Adapter und SMTP-Zustellnachweise ergänzen | Providerkonten, Schlüssel und genehmigte synthetische Zustellziele |
| DP-09-06 | G | Morgenbriefing-/Wartungsgesamtfall mit tatsächlichem Worker ausführen | Reale Betriebsdaten und gewählter Zustellprovider |
| DP-10-04 | H | Parametrische technische Details und Ableitung von Fachgrößen ergänzen | Validierte Detail-/Berechnungskonventionen |
| DP-10-05 | H | Expliziten Aufmaßadapter mit Koordinaten-/Einheitenprüfung ergänzen | Gewähltes DXF-/IFC-/Geräteformat und reale Referenzdateien |
| DP-11-01 | A | Verfahrens-/Datenversions- und Rechteverzeichnis abschließen | Lizenzierte Normen, Herstellerdaten und unabhängige Referenzabnahme |
| DP-11-03 | H | Fehlende Luftschicht-/Korrekturverfahren und Wärmebrückenverfahren ergänzen | Normtexte, nationale Anwendung, unabhängige Material-/Bauteilreferenzen |
| DP-11-04 | H | Ungeklärten ISO-Anhang-C-Vergleich lösen und nationale Verfahrensprüfung ergänzen | Norm-/Klimadaten, Schadensgrenzen und unabhängige Fachabnahme |
| DP-11-05 | H | Nichtrechteckige Geometrie, Innenabläufe, Kehlen, Öffnungen und Zuschnittoptimierung ergänzen | Geprüfte lizenzierte Herstellerkataloge und Ausführungsfreigabe |
| DP-11-09 | H | Dachformen, Verschattung/Ertrag, Montage-/Last-/Elektro-/Brandschutzintegration ergänzen | Hersteller-/Standortdaten und unabhängige Fachabnahmen |
| DP-12-02 | E | Weitere GAEB-Phasen, besondere Positionen, Bietertexte und Anlagen ergänzen | Rechtssicher nutzbare Katalogdaten, AVA-Referenzen und Freigabe |
| DP-12-03 | E | Versionierte providerfertige Buchhaltungs-/Lohnadapter ergänzen | Anbieterwahl, Kontierungs-/Tarifregeln und Referenzabnahme |
| DP-12-04 | B | Externe Ereigniszustellung und OpenAPI-Kompatibilitätsprüfung ergänzen | Verbraucherverträge und vereinbarte Versionspolitik |
| DP-12-05 | B | Versionsüberwachung und Profilupgrade-Verfahren ergänzen | Benannte Verantwortliche und freigegebene Fremdprofile |
| DP-13-02 | G | Auskunftskategorien, Aufbewahrung/Löschung und Konfliktbehandlung vervollständigen | Rechtlich abgestimmte Regeln und Betreibervereinbarungen |
| DP-13-03 | G | Reproduzierbare Last-/Kapazitäts-/Kosten- und Ausfallszenarien ergänzen | Repräsentative Mengen, Plattformkosten und Betriebs-SLOs |
| DP-13-04 | G | Quelladapter-/Ownershipmapping am tatsächlichen Export verifizieren | Vollständiger D1-/R2-Export mit Quellkontrollsummen |
| DP-13-05 | G | Umschaltung, Writes danach und Rückkehr unter realer Infrastruktur proben | DNS-/Proxy-/Zielumgebung und repräsentative Bestandsdaten |
| DP-13-06 | G | Gesamtnachweis und Betriebsdokumentation aller ursprünglichen Anforderungen integrieren | Reale Betriebs-/Altquellen- und Fachabnahmen |
| DP-14-01 | I | Weitere definierte Kennzahlen und historisierte Statusreihen implementieren | Abgestimmter betrieblicher KPI-Katalog |
| DP-14-02 | I | Zusätzliche Berichte, interaktive Trends und Zustellung ergänzen | Priorisierte Berichtskriterien und Zustellprovider |

## Fortsetzung vom 23.10.2026

Für **DP-11-04/05** wurden die [Clientverträge, Grenzflächenanzeige und eigenständigen UI-Regressionspfade](advanced-technical-client.md) ergänzt. Die Softwarelücke „keine eigenständigen Client-/Browserregressionen“ wird mit den ausgeführten finalen CI-Ergebnissen geschlossen; die übrigen Fach- und Geometrielücken bleiben ausdrücklich offen.

Eine weitere Fortschreibung muss zu jeder betroffenen ID konkrete Implementierung und ausgeführte Prüfungen nennen. Eine leere Restspalte, ein grüner Build oder eine zusätzliche Textseite begründen keinen vollständigen Abschluss.

## Anhänge des aktuellen Auftrags

`daemmdemo.exe`, `handwerkdemo.exe`, `dachdemo.exe` und `bauphysik-demo.exe` wurden für diesen Auftrag bereitgestellt. `flachdach-demo.exe`, `drain-demo.exe`, `dd-demo.exe` und `steildach-demo.exe` konnten nicht zugestellt werden. Diese Fortschreibung enthält keine neue Byte-/Methodenanalyse dieser Dateien und behauptet keinen vollständigen Originalbestand. Proprietäre Programme und Herstellerdaten werden nicht ins Repository kopiert.
