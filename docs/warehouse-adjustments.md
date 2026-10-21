# Lagerbestandskorrekturen – Beleggebundene Buchungsketten

Stand: 21.10.2026 · FirstBlick · Roadmap **DP-07-03** und **DP-07-06** (technische Erweiterung).

## Verfahren

Das bestehende Wareneingangs-, Reservierungs- und Verbrauchssystem wird um vier **explizit ausgelöste** und revisionsfähige Buchungen erweitert. Kein Vorgang löscht oder aktualisiert eine schon gebuchte Materialbewegung. Sämtliche Kommandos laufen innerhalb derselben betriebsweiten PostgreSQL-Transaktion mit Mandanten-Lock, aktueller Rollen-/Modulprüfung, Idempotency-Key, Audit und Outbox.

| Fachvorgang | Schnittstelle | Vorbedingungen und Wirkung |
|---|---|---|
| Inventurdifferenz | `POST /api/v1/materials/{id}/inventory-count` | Erfasster Buchbestand `expectedOnHand` muss dem aktuell gebuchten Wert entsprechen. Der gezählte Bestand `countedOnHand` darf nicht negativ und nicht kleiner als die noch gebundene Reservierung sein. Der explizite Inventurbeleg `evidenceReference` und die Begründung sind Pflicht. Die Differenz wird als unveränderliche, **vorzeichenbehaftete** `Adjustment`-Bewegung gebucht; ihre Referenz enthält den Zählbeleg. |
| Lagerumbuchung | `POST /api/v1/materials/{id}/transfer` | Unterschiedliche Lagerorte und ausreichender **nicht reservierter** Ausgangsbestand. `expectedFromOnHand` muss mit dem aktuell gebuchten Quellbestand übereinstimmen, auch wenn die gewünschte Menge weiterhin verfügbar wäre. Genau zwei Bewegungen (negativ `TransferOut`, positiv `TransferIn`) mit gemeinsamer Referenz werden **atomar** geschrieben. |
| Lieferantenrückgabe | `POST /api/v1/stock-movements/{receiptId}/supplier-return` | Ursprüngliche gebuchte `Receipt`, eindeutige Bestellung/Position, bisher noch nicht zurückgegebene Originalmenge und freier Bestand am Empfangsort. Optional kann `returnTransferId` auf einen geprüften direkten `TransferIn` an einem anderen Lagerort verweisen (siehe unten). Negative `SupplierReturn`, Referenz ist weiterhin die ID des Originalwareneingangs. Bestellung wird anhand aller Zu-/Rückgänge auf `Ordered`, `PartiallyReceived` oder `Received` zurückgerechnet; Fehlmengen können erneut angeliefert werden. |
| Baustellenrückgabe | `POST /api/v1/stock-movements/{consumptionId}/project-return` | Ursprünglicher gebuchter und reservierungsbezogener Projektverbrauch. Positive `Return`, nur bis zur noch nicht retournierten Originalmenge, mit identischem Projekt, Material, Lagerort und Einstandswert. Die Reservierung vermindert `consumedQuantity` und erhöht `releasedQuantity` in derselben Transaktion; der noch reservierte Rest bleibt unverändert. Die Nachkalkulation behandelt solche Rückgaben als negative Materialkosten. |

Alle schreibenden Vorgänge benötigen eine Begründung beziehungsweise nachvollziehbare Belegreferenz. Vorgänge werden nicht aus einer bloßen Belegvorschau, aus CSV-Daten oder unbestätigten Bestellmengen automatisch erzeugt.

**Abruf:** `GET /api/v1/materials/{id}/movements?location=Hauptlager&page=1` liefert maximal 25 Bewegungen, gegen aktiven Mandanten und Leserechte geprüft, nach Erstellungszeit absteigend sortiert. Der optionale, serverseitig geprüfte Parameter `kind=TransferIn` liefert die paginierten Umbuchungsbelege des Materials über alle Lagerorte. Der Abruf ist für den Angular-Arbeitsplatz `/inventory-control` vorgesehen; vollständige Journalhistorien werden dort nicht in einem unbeschränkten Objekt geladen.

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

