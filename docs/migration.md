# Kanonische Altbestandsübernahme

FirstBlick bietet einen versionierten Austauschvertrag, eine rein lesende Vorprüfung, einen Import in ein neues gesperrtes Ziel und einen anschließenden exakten Vergleich. Der Vertrag heißt **`firstblick.migration.v1`**. Seine ausführbare Definition liegt in [MigrationContract.cs](../tools/FirstBlick.Operations/MigrationContract.cs); der vollständige synthetische Beispielexport liegt unter [examples/migration-v1](examples/migration-v1/README.md).

Der bisherige D1-/R2-Originalexport, dessen vollständiges Schema und die tatsächlichen Eigentums- und Freigabenachweise liegen diesem Projekt nicht vor. Daher gibt es keinen behaupteten direkten Zugriff auf das alte System und keinen als erfolgreich ausgegebenen echten Kundenumzug. Ein geprüfter Quelladapter muss den hier beschriebenen Vertrag aus den tatsächlichen Quelldaten herstellen. Eine generische CSV-Datei ist kein Ersatz für Vertrags-, Buchungs-, Versand- und Identitätshistorie. Die normalen [Discovery-/CSV-Kopien](discovery-data-copy.md) haben einen anderen Umfang.

## Nachweis und ausführbare Befehle

Der unveränderte Maschinenbericht [evidence/migration-drill.json](evidence/migration-drill.json) stammt aus einem synthetischen Export mit zwei Betrieben, zwei alten Identitäten, 19 Fachdatenobjekten, zwei originalen Dokumentdateien und einem historischen Versandbeleg. Der Test verwendet eine echte isolierte PostgreSQL-Datenbank und einen realen versionierten S3-Bucket. Er prüft unter anderem eingefrorene Preise und Vertragspartner trotz geänderter Stammdaten, Teilzahlung/Skonto/Einbehalt/Korrektur, die gemeinsame Bankkapazität von Kunden- und Lieferantenzuordnungen, Objektbytes, Nummernkreise und Wiederholungen ohne zusätzliche Datenbank- oder S3-Schreibvorgänge.

