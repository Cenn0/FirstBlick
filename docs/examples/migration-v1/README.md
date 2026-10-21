# Ausführbares synthetisches Migrationsbundle v1

Dieses Verzeichnis enthält den vollständigen, unveränderten Source-Teil des erfolgreichen nativen Migrationsdrills vom 20. Oktober 2026. Es ist ausschließlich ein technisch prüfbares Beispiel mit erfundenen Betrieben, Personen, Belegen und Providerreferenzen. Die Adressen enden auf `example.invalid`. Keine der Dateien enthält reale Zugangsdaten, ein Passwort, einen Schlüsselring oder ein Zertifikat.

- [migration.json](migration.json) enthält den vollständigen Vertrag `firstblick.migration.v1` mit zwei Betrieben, zwei persönlichen Quellidentitäten, drei Mitgliedschaften, 19 Fachdatenobjekten, zwei Dokumenten und einem historischen Versandbeleg.
- [evidence/source-records.txt](evidence/source-records.txt) ist der ausdrücklich synthetische Originalnachweis des Beispiels.
- `objects/` enthält genau die zwei im Manifest gehashten Originaldateien, darunter ein archiviertes Dokument. Es handelt sich um kleine Textdateien; daraus wird kein erfolgter PDF- oder realer R2-Export abgeleitet.
- [Der unveränderte Maschinenbericht](../../evidence/migration-drill.json) dokumentiert den tatsächlich ausgeführten Import in echte isolierte PostgreSQL-/S3-Testziele und deren anschließende Entfernung.

SHA-256 der unveränderten `migration.json`-Bytes:

```text
3271d42a56e77d2cae2ffa9c4911a9fda1e3b8311a5af9b8de6e60409c59bbba
```

Im Repository kann das Bundle ohne Datenbank-, Speicher- oder Kontokonfiguration geprüft werden:

```bash
./scripts/operations.sh migration-check \
  --input docs/examples/migration-v1 \
  --report backups/example-migration-check.json
```

Das erwartete Ergebnis ist `Compatible` und Exitcode `0`. Bei einer Wiederholung wird ein neuer Berichtsname verwendet. `targetRelease` ist absichtlich der eindeutige synthetische Drill-Stand. Für einen realen Quelladapter werden eigene kontrollierte Quelldaten, stabile Identitätszuordnungen, bestätigte Originalbelege und unabhängig erhobene Kontrollsummen benötigt. Das Ändern von Beispieldaten oder ihren Hashes beweist keinen entsprechenden historischen Geschäftsvorfall.

Die historische Käuferbezeichnung im akzeptierten Angebot unterscheidet sich bewusst vom später geänderten Kundenstamm. Die Rechnung über netto 1.000 und Steuer 190 enthält eine ursprüngliche Bruttosumme von 1.190; dazu sind eine Teilkorrektur über 238, eine Zahlung über 500, ein Skonto über 10 und ein Einbehalt über 100 erhalten. Lieferantenrechnung, Zahlung und Teilerstattung teilen die Bankkapazitätsprüfung mit Kundenbuchungen. Diese Beträge dienen ausschließlich der ausführbaren Probe; ein rechtliches Rechnungsprofil wird dadurch nicht behauptet.

Vertrag, Grenzen, kanonische Hashregeln, isolierter Import, exakter Vergleich, wiederholbarer Lauf und spätere ausdrückliche Freigabe sind in [migration.md](../../migration.md) beschrieben.