Damit ist ein direkter Lagerwechsel abgedeckt. Mehrstufige Umlagerungsketten sowie die Identifikation einzelner Chargen oder Seriennummern sind nicht enthalten. Bei gleichartigen Materialien ist dies eine belegte Mengen- und Bewertungskette; eine physische Zuordnung einzelner Einheiten zu einem Lieferlos wird nicht behauptet.

## Rollen und Bedienung

Die Backend-Berechtigung für `stock-movements` und das Betriebsmodul `purchasing` werden **vor** jedem Schreibvorgang erneut geprüft. **Inventur, Umbuchung und Baustellenrückgabe** erfordern GF oder Bauleiter, **Lieferantenrückgabe** GF, Büro oder Bauleiter. Eine reine Buchhaltungssitzung darf diese manuellen Lagerbewegungen nicht buchen. Die Angular-Oberfläche zeigt nur zulässige Vorgangstypen; das ist kein Ersatz für die API-Rollenprüfung.

Die konkrete Lagerrolle wird nach dem Mandanten-Lock auch bei einem Idempotenz-Replay erneut geprüft. Eine frühere erfolgreiche Buchung erhält keine inzwischen entzogene Berechtigung.

Die Bedienung liefert keinen vorbefüllten Sollwert für tatsächlich gezählte Inventur oder retournierten Materialumfang. Die Anwender müssen Mengen, Referenzen, Standorte und belegende Informationen eingeben. Eine Buchung bestätigt keine physisch durchgeführte Inventur oder rechtliche Gewährleistungsabwicklung.

Bei Material- oder Lagerortwechsel verwirft die Oberfläche ungespeicherte Korrektureingaben und ausstehende Antworten des vorherigen Kontexts. Auch während einer laufenden Abfrage kann der neue Lagerort erneut geladen werden. Eine fachlich abgewiesene Buchung bleibt mit ihrem Fehlerhinweis und den Eingaben sichtbar, während Bestand und Journal neu geladen werden.

## Zusammenführung der bisherigen Arbeitsstände

Die konkurrierenden Entwürfe aus PR #7 und PR #8 werden auf den Schnittstellen von PR #8 konsolidiert. Übernommen wurden die getrennte Inventurbelegreferenz, die bestätigte Transfer-Ausgangsmenge und die atomare Korrektur der Reservierungszähler. Rückgaben aus einem anderen Lager sind über das oben beschriebene direkte Transferpaar belegt. Die genauere Bindung jeder Lieferantenrückgabe an einen einzelnen Wareneingang, das paginierte Journal und der Aufmaß-PDF-Ablauf aus PR #8 bleiben erhalten. Es wird keine zweite Variante der Lagerendpunkte eingeführt. Der ältere alternative Branch ist dadurch technisch ersetzt; seine beliebige Lagerortangabe wird durch den geprüften direkten Transfer ersetzt.

## Regression und verbleibende Grenzen

Core-Tests decken Vorzeichen-/Grenzfälle, Reservierungen, überhöhte Doppelrückgaben, fremde Betriebe, Einkaufsstatus, Transferpaare und Digest-Unveränderlichkeit ab. Die API-/PostgreSQL-Tests überprüfen Transaktions- und Auditkette, Idempotenz, Rolle und betriebsübergreifende Sperre. Browser-Tests prüfen die durchgängige Bedienung und deaktivierte Aktionen bei ungeeigneter Rolle.

**Noch keine vollständige fachliche/produktive Abnahme:** Physische Inventur mit realem Betrieb, Mehrlagerprozesse unter Last, Lieferantengutschriften/Warengutschriften, Kostenbewertung nach Chargen oder gleitendem Durchschnitt, Mehrwährungen, Retourenscheine, Schadens-/Transportschadenprozesse, von Dritten bestätigte Liefermengen sowie bidirektionale Lieferantenbestellschnittstellen. Eine Rückgabe ist eine interne Lagerbuchung und löst keine Zahlung, Gutschrift oder Lieferantenbenachrichtigung aus.

Diese Erweiterung verbessert die technischen Lücken von DP-07-03/DP-07-06; sie ist **keine** Behauptung, dass alle fachlichen Abnahmekriterien aller 49 vormals als `K` geführten Roadmap-Aufgaben erfüllt sind.