Der native Bericht nennt seine tatsächliche PostgreSQL-Version. Der [Operations-Workflow](../.github/workflows/operations.yml) führt denselben Migrationsdrill im tatsächlichen Containerstack mit PostgreSQL 18.6 und SeaweedFS 4.48 aus und lädt die synthetischen Berichte als `operations-evidence` hoch. Erst ein erfolgreicher Lauf, der diesen neuen Schritt enthält, belegt auch den Container-Migrationsstand. Der bereits bestandene [Container-Restore-Lauf 39378228901](https://github.com/Cenn0/FirstBlick/actions/runs/39378228901) enthält die separat dokumentierte Backup-/Wiederherstellungsprüfung; er wird nicht als Nachweis eines damals noch nicht enthaltenen Migrationsschritts verwendet.

Alle Befehle gehören zum Operations-CLI:

| Befehl | Ergebnis und Wirkung |
|---|---|
| `migration-check --input VERZEICHNIS --report DATEI` | Liest Bundle und referenzierte Dateien; schreibt nur den neuen Prüfbericht. Benötigt keine Datenbank, keinen Speicherzugang und kein Konto. |
| `migration-import --input VERZEICHNIS --database NAME --bucket NAME --report DATEI` | Prüft erneut, erzeugt ausschließlich neue isolierte Ziele, importiert in einen persistenten Recovery-Hold und vergleicht Daten und Objektbytes. |
| `migration-compare --input VERZEICHNIS --database NAME --bucket NAME --report DATEI` | Liest eine konsistente Datenbanksicht und exakt referenzierte Objektversionen; schreibt nur einen neuen Vergleichsbericht. |
| `migration-drill --output VERZEICHNIS` | Erzeugt synthetische Quelldaten und neue zufällige Testziele, prüft positive und negative Fälle und entfernt nach Erfolg ausschließlich diese eigenen Testziele. |

Exitcode `0` bedeutet abgeschlossene Prüfung bzw. abgeschlossenen Befehl. `migration-check` liefert bei `Blocked` und `migration-compare` bei `Mismatch` den Exitcode `2`. Ein fehlgeschlagener technischer Vorgang liefert `1` und behauptet keinen Abschluss. Berichtsdateien werden nicht überschrieben. Für jeden neuen Lauf wird ein neuer Dateiname verwendet.

Mit dem vorhandenen .NET SDK lässt sich das Beispiel ohne jede Datenbankkonfiguration prüfen:

```bash
./scripts/operations.sh migration-check \
  --input docs/examples/migration-v1 \
  --report backups/example-migration-check.json
```

Für ein tatsächlich geprüftes Bundle im lokalen Compose entspricht `/backups` dem privaten Hostverzeichnis `backups/`. Die Source-Dateien müssen dort vorliegen:

```bash
FIRSTBLICK_OPS_CONTAINER=true ./scripts/operations.sh migration-check \
  --input /backups/reviewed-source \
  --report /backups/source-check.json

FIRSTBLICK_OPS_CONTAINER=true ./scripts/operations.sh migration-import \
  --input /backups/reviewed-source \
  --database firstblick_restore_migration_20261020 \
  --bucket firstblick-restore-migration-20261020 \
  --report /backups/import-report.json

FIRSTBLICK_OPS_CONTAINER=true ./scripts/operations.sh migration-compare \
  --input /backups/reviewed-source \
  --database firstblick_restore_migration_20261020 \
  --bucket firstblick-restore-migration-20261020 \
  --report /backups/compare-report.json
```

Die nötigen Datenbank-, Speicher-, Zertifikats- und Operator-Einstellungen stehen in [operations.md](operations.md). `FIRSTBLICK_DOTNET` kann den Pfad zu einem vorhandenen SDK setzen. Außerhalb von `Development` und `Testing` muss `FIRSTBLICK_RELEASE` dem `targetRelease` des geprüften Bundles exakt entsprechen. Das neue Ziel verwendet sein bewusst konfiguriertes RSA-Zertifikat; alte Schlüsselringe, Passwörter und Zugangstokens werden bei der kanonischen Migration nicht übernommen.

## Inhalt des Bundles

Ein Bundle besteht aus `migration.json` und den darin relativ referenzierten Dateien. Alle Eigenschaften des Vertrags werden ausdrücklich geliefert, einschließlich leerer Listen und zulässiger `null`-Werte. Die Anwendung ergänzt keine fehlenden Quell-IDs oder historischen Standardwerte. Das Manifest ist höchstens 100 MiB groß; die JSON-Verschachtelung ist auf 64 Ebenen beschränkt. Das Importwerkzeug versteht ausschließlich registrierte typisierte Fachdatenobjekte.

| Bereich in `migration.json` | Verbindlicher Inhalt |
|---|---|
| Kopf | `format`, neue eindeutige `bundleId`, benanntes `sourceSystem`, `sourceVersion`, exakt vorgesehenes `targetRelease`, UTC-`exportedAt`. |
| `tenants` | Ursprüngliche `sourceId` und vollständiger `BusinessTenant`: unveränderte UUID, Name, bekannte Module, Zeitzone, Freigaberichtlinie und Richtlinienrevision. |
| `accounts` | `sourceId`, unveränderte bzw. ausdrücklich zugeordnete UUID, E-Mail, Anzeigename und optionale Telefonnummer. Es gibt keine importierbaren Passwort-/Tokenfelder. |
| `memberships` | Betrieb, Benutzer, bekannte `BusinessRole`, bisheriger Aktivstatus und Revision. Historische inaktive Mitgliedschaften sind ausdrücklich zulässig. |
| `entities` | Quell-ID, Registry-`kind`, vollständige typisierte `data`, deren kanonischer SHA-256, Evidenzzustände `contractParties`/`deliveryHistory` und gegebenenfalls `evidenceFile`. |
| `documents` | Vollständige historische `DocumentObject`-Metadaten, lokaler Dateinachweis, ursprünglicher Speichername sowie originale Versionskennung oder ausdrücklich `null`. |
| `counters` | Betrieb, Nummernkreis, Jahr, zuletzt belegter Zählerstand und unabhängig erwartete nächste Nummer. |
| `history` | Originale Ereignis-ID, Betrieb, Kategorie, optionale Fachdatenreferenz, UTC-Zeit, unveränderte JSON-Belegdaten, kanonischer SHA und Originaldateiverweis. Kategorien: `audit`, `dispatch`, `contract`, `numbering`, `account`. |
| `evidenceFiles` | Relative Originalnachweisdateien mit exakter Byteanzahl und kleingeschriebenem SHA-256. |
| `uncertainties` | Bekannte ungeklärte Sachverhalte mit Code, Beschreibung und optionalem Betrieb-/Objekt-/Dateibezug. Jeder Eintrag blockiert den Import. |
| `controls` | Ein eigener Quellkontrollsatz pro Betrieb: Anzahlen, Digests sowie kaufmännische Kontrollsummen. |

Der Vertrag erlaubt maximal 1.000 Betriebe und jeweils 100.000 Konten, Mitgliedschaften, Entitäten, Dokumente, Nummernkreise und historische Datensätze. Ein Fachdatenobjekt darf höchstens 1.900.000 UTF-8-Bytes und ein historischer JSON-Beleg 100.000 Bytes enthalten. Dokumentdateien sind auf 25 MiB, die höchstens 10.000 zusätzlichen Nachweisdateien jeweils auf 512 MiB begrenzt. Relative Pfade dürfen weder aus dem Bundle ausbrechen noch symbolische Links durchlaufen. Das Werkzeug verweigert fehlende Dateien oder abweichende Bytes vor der Zielanlage.

Die zusätzlichen `evidenceFiles` sind geprüfte Originalbelege des **extern aufzubewahrenden Quellbundles**. Der Import speichert ihre Dateinamen, Größen und Hashes bei der jeweiligen Fachdatenherkunft; er veröffentlicht diese potenziell vertraulichen Exportbelege nicht als allgemein lesbare Anwendungsdokumente. Nur die ausdrücklich unter `documents` aufgeführten Dateien werden als Anwendungsdokumente in den Ziel-Bucket übernommen. Das vollständige Quellbundle einschließlich Adapterbericht und Originalnachweisen bleibt deshalb zusätzlich im kontrollierten, verschlüsselten Migrationsarchiv des Betreibers erhalten. Eine spätere Anwendungssicherung enthält die Herkunftsbelege in der Datenbank und die importierten Anwendungsdokumente, ersetzt dieses Quellarchiv jedoch nicht.

### Identitäten und Relationen

Eine ursprüngliche UUID bleibt exakt erhalten. Für eine alte nicht als UUID darstellbare Kennung muss der Quelladapter eine feste UUID-Zuordnung liefern. Das Werkzeug erzeugt dafür keine wechselnden Zufallswerte und dedupliziert keine Personen anhand ihres Namens. Dieselbe normalisierte E-Mail darf nicht auf mehrere Benutzer-UUIDs zeigen. Bei mehreren Betrieben wird eine gemeinsame persönliche Identität mit getrennten Mitgliedschaften abgebildet.

Alle Fachdatenreferenzen werden aus `TenantEntity.References()` abgeleitet und unter Betrieb, Art und UUID geprüft. Historische Verweise auf archivierte Datensätze bleiben erlaubt. Kunden-/Gebäude-/Projektbezüge, Auftrag-/Angebotssnapshots, Rechnungskorrekturen, Lieferantenbestellungen, Materialbewegungen, Formversionen und technische Grundlagen werden zusätzlich fachlich geprüft. Historische Akteure müssen eine mitgelieferte Mitgliedschaft des jeweiligen Betriebs besitzen. Die referenziellen Datenbankkanten verwenden dieselbe mandantengebundene Projektion wie die Anwendung.

Mengenübernahmen behalten Messungs-ID, Messungsdigest, Zeilen-ID, Basismenge und Einheit, Umrechnungsfaktor, Verschnitt, Regelquelle, Methodenversion und resultierende Menge. Angebot und Nachtrag müssen den vollen übernommenen Mengenwert behalten; eine Rechnung darf die vom Rechnungskontrakt ausdrücklich zugelassene Teilmenge mit unveränderter vollständiger Mengenevidenz enthalten. Ein umgewandelter Materialbedarf verweist auf genau seine Bestell- und Zeilen-ID; die Bestellzeile verweist zurück auf denselben Bedarf und dieselbe Mengenevidenz.

### Preise, Vertragspartner und Versand

Die importierten Daten werden mit den vorhandenen Versionen der Domain-Verträge validiert. Unterstützte Berechnungen dürfen dabei zur **Gleichheitsprüfung** erneut ausgeführt werden. Ihre Ergebnisse ersetzen keine historischen Zeilen, Rundungen, Summen, Methodenkennungen oder Digests. Ein früherer, abweichender Rechenvertrag muss zuerst ausdrücklich unterstützt und separat geprüft werden; das Umbenennen in die aktuelle Methodenversion wäre keine zulässige Migration.

Ausgegebene Angebote benötigen ihre historische `QuotePresentationSnapshot` mit Käufername und Käuferadresse. Ein Auftrag muss exakt den angenommenen Angebots-, Preis- und Darstellungsstand behalten. Rechnungs- und Lieferantenkorrekturen bleiben an die ursprünglichen Belege gebunden. Auch bestellte Einkaufsbelege verlangen bestätigte historische Vertragspartner und eine Originaldatei. Da der aktuelle `PurchaseOrder` keinen separaten eingefrorenen Lieferantennamen besitzt, ist dessen damalige Parteidarstellung im Originalbeleg des erhaltenen Quellbundles nachzuweisen; der Import rekonstruiert sie nicht aus heutigen Lieferantenstammdaten.

`contractParties` und `deliveryHistory` verwenden die Werte `NotApplicable`, `Confirmed` und `Unknown`. `Unknown` blockiert auch dann, wenn ein technischer Hash korrekt ist. Für historische Verträge reicht `NotApplicable` nicht aus. Ein bloßer Text `Confirmed` beweist die inhaltliche Richtigkeit des Altbelegs nicht; hierfür bleibt der geprüfte Quelladapter samt verantwortlicher Abnahme maßgeblich.

Ein historischer Angebotsversand darf nur als abschließend `Sent` oder `Failed` übernommen werden. `Sent` verlangt gespeicherten Abschlusszeitpunkt und ursprüngliche Providerreferenz. `Pending`, `Processing` und `Uncertain` blockieren, bis die tatsächliche Versandlage vor dem Export geklärt und dokumentiert wurde. Die Quelle darf diese Unsicherheit ausdrücklich in `uncertainties` erhalten. Das Werkzeug macht daraus keinen vermeintlichen Erfolg und legt bei keiner historischen Versandart einen neuen Outbox-Job an. Der Vertragszustand `Accepted` bleibt unabhängig vom Versandzustand erhalten.

### Prüfsummen und Quellkontrollen

Zwei Hasharten erfüllen unterschiedliche Aufgaben:

1. `bundleSha256` ist SHA-256 über die **unveränderten Datei-Bytes** von `migration.json`. Bereits eine Änderung der Einrückung ergibt ein anderes Bundle und verhindert die Wiederverwendung seines Importbelegs.
2. Entitäten und historische JSON-Belege verwenden SHA-256 über das kanonische JSON des Vertrags v1. Objekteigenschaften werden ordinal sortiert; die Reihenfolge von Arrays bleibt erhalten. Doppelte Objekteigenschaften werden abgewiesen. Die UTF-8-Ausgabe verwendet die Serialisierung von `MigrationJson.Canonical` ohne zusätzliche Einrückung.

Die numerische Normalisierung arbeitet mit dezimalen Ziffern und Exponenten, nicht mit binärem Gleitkomma: `100.000` und `1e2` haben denselben numerischen Inhalt; `9007199254740993` und `9007199254740992` bleiben verschieden; `1e-100` wird nicht zu null. Der normalisierte Dezimalexponent ist auf −10.000 bis +10.000 begrenzt. Dieser ausdrücklich versionierte Algorithmus ist der Projektvertrag und wird nicht als fremder JSON-Kanonisierungsstandard ausgegeben.

Fachliche Dezimalwerte werden im vollständigen typisierten Fachdaten-JSON als Strings ausgegeben, beispielsweise `"1190"`; Enums verwenden ihre unveränderten Vertragsnamen, beispielsweise `Accepted`. Die Vorlage ist die tatsächliche serialisierte Definition. Unbekannte Eigenschaften, numerische Enum-Abkürzungen, implizit ergänzte Standardwerte oder ein nach der typisierten Deserialisierung abweichender Hash blockieren. Historische Fachdaten-Envelope-Zeiten und Dokumenterstellungszeiten müssen bereits zur Mikrosekundenauflösung von PostgreSQL passen; der Import kürzt keine historischen Zeitwerte stillschweigend.

Eine eng begrenzte v1-Kompatibilitätsregel betrifft Lieferantenrechnungen ohne elektronischen Herkunftsbeleg: Alte Exporte dürfen die später ergänzte Eigenschaft `electronicSource` auslassen; neuere Exporte dürfen sie ausdrücklich als `null` enthalten. Prüfung, Import und Vergleich erhalten jeweils die ursprüngliche Form samt Einzel-SHA-256 und Herkunftsbeleg. Diese Regel erlaubt keine anderen ausgelassenen oder unbekannten Eigenschaften und entfernt niemals vorhandene elektronische Nachweise. Ein nachträglicher Wechsel zwischen fehlender Eigenschaft und `null` im Importziel gilt weiterhin als Abweichung.

Ein Quellkontrollsatz enthält:

- Anzahlen der Entitäten, Dokumente und Mitgliedschaften je Betrieb.
- `entityDigest` über nach Art und UUID geordnete Objektkennungen und Einzel-Digests; getrennte Digests für Dokumentmetadaten, historische Ereignisse, Konten/Mitgliedschaften und Nummernkreise.
- Summen von ausgegebenen Angeboten, Aufträgen, Rechnungsnetto/-steuer/-brutto, Gutschriften, aktiven Zahlungseingängen/-erstattungen, Skonto, verbleibendem Einbehalt, signiertem Bankbestand sowie genehmigten Lieferantenrechnungen/-gutschriften und deren Zahlungen/Erstattungen.

Die exakte Auswahl und Sortierung ist in `MigrationControls.Calculate` definiert. Stornierte Zahlungszuordnungen gehen nicht erneut als aktive Zahlung ein; ein verbleibender Einbehalt berücksichtigt seine dokumentierten Freigaben. Die gemeinsame Bankkapazität betrachtet Kunden- und Lieferantenzahlungen sowie Rückläufer gemeinsam. Der Quelladapter muss die Kontrollwerte unabhängig aus der Quelle ermitteln und abgleichen. Das synthetische Beispiel erzeugt erwartete Werte programmgesteuert; es beweist keine Vollständigkeit einer nicht bereitgestellten echten Quelle. Ein plausibler Hash beweist ebenso wenig den Eigentümer eines Exports. Nur kontrolliert übergebene Quellbundles sind für einen tatsächlichen Import vorgesehen.

### Nummernkreise

Die Kreise `AN`, `AU`, `RE`, `KO` und `BE` werden mit Jahr, letztem vergebenen Wert und erwarteter Folgennummer geliefert. Für das betriebliche Jahr des Exportzeitpunkts müssen alle fünf Kreise vorhanden sein, auch unbenutzte Kreise mit Wert `0`. Das Jahr wird in der gespeicherten Betriebszeitzone bestimmt.

Bereits vergebene Nummern bleiben erhalten. Bei Nummern des Anwendungsschemas `ART-JAHR-ZAHL` muss der Zähler mindestens den höchsten vergebenen Wert abdecken. Sonderformate alter Belege bleiben unverändert, verlangen aber weiterhin explizite zukünftige Nummernkreise. Die erwartete Folgennummer darf mit keinem vorhandenen Beleg kollidieren. Der Drill ruft nach dem Import den tatsächlichen `EntityStore.NextNumber` unter Mandantensperre auf und prüft die Folgennummer; die Probe wird zurückgerollt.

## Import, Quarantäne und Vergleich

`migration-import` führt die vollständige Vorprüfung erneut aus. Zielnamen sind auf neue `firstblick_restore_*`-Datenbanken und `firstblick-restore-*`-Buckets begrenzt. Der Befehl schreibt weder in die Quelle noch in eine vorhandene beliebige Datenbank. Er erzeugt das Schema mit den versionierten EF-Migrationen und schreibt die Fachdaten zusammen mit einem persistenten `operations.recovery-hold` pro Betrieb.

Alte Identitäten behalten UUID, E-Mail, Namen und historische Mitgliedschaften. Ihre Passwörter sind `null`, E-Mail-/Telefonbestätigungen sind zurückgesetzt und ihre Mitgliedschaften sind inaktiv. Der `migration.account:{userId:N}`-Beleg hält ursprüngliche Quell-ID, Rolle und Aktivstatus unter dem exakten Bundle-Hash fest. Alte persönliche Passwörter, externe Login-Verknüpfungen, Identity-Claims/-Rollen, Tokens, Einladungen, Freigaben und Outbox-Aufträge werden nicht als aktive Zugangsmittel erzeugt.

Ein ausdrücklich konfiguriertes **neues persönliches Operator-Konto** erhält eine neue UUID, ein neues Passwort und die Geschäftsführung der importierten Betriebe. Seine Adresse muss sich von sämtlichen Quellkonten unterscheiden. Auch dieses Konto unterliegt der Wiederanlaufsperre. So ist die spätere Freigabe möglich, ohne sich auf alte Rollen oder Passwörter zu verlassen.

Alle noch nicht widerrufenen alten Portal-Freigaben werden beim Import mit Zeit und Revision widerrufen. Ihre ursprünglichen JSON-Werte bleiben zusätzlich im Herkunftsbeleg erhalten. Die exakte Quellkennung und ihr Hash werden unabhängig von dieser ausdrücklich erlaubten Sicherheitsänderung aufbewahrt. Bei einem späteren Restore bleiben auch bereits importierte, noch nicht übernommene Quellkonto-IDs und deren ursprüngliche Rollen-/Aktivitätsherkunft erhalten; der vorherige Kontoherkunftsbeleg wird gesondert archiviert.

Anwendungsdokumente werden aus den bereits geprüften Originalbytes in den neuen versionierten Bucket übertragen und anschließend über die **neue genaue `VersionId`** zurückgelesen. Größe und SHA-256 müssen erneut stimmen. Ursprünglicher R2-/S3-Schlüssel, ursprüngliche Versionskennung, neues Objektziel und neue Version stehen im Herkunftsbeleg und im Bericht. Dokument-IDs, fachliche Quell-Digests, historische Zeitwerte und Revisionen bleiben dabei erhalten. Die unterschiedlichen Versionen sind eine erforderliche Zuordnung zwischen Speichern; die Dateibytes bleiben identisch.

Vor dem Abschluss prüft der Importer seine Daten mit demselben Vergleich wie `migration-compare`. Dieser verwendet eine lesende PostgreSQL-Repeatable-Read-Transaktion und prüft unter anderem typisierte Payloads, Envelope-Revisionen, Datenbankprojektionen, mandantengebundene Referenzen, Nummernkreise, Herkunftsbelege, historische JSON-Ereignisse, Zugangssperren, Operator, Dokumentmetadaten, genaue S3-Versionen und sämtliche Kontrollsummen. Nur die dokumentierten Portalwiderrufe, gesperrten alten Zugangsmittel, neue Operator-Identität sowie neue Speicherkennungen sind erlaubte Abweichungen. Ein JSONB-bedingter Wechsel der Objekteigenschaftsreihenfolge ändert keine historischen Werte; Suchprojektionen werden dennoch mit derselben typisierten Serialisierung wie beim normalen Anwendungsschreiben verglichen.

Erst nach erfolgreichem Vergleich setzt der Importer seinen Beleg auf `Prepared` und schreibt `operations.recovery-prepared`. Sein Bericht lautet **`PreparedHeld`**. Das bedeutet geprüfte Daten bei weiterhin gesperrtem Betrieb. Es ist keine Produktionsfreigabe.

Eine Wiederholung desselben unveränderten Bundles gegen genau sein unverändertes vorbereitetes, noch gesperrtes Ziel vergleicht nur und antwortet `repeatedWithoutWrites: true`. Sie erzeugt keine neuen Datenbankzeilen, Dokumentversionen, Nummern oder Jobs. Ein fehlgeschlagener Teilimport, ein fremdes Ziel, andere Quelldatei-Bytes oder veränderte Zieldaten werden nicht überschrieben. Ein neuer Versuch verwendet neue Zielnamen. Nach einer tatsächlichen Freigabe und fachlichen Weiterarbeit ist das Ziel bewusst kein unveränderter Importvergleich mehr.

## Freigabe und Rückfall

Das vollständige Quellbundle und die Berichte bleiben erhalten. API und insbesondere Worker werden nicht durch einen Importbefehl auf das neue Ziel umgeschaltet. Bei Fehlern bleiben bereits erzeugte Daten in der isolierten, gesperrten Umgebung zur Untersuchung bestehen. Der Befehl löscht sie nicht durch einen vermeintlichen automatischen Rückfall.

Für den tatsächlichen Umzug werden zuerst die realen Veränderungen nach dem Export geklärt: ausgegebene Nummern, neue oder zurückgenommene Rechte, Passwortrücksetzungen, Zahlungen, externe Sendungen, Dokumentstände und offene Offline-Änderungen. Eine neue finale Quellextraktion oder eine ausdrücklich geprüfte Ergänzungsregel muss diese Änderungen abdecken. Unbekannte Folgewirkungen sind keine automatisch errechenbare Differenz.

Die benannte verantwortliche Person füllt pro Betrieb den in [operations.md](operations.md#die-sperre-nach-konkreter-abstimmung-freigeben) beschriebenen Nachweis für sechs Abstimmungsbereiche aus. `resume` verlangt exakt passende Betrieb-/Bundle-Operations-ID, Exportzeit, Release und gehashte Nachweisdateien. Der Import muss vollständig vorbereitet sein. Eine Freigabe ohne abgeschlossene Daten-/Objektprüfung wird zurückgewiesen.

Erst nach dem bewussten `resume` lädt die neue Geschäftsführung die alten Personen gezielt neu ein. Das erste gültige neue Einladungstoken aktiviert die passende persönliche Identität mit einem neuen Passwort. Eine Identität, die bereits in einem anderen Betrieb aktiv ist, muss sich zuvor anmelden; ihr Passwort wird nicht global ersetzt. Historische Rollen werden als Herkunft erhalten, aber nicht ohne bewusste Einladung wirksam. Portalzugänge werden bei Bedarf gezielt neu erteilt.

Der anschließende Wechsel übernimmt Datenbank, Bucket, Verschlüsselungszertifikat und Release gemeinsam. Geräte laden aktuelle Mandanten- und Revisionsdaten neu. Der Worker wird zuletzt gestartet. Der ursprüngliche Export und die alte Umgebung bleiben während des vereinbarten Rückfallfensters verfügbar. Ein Rückfall nach neuen Zielbuchungen muss diese Buchungen, externen Nummern und Sendungen ausdrücklich berücksichtigen. Er ist kein blindes Zurücksetzen auf einen älteren Zähler- oder Berechtigungsstand.

## Drill wiederholen

```bash
FIRSTBLICK_OPS_CONTAINER=true ./scripts/operations.sh migration-drill \
  --output /backups/migration-drill-2026-10-20
```

Der Drill erzeugt eigene zufällige Datenbank-/Bucketnamen; er verwendet nicht die Daten der konfigurierten Live-Anwendung als Testmaterial. Neben dem erfolgreichen Import prüft er manipulierte eingefrorene Preise, fehlende historische Parteien, ungewissen Versand, fehlende Mandantenreferenzen, unbekannte Methodenversionen, ungültige Geschäftszeitzonen, gemeinsame Banküberbelegung, Nummernkollisionen, ausdrücklich ungeklärte Quelle, übergroße JSON-Exponenten und korrupte Dokumentbytes vor Zielanlage. Ein geänderter Zielzähler muss danach als `Mismatch` erscheinen. Der genaue ausgeführte Stand, alle gemessenen Zahlen, Objektzuordnungen und die erfolgreiche Entfernung ausschließlich der eigenen Testziele stehen im Maschinenbericht.

Für eine reale Abnahme fehlen weiterhin der vollständige tatsächliche Export, die unabhängig erhobenen Kontrollwerte, reale Originaldokumente und Versandnachweise, die geprüfte Zuordnung des alten Schemas, repräsentatives Volumen und die organisatorische Freigabe des konkreten Umzugs. Der implementierte Vertrag macht diese Punkte prüfbar; er behauptet sie nicht stellvertretend als erledigt.
