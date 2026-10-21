# DP-12-02 – quellgebundene GAEB-Preisarbeitslisten

Stand: 21.10.2026. Implementierung und zugehörige GitHub-Prüfungen: [PR #10](https://github.com/Cenn0/FirstBlick/pull/10). Der Ablauf ergänzt die bereits vorhandene [offizielle X83/X84-Prüf- und Exportkette](advanced-exchange-and-physics.md). Er ersetzt weder deren Server-/XSD-Prüfungen noch eine ausdrückliche Angebotsfreigabe.

## Bedienbarer Ablauf

1. Im Bereich **Schnittstellen / GAEB** das unveränderte X83- oder X84-Original strukturell und gegen die vorhandenen Original-XSD prüfen.
2. **Aktuelle Preise als CSV herunterladen** exportiert alle Positionen einschließlich aktueller Entwurfswerte. Fehlende Preise bleiben offen; aus einem leeren Feld entsteht kein Nullpreis. Die Dateikennung enthält einen Ausschnitt der Quellprüfsumme.
3. Die Arbeitsliste lokal bearbeiten und als CSV UTF-8 mit Semikolon-Trennzeichen speichern. Es dürfen ausschließlich Entscheidung, Einheitspreis und Preisgrundlage geändert werden. Die Zeilen dürfen umsortiert, aber nicht weggelassen oder vervielfacht werden.
4. **Bearbeitete Preisarbeitsliste prüfen** liest die Datei ausschließlich im Browser und zeigt Änderungen gegenüber den aktuellen Eingaben. Auch bei einem Fehler in einer späteren Zeile bleibt der gesamte bisherige Preisentwurf unverändert.
5. Nach Sichtung der Vorher-/Nachher-Werte und ausdrücklicher Bestätigung übernimmt **Geprüfte Preisarbeitsliste übernehmen** die Werte in den lokalen Entwurf. Unvollständige Entscheidungen oder fehlende Preisgrundlagen werden gezählt und bleiben erkennbar unvollständig.
6. Der bestehende Ablauf verlangt weiterhin Bieter-/Projektangaben, vollständige Preisgrundlagen, serverseitige Neuberechnung und Original-/Ausgabe-XSD-Prüfung. Erst danach sind die gesonderte Bestätigung und Archivierung der X84 möglich. Der Arbeitslistenimport selbst archiviert nichts und übermittelt kein Angebot.

Eine neue Arbeitsliste oder eine Änderung der Eingaben verwirft eine frühere X84-Vorschau und deren Bestätigung. Änderungen nach einer CSV-Prüfung entwerten auch diese Prüfung. Die Bestätigungsfunktion prüft Original und aktuellen Preisstand erneut und gibt unabhängige Preisobjekte zurück; sie überschreibt keine zwischenzeitlichen Eingaben stillschweigend.

## Festes Arbeitsformat

Vertragskennung: `firstblick.gaeb-price-worksheet/1.0.0`. Dies ist **kein GAEB-Datenaustauschformat**, sondern ein FirstBlick-Arbeitsformat für die Bepreisung.

| Spalte | Inhalt und Regel |
|---|---|
| `contract` | Unveränderte Vertragskennung |
| `source_sha256` | `sha256:` und vollständige Prüfsumme des XML-Originals |
| `position_key` | `pos:` und originale Gliederungskennung; führende Nullen erhalten |
| `item_sha256` | `sha256:` und vollständiger Positionsdigest |
| `quantity` | `qty:` und unveränderte Quellmenge, bei unbestimmter Menge `qty:?` |
| `unit` | `unit:` und unveränderte Quelleinheit |
| `description` | `text:` und unveränderte Vorschau-Beschreibung |
| `currency` | `EUR`; nur im Original ausdrücklich deklarierte EUR-LVs |
| `decision` | `offer`, `not-offered` oder `pending` |
| `unit_price_eur` | Expliziter Dezimalpreis bei `offer`, sonst leer |
| `price_evidence` | `text:` und die bearbeitbare Preisgrundlage |

`offer` verlangt einen nichtnegativen Preis bis einschließlich `999999999` EUR mit höchstens sechs Nachkommastellen. Dezimalpunkt oder Dezimalkomma sind möglich; Formeln, Exponenten, Tausendertrennzeichen und implizite Rundung nicht. Die Verarbeitung verwendet Dezimaltexte und eine ganzzahlige Bereichsprüfung, keine binäre Gleitkommaarithmetik. Ein ausdrücklich eingetragener Nullpreis bleibt von `pending` unterscheidbar.

Eine leere Preisgrundlage darf als noch unvollständiger Entwurf zurückgelesen werden. Sie wird gesondert gezählt und erfüllt **nicht** die Pflichtprüfung vor der X84-Erzeugung. Bei `pending` und `not-offered` wird ein versteckter Preis abgewiesen. Unbestimmte Quellmengen werden lediglich wiedererkannt; ihre Präsenz hebt die bestehenden Ablehnungsregeln des X84-Writers nicht auf.

Grenzen: maximal 1000 Positionen, elf Spalten, 4 MiB UTF-8, 8192 Zeichen je CSV-Zelle und 2000 Zeichen je Preisgrundlage. Eine UTF-8-BOM ist optional. Doppelte Anführungszeichen und mehrzeilige, korrekt zitierte Quellbeschreibungen werden verarbeitet. Ungültiges UTF-8, zusätzliche Spalten, falsche Überschriften, unvollständige Anführungszeichen und zusätzliche Leerzeilen werden abgewiesen. Der Parser errät keine anderen CSV-Dialekte.

## Herkunft, Rechte und Sicherheit

Unveränderliche Spalten werden mit der aktuell ausgewählten Quellvorschau verglichen, nicht aus der CSV als neue Wahrheit übernommen. Die SHA-256-Bindung verhindert den versehentlichen Preisübertrag auf geänderte Originale; sie ist **keine elektronische Signatur** und kein Nachweis der fachlichen Richtigkeit eines eingegebenen Preises.

Alle frei beeinflussbaren Text- und Identitätszellen werden mit einem festen, beim Rücklesen erforderlichen Literalpräfix ausgegeben. Dadurch beginnt beispielsweise eine Formelzeichenfolge aus einer LV-Beschreibung im exportierten Zellwert mit `text:` statt mit `=`. CSV-Zitierung allein ist hierfür nicht ausreichend; siehe [OWASP CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection). Formeln sind in der Preiszelle unzulässig. Die Anwendung entfernt keine schützenden Präfixe, um fremde Office-Umformungen still zu akzeptieren.

Die Oberfläche erfordert weiterhin Katalog-Schreibrecht. Betriebs-/Berechtigungswechsel löschen Original, Preise, Arbeitslistenprüfung, Bestätigungen und Bieterangaben. Asynchrone Dateiantworten werden gegen die aktuelle Zugriffssignatur und Bearbeitungsgeneration geprüft. Auch der bestehende GAEB-Dokumentdownload prüft den Zugriffskontext jetzt nochmals **nach** der asynchronen SHA-256-Berechnung, bevor eine Datei an den Browser übergeben wird.

Der CSV-Import löst keine neue HTTP-Anfrage aus. Der Download enthält vertrauliche Kalkulationswerte und liegt danach außerhalb des serverseitigen Zugriffsschutzes; Aufbewahrung und Weitergabe sind entsprechend zu behandeln. Entwürfe werden nicht neu in LocalStorage/IndexedDB gespeichert und gehen ohne eigenen CSV-Download beim Verlassen der Seite verloren.

## Nachweise und ausdrücklich verbleibende Grenzen

- Codec und Regeln: `web/src/app/core/gaeb-price-worksheet.ts`.
- Unit-Regressionen: `web/tests/gaeb-price-worksheet.test.ts`, einschließlich Quell-/Positionsbindung, umsortierter Zeilen, Atomarität, Vorzeichen-/Formel-/Präzisionsfehler, Größenbudgets, ungültigem UTF-8, 1000 Positionen und veralteter Bestätigung.
- Oberfläche: `web/src/app/pages/gaeb-bid-exchange.component.ts` und `.html`.
- Browser-Regressionen: `web/tests/browser/gaeb-price-worksheet.spec.ts`; Ausführung in den vorhandenen Desktop-/Tablet-/Mobil-Projekten. Diese Tests verwenden kontrollierte HTTP-Antworten und sind ausdrücklich kein zusätzlicher echter XSD-/PostgreSQL-Lauf. Die bestehenden Server-/Standards-/Operations-Suiten bleiben getrennte Prüfungen.

Vorhandener Testcode ist kein bestandener Testlauf. Maßgeblich sind die abgeschlossenen Ergebnisse für den endgültigen PR-Commit; die PR-Diskussion hält die tatsächlich gelesenen Ergebnisse fest.

Nicht hinzugefügt werden vollständige GAEB-Phasenunterstützung, Bietertextergänzungen, Anlagenrückexport, besondere Preis-/Positionsformen, Hersteller-/STLB-Lizenzdaten oder eine automatische Angebotsabgabe. Eine pauschale Kompatibilitätsprüfung mit Excel, LibreOffice oder fremden AVA-Produkten wurde nicht durchgeführt. Der strikt beschriebene CSV-Vertrag wird unterstützt, nicht beliebige Tabellenkalkulations-Exporte. **DP-12-02 bleibt deshalb ein erweiterter Teilumfang und wird nicht vollständig abgehakt.**
