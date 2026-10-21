# Lagerbestandskorrekturen – Beleggebundene Buchungsketten

Stand: 21.10.2026 · FirstBlick · Roadmap **DP-07-03** und **DP-07-06** (technische Erweiterung).

## Verfahren

Das bestehende Wareneingangs-, Reservierungs- und Verbrauchssystem wird um vier **explizit ausgelöste** und revisionsfähige Buchungen erweitert. Kein Vorgang löscht oder aktualisiert eine schon gebuchte Materialbewegung. Sämtliche Kommandos laufen innerhalb derselben betriebsweiten PostgreSQL-Transaktion mit Mandanten-Lock, aktueller Rollen-/Modulprüfung, Idempotency-Key, Audit und Outbox.

| Fachvorgang | Schnittstelle | Vorbedingungen und Wirkung |
|---|---|---|
| Inventurdifferenz | `POST /api/v1/materials/{id}/inventory-count` | Erfasster Buchbestand `expectedOnHand` muss dem aktuell gebuchten Wert entsprechen. Der gezählte Bestand `countedOnHand` darf nicht negativ und nicht kleiner als die noch gebundene Reservierung sein. Der explizite Inventurbeleg `evidenceReference` und die Begründung sind Pflicht. Die Differenz wird als unveränderliche, **vorzeichenbehaftete** `Adjustment`-Bewegung gebucht; ihre Referenz enthält den Zählbeleg. |
| Lagerumbuchung | `POST /api/v1/materials/{id}/transfer` | Unterschiedliche Lagerorte und ausreichender **nicht reservierter** Ausgangsbestand. `expectedFromOnHand` muss mit dem aktuell gebuchten Quellbestand übereinstimmen, auch wenn die gewünschte Menge weiterhin verfügbar wäre. Optional bindet `transferSourceId` beide Buchungen an einen konkreten Wareneingang oder einen bereits herkunftsgebundenen Umlagerungseingang. Genau zwei Bewegungen (negativ `TransferOut`, positiv `TransferIn`) mit gemeinsamer Referenz werden **atomar** geschrieben. |
| Lieferantenrückgabe | `POST /api/v1/stock-movements/{receiptId}/supplier-return` | Ursprüngliche gebuchte `Receipt`, eindeutige Bestellung/Position, bisher noch nicht zurückgegebene Originalmenge und freier Bestand am Rückgabeort. Optional verweist `returnTransferId` auf den letzten `TransferIn` einer expliziten Herkunftskette oder einen älteren direkten Transfer (siehe unten). Negative `SupplierReturn`, Referenz ist weiterhin die ID des Originalwareneingangs. Bestellung wird anhand aller Zu-/Rückgänge auf `Ordered`, `PartiallyReceived` oder `Received` zurückgerechnet; Fehlmengen können erneut angeliefert werden. |
| Baustellenrückgabe | `POST /api/v1/stock-movements/{consumptionId}/project-return` | Ursprünglicher gebuchter und reservierungsbezogener Projektverbrauch. Positive `Return`, nur bis zur noch nicht retournierten Originalmenge, mit identischem Projekt, Material, Lagerort und Einstandswert. Die Reservierung vermindert `consumedQuantity` und erhöht `releasedQuantity` in derselben Transaktion; der noch reservierte Rest bleibt unverändert. Die Nachkalkulation behandelt solche Rückgaben als negative Materialkosten. |

Alle schreibenden Vorgänge benötigen eine Begründung beziehungsweise nachvollziehbare Belegreferenz. Vorgänge werden nicht aus einer bloßen Belegvorschau, aus CSV-Daten oder unbestätigten Bestellmengen automatisch erzeugt.

**Abruf:** `GET /api/v1/materials/{id}/movements?location=Hauptlager&page=1` liefert maximal 25 Bewegungen, gegen aktiven Mandanten und Leserechte geprüft, nach Erstellungszeit absteigend sortiert. Der optionale, serverseitig geprüfte Parameter `kind=TransferIn` liefert die paginierten Umbuchungsbelege des Materials über alle Lagerorte. Der Abruf ist für den Angular-Arbeitsplatz `/inventory-control` vorgesehen; vollständige Journalhistorien werden dort nicht in einem unbeschränkten Objekt geladen.

