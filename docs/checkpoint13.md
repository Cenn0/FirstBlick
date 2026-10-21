# Checkpoint 13 – Bedienbare Berechnungen und überprüfte XML-Übernahme

Stand: **20.10.2026**. Ausgangspunkt ist `b811fdb0dce7495a5a0768f6a49b2857daae8831`. Der vollständig geprüfte Implementierungsstand ist [fa61a46](https://github.com/Cenn0/FirstBlick/commit/fa61a46566124a8d0eb490b2c1420bbe85e2e35e). Drei Implementierungscommits ändern 18 Quell-/Testdateien; diese nachfolgende Dokumentation verändert den geprüften Anwendungscode nicht.

## 1. Zusammenhängende Funktionen

### Rechnen, im Projekt festhalten und Bericht herunterladen

Der bisher auf zwei einfache Rechner beschränkte Angular-Arbeitsplatz bietet jetzt fünf vorhandene Verfahren: rechteckige Dachgeometrie, homogene Wärmeschichten, stationäres Einzelklima-Feuchtescreening, zwölf unabhängige Klimamonate und lineare rechteckige Gefälledämmung. Alle fachlichen Eingaben sind ausdrücklich erforderlich. Die Oberfläche zeigt Einheiten, Grenzflächen, Monatsprofile und Eckdicken statt ausschließlich unstrukturierter Antwortdaten.

Ergebnisse müssen zur aktuellen Eingabe und zum erwarteten nichtnormativen Verfahren passen. Danach können sie bewusst einem ausgewählten Projekt zugeordnet, serverseitig erneut berechnet und unverändert als `TechnicalRecord` abgelegt werden. JSON-Export und PDF-Erzeugung verwenden diesen Zusammenhang. Der PDF-Download prüft Projekt-/Quellenbezug, Digest, Dateigröße, PDF-Kennung und tatsächliche SHA-256 vor der Downloadfreigabe. Eingabeänderungen und Betriebswechsel verwerfen alte Ergebnisse; verspätete Antworten stellen sie nicht wieder her.

Implementierung: [Komponente](../web/src/app/pages/technical.component.ts), [Oberfläche](../web/src/app/pages/technical.component.html), [Antwort-/Eingabeverträge](../web/src/app/core/technical-workbench.models.ts). Die fachlichen Berechnungsverfahren wurden für diesen Schritt nicht in neue Normnachweise umgedeutet.

### GAEB-Datei bis zum internen Leistungskatalog

Ein eigener Arbeitsplatz `/trade-imports`, erreichbar aus **Import & Export → GAEB & E-Rechnungen**, führt durch Originaldatei, strukturelle Vorschau, Positionsauswahl, eigene Kalkulationswerte, geprüfte Zuordnung und ausdrücklich bestätigte Übernahme. Originalbytes einschließlich UTF-8-BOM und Zeilenumbrüchen bleiben erhalten. Der Quellenhash wird gegen die Serverantwort kontrolliert.

Fremde Angebotspreise bleiben als solche sichtbar. Eigene Materialkosten, Arbeitsstunden, Stundensatz, Zuschlag, Steuer und Preisgrundlage müssen eingegeben werden; auch eine Null ist eine ausdrückliche Eingabe. Änderungen entwerten die vorherige Bestätigung. Die Übernahme erzeugt ausschließlich den gewählten internen Katalogsatz, keine Angebote oder Bestellungen.

Der Importbeleg muss vollständig zum bestätigten Quellen-/Positionssatz passen. Bei unklarer Transportantwort bleibt die Idempotenzkennung für eine unveränderte Wiederholung gleich. Ein unvollständiger Erfolgsbeleg wird nicht als abgeschlossene Übernahme dargestellt.

Implementierung: [Komponente](../web/src/app/pages/trade-imports.component.ts), [Oberfläche](../web/src/app/pages/trade-imports.component.html), [Byte-, Dezimal- und Herkunftsverträge](../web/src/app/core/trade-import.models.ts).

### E-Rechnungssichtung ohne unerlaubte Folgeschritte

UBL-/CII-XML kann in derselben Oberfläche lesend gesichtet werden: Geschäftspartner, Datum, Profilkennung, Positionen, deklarierte Beträge und Prüfhinweise. Große Dezimalbeträge bleiben exakt. Textinhalte werden nicht als HTML ausgeführt. Es gibt weder Buchung noch Versand oder eine erfundene XRechnung-/ZUGFeRD-Konformität. Die Steueranzeige bezeichnet die extrahierte Brutto-Netto-Differenz und keine validierte Steuerkategorienrechnung.

## 2. Fehlerbehebung und serverseitige Absicherung

[fa61a46](https://github.com/Cenn0/FirstBlick/commit/fa61a46566124a8d0eb490b2c1420bbe85e2e35e) ergänzt am [GAEB-Endpunkt](../src/FirstBlick.Api/GaebCatalogEndpoints.cs) den Mandanten in die geprüfte Digestbindung. Eine identische Datei mit gleicher Kostenangabe kann deshalb nicht mit dem Prüfstand eines anderen Betriebs übernommen werden. Vor der Änderung waren die Entitäten bereits mandantengetrennt, **der Prüfstand selbst aber nicht an den Betrieb gebunden**; dies war keine nachgewiesene Möglichkeit zum Lesen fremder Katalogdaten.

JSON-Pflichtfelder verhindern außerdem, dass ausgelassene numerische Kostenparameter beim Deserialisieren zu impliziten Nullkosten werden. Leere oder fehlende Positionskennungen werden vor der Zuordnung als Eingabefehler abgewiesen. Die vorhandene Transaktion wird jetzt zusätzlich mit einer Dublette an zweiter Stelle eines Batches geprüft: Die zuerst verarbeitete neue Position wird vollständig zurückgerollt.

**Kompatibilität:** Noch nicht gebuchte GAEB-Prüfdigests aus dem früheren, nicht mandantengebundenen Verfahren müssen neu erzeugt werden. API-Aufrufer müssen alle dokumentierten Kostenfelder mitsenden; bisher ausgelassene Werte werden nicht mehr akzeptiert. Eine ausdrücklich eingegebene Null bleibt zulässig. Historische Katalogpositionen werden durch diese Änderung nicht umgeschrieben.

Die erste neue UI-Fassung führte zu drei Fehlern im bestehenden Bedienungstest; passende Reitersemantik und Bedienbezeichnungen wurden wiederhergestellt. Der danach ausgeführte vollständige Browserlauf bestand 289/291 Fälle und zeigte zwei reale horizontale Überläufe im Tablet-Layout. Die Layoutkorrektur verwendet begrenzte Grid-Spalten, umbrechende Aktionen und einen passenden Einspalten-Breakpoint. **Keine Tests wurden entfernt, übersprungen oder für diese Korrektur abgeschwächt.**

## 3. Tatsächlich ausgeführte Verifikation

Die folgenden Zahlen stammen aus dem abgeschlossenen [CI-Lauf 39486070941](https://github.com/Cenn0/FirstBlick/actions/runs/39486070941) zum Implementierungsstand `fa61a46`. Der PR-Lauf prüfte den synthetischen Merge-Checkout `a32035b81cd3a3a79390df0b0fc4ed313b7a1349`; dies ist kein tatsächlich vollzogener Merge in `main`.

| Suite | Erfolgreich | Neu in diesem Schritt |
|---|---:|---:|
| C# Core | 293/293, 0 übersprungen | Rechenkern unverändert |
| HTTP-/PostgreSQL-/Worker-Suite | 152/152, 0 übersprungen | 6 HTTP-/PostgreSQL-Fälle |
| Frontend-Unit-Tests | 100/100 | 55 Fälle: 26 Berechnung, 29 XML/Mapping |
| Browser-Fixtures | 291/291 | 22 Abläufe × 3 Viewports = 66 Kombinationen |
| Angular-Produktionsbuild | Erfolgreich | Beide Arbeitsplätze in Lazy-Chunks |

Die **97 Browser-Testidentitäten** werden je auf Desktop 1440×1000, Tablet 1024×768 und Mobil 390×844 ausgeführt. Die zusätzlichen 66 Kombinationen sind keine 66 unabhängigen fachlichen Szenarien. Der abschließende Browserlauf meldet 291 bestanden und keine fehlgeschlagenen oder nur durch Wiederholung geretteten Fälle.

Neue Nachweise:
- [Technik-Unit-Tests](../web/tests/technical-workbench.test.ts): vollständige Eingaben, Monatsfolge, nichtnormative Antwortverträge, normalisierte Quellen und Speicherbezug.
- [XML-/Mapping-Unit-Tests](../web/tests/trade-import.test.ts): Bytebudget, unveränderte Originalbytes, exakte Dezimaltexte, eindeutige Positionen, explizite eigene Preise und vollständige Importbelege.
- [Technik-Browserabläufe](../web/tests/browser/technical-workbench.spec.ts): Berechnen/Speichern/JSON/PDF, beschädigte Datei, veraltete Antwort, Betriebswechsel, Rechteentzug, Feuchtewarnungen und Tastaturbedienung.
- [XML-Browserabläufe](../web/tests/browser/trade-imports.spec.ts): bestätigter Katalogimport, geänderte Kosten, falsche Hashes, unklare Wiederholung, unvollständiger Beleg, Rollen, große Geldbeträge, XML-Text und Seitenwechsel.
- [GAEB-HTTP-/PostgreSQL-Grenzen](../tests/FirstBlick.Api.Tests/GaebCatalogBoundaryTests.cs): Mandantenbindung, erforderliche JSON-Werte, ungültige Kennungen und vollständiger Rollback.
- [Technische HTTP-/PostgreSQL-Abläufe](../tests/FirstBlick.Api.Tests/TechnicalWorkbenchApiTests.cs): sortierte Monatsberichtablage, Idempotenz-Replay und echte mandantenfremde Projekt-/Berichtsablehnung.

Der ebenfalls erfolgreiche [Operations-Lauf 39486070896](https://github.com/Cenn0/FirstBlick/actions/runs/39486070896) führte den tatsächlichen Container-Stack, API-/Identity-/Mandanten-Smoke, PostgreSQL-/versionierten S3-Restore, Migrationsdrill sowie Backup und Neustart aus.

**Trennung der Nachweise:** Die neuen Browserfälle sind kontrollierte HTTP-Fixtures und kein neuer Live-Browserlauf gegen die echte Anwendung. Die sechs zusätzlichen Servertests verwenden echte HTTP-Endpunkte und PostgreSQL. Der Operations-Lauf prüft den vorhandenen Container-/Restore-Ablauf, nicht automatisch jeden neuen UI-Pfad. Die Ausführung erfolgte in GitHub Actions; es wird kein zusätzlicher lokaler Testlauf behauptet. Maschinenlesbare Angaben stehen in [evidence/checkpoint13.json](evidence/checkpoint13.json).

## 4. Aufgabenbezogener Fortschritt und Restumfang

Diese Tabelle ergänzt und berichtigt für die betroffenen Aufgaben den älteren Stand von [roadmap-evidence.md](roadmap-evidence.md). Sie erklärt keine vollständigen Epics allein aufgrund neuer Oberflächen für erledigt.

| Aufgabe | Neuer belegter Umfang | Weiter fehlend |
|---|---|---|
| DP-11-02 | Bedienbarer Ablauf vom validierten Recheninput bis zur bewussten Projektablage und geprüftem Berichtdownload | Vollständige fachlich geprüfte Berichte für alle vorgesehenen technischen Verfahren |
| DP-11-03 | Eingaben und Ergebnisdarstellung des vorhandenen homogenen Wärmeverfahrens im gemeinsamen Arbeitsplatz | Vollständige Normverfahren, u.a. inhomogene Kombinationen und weitere Bauteilklassen |
| DP-11-04 | Einzelklima und vollständige zwölfmonatige **Screening**-Eingabe, Monatsdarstellung und gespeicherter Verlauf | Normative Verfahrenswahl, Kondensatmassen, Verdunstung, Jahresbilanz und Referenzvalidierung |
| DP-11-05 | Bedienbare lineare Mengenhilfe mit Eckdicken, Projektbericht und Dateiexport | Herstellerplatten, Verlegeplan, Zuschnitt, reale Ablaufgeometrie und Optimierung |
| DP-12-02 | Strukturvorschau bis zum bestätigten mandantengebundenen internen Katalogimport einschließlich Herkunft und atomarem Rollback | Vollständiger XSD-Abgleich, Langtext-/Anlagenverlustfreiheit, Originaldateiarchiv, Rückexport und lizenzierte Kataloge |
| DP-08-03 | Lesende E-Rechnungssichtung mit exakten Beträgen und abgesicherten Antwortverträgen | Externe Profilvalidierung, PDF-Anhänge, Dubletten-/Lieferantenprüfung, Buchung und strukturierte Ausgabe |
| DP-03-06/07, DP-06-08 | Neue rollen-/kontextgebundene Oberflächen mit Desktop-/Tablet-/Mobilregressionen und serverseitigen Negativfällen | Unabhängige Gesamtsicherheitsprüfung, breitere echte Geräte-/Browser- und vollständige Live-Abdeckung |

Die ursprünglichen 100 Aufgaben und ihre Kriterien bleiben bestehen. Es gibt keine neu berechnete pauschale Fertigstellungsquote. Der neue Umfang ist als Softwarefortschritt veröffentlicht; daraus wird keine externe Zertifizierung oder automatische Zusammenführung in `main` abgeleitet.