**Herkunft:** `GET /api/v1/stock-movements/{id}/transfer-chain` liefert für einen Originalwareneingang oder herkunftsgebundenen Umlagerungseingang Originalbeleg, geordnete Lagerwechsel, unveränderten Einstandswert und den noch nicht zugeordneten Belegrest. Eine Kette ist auf **32 Umlagerungen** begrenzt. Diese Belegrestmenge ersetzt nicht die gesonderte Prüfung des verfügbaren, unreservierten Lagerbestands.

## Status-, Bestands- und Kostenregeln

- Ein Wareneingang erfüllt eine Bestellposition nur in Höhe **Originalzugänge minus belegte Lieferantenrückgaben**. Andere Bewegungsarten dürfen die Bestellmenge nicht beeinflussen.
- `OnHand = Σ signed movements`. Offene Reservierung = `Σ(quantity - consumed - released)`. `Available = OnHand - reserved`. Negativbuchungen aus Inventur, Transfer und Lieferantenrückgabe sind höchstens in der ungebundenen Menge zulässig.
- Für jede Rückgabe wird die konkrete Ursprungsbewegung genannt. Bereits zu dieser Ursprungsbewegung gebuchte Rückgaben werden in die Obergrenze eingerechnet; fehlende oder fremdmandantige Belege werden zurückgewiesen.
- Wiederholte HTTP-Anforderungen verwenden denselben Idempotency-Key **nur bei identischem Payload und derselben Revision**; ein Replay erzeugt keine Doppelbuchung.
- Bestehende `StockMovement`-Digests bleiben bitweise unverändert; zusätzliche `SupplierReturn`-Enumwerte verändern keine bereits archivierten Nachweise.
- Eine Baustellenrückgabe wird mit dem ursprünglichen Einstandswert gebucht. In der vorhandenen Projekt-Nachkalkulation senkt die positive `Return`-Menge die Materialkosten, ohne die Originalverbrauchsbuchung zu manipulieren.
- Konkurrierende Rückgaben werden gegen den aktuellen Belegrest und den aktuellen Nettoverbrauch der exakt zugehörigen Reservierung geprüft. Ein abgewiesener zweiter Vorgang verändert weder Lagerjournal noch Reservierungszähler; ein Replay des erfolgreichen Vorgangs verändert sie ebenfalls nicht.

### Lieferantenretoure nach einer direkten Umbuchung

Die Oberfläche kann zusätzlich zum Originalwareneingang einen bereits gebuchten Umlagerungseingang auswählen. Der Server akzeptiert ihn nur, wenn sein exakt passender Ausgangsbeleg aus dem ursprünglichen Wareneingangslager stammt, beide Buchungen gültige Digests, denselben Mandanten und dasselbe Material haben, die Bewertungsbeträge dem Originalzugang entsprechen und die Zeit- und Mengenangaben des Paars übereinstimmen. Der Transfer darf nicht vor dem Originalzugang liegen.

Die Rückgabe ist gleichzeitig auf den noch nicht retournierten Originalwareneingang, die noch nicht für Rückgaben verwendete Transfermenge und den freien Bestand am Ziellager begrenzt. Ihre `reference` bindet weiter den Originalwareneingang; `returnTransferId` bindet zusätzlich den Transfer und wird durch eine mandantenbezogene Datenbankreferenz sowie den Bewegungsdigest geschützt. Bestandsbewegungen ohne diese optionale Referenz behalten ihren bisherigen Digestvertrag unverändert.

Ältere direkte Transfers ohne `transferSourceId` bleiben für diesen begrenzten Rückgabeweg verwendbar. Aus solchen Buchungen wird keine zuvor nicht erfasste mehrstufige Herkunft nachträglich abgeleitet. Eine explizite neue Kette beginnt mit einem Originalwareneingang.

### Mehrstufige Umbuchungen und Verzweigungen

Die optionale Eingabe `transferSourceId` verknüpft jeden neuen Transfer mit genau einem bereits gebuchten Quellbeleg. Als Quelle sind ein bestellungsgebundener `Receipt` oder ein `TransferIn` mit eigener vollständiger Herkunftskette zulässig. Material, Betrieb, Ausgangslager und Einstandswert müssen übereinstimmen. Eine Umlagerung darf ihrem Quellbeleg zeitlich nicht vorausgehen. Beide neuen Bewegungen speichern denselben Quellenbezug, schützen ihn im Digest und führen eine mandantenbezogene Datenbankreferenz zum Quellbeleg.

Die Prüfung folgt den Quellen zurück bis zum ursprünglichen Wareneingang. Fehlende/archivierte Belege, falsche Digests, unvollständige oder doppelte Transferpaare, geänderte Mengen/Werte, fremde Mandanten, Zyklen und mehr als 32 Lagerwechsel werden zurückgewiesen. Ein Lagerort darf später erneut besucht werden; ein bereits besuchter **Beleg** darf nicht erneut Teil derselben Kette sein.

Jeder Quellbeleg besitzt einen eigenen Zuordnungsrest:

`Belegrest = ursprüngliche positive Menge − direkte Folgeumbuchungen − unmittelbar von diesem Beleg gebuchte Lieferantenrückgaben`.

Eine Rückgabe über einen Nachfolger belastet dessen Rest. Sie wird beim Vorgänger nicht noch einmal abgezogen, weil dessen Menge schon beim Transfer zugeordnet wurde. Ältere Rückgaben über direkte, ungebundene Transfers werden am Originalwareneingang berücksichtigt. Zusätzlicher gleichartiger Lagerbestand erhöht keinen Belegrest.

Beispiel: Von 10 Einheiten in A werden 8 nach B, davon 6 nach C und davon 4 nach D gebucht. Nach einer Lieferantenrückgabe von 3 aus D sind die Belegreste A=2, B=2, C=2 und D=1. Ein weiterer Transfer aus B darf höchstens die dort verbliebenen 2 Einheiten dieses Belegs zuordnen. Eine Rückgabe aus D referenziert weiterhin den ursprünglichen Wareneingang und zusätzlich den letzten Umlagerungseingang. Konkurrierende Aufteilungen desselben Rests werden unter dem bestehenden Mandanten-Lock erneut geprüft; eine Ablehnung hinterlässt kein halbes Transferpaar.

Diese Funktion dokumentiert Mengen und Bewertung über Lagerwechsel. **Chargen-/Serienidentität**, FIFO/LIFO, gleitender Durchschnitt und die tatsächliche Identifikation physischer Einheiten sind damit nicht eingeführt. Verbrauchsbuchungen und Inventurdifferenzen behalten ihren bisherigen aggregierten Lagervertrag; sie erhalten keine erfundene Einzelherkunft.

## Rollen und Bedienung

Die Backend-Berechtigung für `stock-movements` und das Betriebsmodul `purchasing` werden **vor** jedem Schreibvorgang erneut geprüft. **Inventur, Umbuchung und Baustellenrückgabe** erfordern GF oder Bauleiter, **Lieferantenrückgabe** GF, Büro oder Bauleiter. Eine reine Buchhaltungssitzung darf diese manuellen Lagerbewegungen nicht buchen. Die Angular-Oberfläche zeigt nur zulässige Vorgangstypen; das ist kein Ersatz für die API-Rollenprüfung.

Die konkrete Lagerrolle wird nach dem Mandanten-Lock auch bei einem Idempotenz-Replay erneut geprüft. Eine frühere erfolgreiche Buchung erhält keine inzwischen entzogene Berechtigung.

Die Bedienung liefert keinen vorbefüllten Sollwert für tatsächlich gezählte Inventur oder retournierten Materialumfang. Die Anwender müssen Mengen, Referenzen, Standorte und belegende Informationen eingeben. Eine Buchung bestätigt keine physisch durchgeführte Inventur oder rechtliche Gewährleistungsabwicklung.

Bei einer Umbuchung kann der Anwender einen Herkunftsbeleg aus der aktuellen Journalseite ausdrücklich auswählen. Die Oberfläche zeigt dann Originalbeleg, bisherige Lagerwechsel und Belegrest; der Einstandswert wird aus dem ausgewählten Beleg übernommen. Bei Lieferantenrückgaben kann der letzte Transfer einer mehrstufigen Kette gewählt werden. Die Oberfläche prüft die Zuordnung zum ausgewählten Originalwareneingang und verwirft verspätete Herkunftsantworten nach Kontextwechsel. Fehlgeschlagene oder widersprüchliche Herkunftsantworten geben keine zugeordnete Buchung frei.

Bei Material- oder Lagerortwechsel verwirft die Oberfläche ungespeicherte Korrektureingaben und ausstehende Antworten des vorherigen Kontexts. Auch während einer laufenden Abfrage kann der neue Lagerort erneut geladen werden. Im unveränderten Kontext warten Materialsuche und erneuter Bestandsabruf auf den Abschluss einer laufenden Herkunftsprüfung; sie können deren Ladezustand nicht durch eine konkurrierende Antwort festhalten. Eine fachlich abgewiesene Buchung bleibt mit ihrem Fehlerhinweis und den Eingaben sichtbar, während Bestand, Journal und gegebenenfalls die Herkunftskette neu geladen werden.

## Zusammenführung der bisherigen Arbeitsstände

Die konkurrierenden Entwürfe aus PR #7 und PR #8 werden auf den Schnittstellen von PR #8 konsolidiert. Übernommen wurden die getrennte Inventurbelegreferenz, die bestätigte Transfer-Ausgangsmenge und die atomare Korrektur der Reservierungszähler. Rückgaben aus einem anderen Lager sind über das oben beschriebene direkte Transferpaar belegt. Die genauere Bindung jeder Lieferantenrückgabe an einen einzelnen Wareneingang, das paginierte Journal und der Aufmaß-PDF-Ablauf aus PR #8 bleiben erhalten. Es wird keine zweite Variante der Lagerendpunkte eingeführt. Der ältere alternative Branch ist dadurch technisch ersetzt; seine beliebige Lagerortangabe wird durch den geprüften direkten Transfer ersetzt.

## Regression und verbleibende Grenzen

Core-Tests decken Vorzeichen-/Grenzfälle, Reservierungen, überhöhte Doppelrückgaben, fremde Betriebe, Einkaufsstatus, Transferpaare und Digest-Unveränderlichkeit ab. [InventoryTransferChainTests](../tests/FirstBlick.Core.Tests/Business/InventoryTransferChainTests.cs) ergänzen mehrstufige Pfade, Verzweigungen, Quellenkapazität trotz zusätzlichem Bestand, historische direkte Rückgaben, Zyklen und die Tiefengrenze. [InventoryTransferChainApiTests](../tests/FirstBlick.Api.Tests/InventoryTransferChainApiTests.cs) prüfen tatsächliche HTTP-/Identity-/PostgreSQL-Transaktionen, Datenbankreferenzen, konkurrierende Aufteilung, Replay und entzogene Lagerrolle. [Browser-Fixtures](../web/tests/browser/inventory-control.spec.ts) prüfen Desktop/Tablet/Mobil mit kontrollierten API-Antworten einschließlich Auswahl, Rückgabe, Kontextwechsel, beschädigter Herkunftsantwort, konkurrierender Aktualisierung und Fehlererhalt nach abgewiesener Umbuchung; sie ersetzen keinen echten Mehrlagerbetrieb.

Die [Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) prüft auch importierte Transferpaare, Herkunftsketten und Rückgaben. Gebuchte Bewegungen dürfen weder nach dem Exportzeitpunkt liegen noch durch Archivierung aus dem aktiven Lagerjournal verschwinden. Aktive Bestellstatus müssen mit den Nettowareneingängen einschließlich Lieferantenrückgaben übereinstimmen. Der [Migrationsdrill](../tools/FirstBlick.Operations/MigrationDrill.cs) enthält eine verzweigte Herkunft mit zusätzlicher Inventurdifferenz, kontrolliert die getrennten Belegreste vor und nach Import und weist eine Überzuordnung trotz ausreichendem physischem Lagerbestand gezielt zurück.

**Noch keine vollständige fachliche/produktive Abnahme:** Physische Inventur mit realem Betrieb, Mehrlagerprozesse unter Last, Lieferantengutschriften/Warengutschriften, Kostenbewertung nach Chargen oder gleitendem Durchschnitt, Mehrwährungen, Retourenscheine, Schadens-/Transportschadenprozesse, von Dritten bestätigte Liefermengen sowie bidirektionale Lieferantenbestellschnittstellen. Eine Rückgabe ist eine interne Lagerbuchung und löst keine Zahlung, Gutschrift oder Lieferantenbenachrichtigung aus.

Diese Erweiterung verbessert die technischen Lücken von DP-07-03/DP-07-06; sie ist **keine** Behauptung, dass alle fachlichen Abnahmekriterien aller 49 vormals als `K` geführten Roadmap-Aufgaben erfüllt sind.
