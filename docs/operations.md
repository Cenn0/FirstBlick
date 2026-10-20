# Betrieb, Sicherung und Wiederherstellung

FirstBlick besteht aus der Angular-Oberfläche im API-Container, PostgreSQL, einem versionierten S3-Objektspeicher und einem separaten Outbox-Worker. `compose.yml` startet diese Komponenten lokal. Eine Wiederherstellung erfolgt grundsätzlich in einer neuen Datenbank und einem neuen Bucket. Das Werkzeug verändert keine vorhandene Zieldatenbank und schaltet kein Produktivsystem um.

Die Befehle und Garantien in diesem Dokument beziehen sich auf `tools/FirstBlick.Operations`, `deploy/Dockerfile.*`, `compose.yml` und die Skripte in `scripts/`. Die fachlichen Versandzustände und manuellen Klärungen sind in [jobs.md](jobs.md) beschrieben.

## Versionen und belegter Stand

| Komponente | Festgelegter Stand | Nachweis |
|---|---|---|
| .NET SDK | 10.0.402, Ubuntu 24.04 | `global.json`, unveränderlicher Registry-Digest im Dockerfile |
| ASP.NET Core Runtime | 10.0.13, Ubuntu 24.04 | Unveränderlicher Registry-Digest im Dockerfile |
| Node.js | 24.19.0, Debian Bookworm | Unveränderlicher Registry-Digest im Dockerfile; npm-Lockdatei |
| PostgreSQL im lokalen Compose | 18.6, Debian Bookworm | Offizielles Image mit Digest; `pg_dump`/`pg_restore` 18.6 im Operations-Image |
| S3 im lokalen Compose | SeaweedFS 4.48 | Hersteller-Image mit Digest; Versionierung wird vor dem API-Start ausdrücklich aktiviert |
| PDF-Schriften | DejaVu Sans, `fonts-dejavu-core=2.37-8` | Paket im API-Image; reguläre und fette TTF-Dateien |

[images.lock.json](../deploy/images.lock.json) enthält die geprüften Registry-Adressen und SHA-256-Digests sowie den Hash der für den nativen Test verwendeten SeaweedFS-Release-Datei. Die Images werden nicht durch einen beweglichen `latest`-Tag ausgewählt. Paket-Lockdateien werden beim Build im gesperrten Modus geprüft. Die Schrift wird in einer festgelegten Paketversion installiert; das Systempaketarchiv muss diese Version bereitstellen. Ein fehlendes Paket führt zu einem fehlgeschlagenen Build und keinem stillen Versionswechsel.

Der reale, kleine Wiederherstellungstest wurde mit PostgreSQL **16.15** und SeaweedFS **4.48** ausgeführt. Der unveränderte Maschinenbericht liegt in [evidence/restore-drill.json](evidence/restore-drill.json). Er belegt 20 Tabellen, 31 Zeilen und zwei referenzierte S3-Versionen einschließlich eines historischen, später überschriebenen Objekts und eines archivierten Dokuments. Der Bericht enthält die gemessene Dauer dieses synthetischen Durchlaufs; daraus folgt keine zugesicherte Produktions-RTO. PostgreSQL 18.6 ist der festgelegte Compose-Stand und wird getrennt vom nativen 16.15-Nachweis betrachtet.

[evidence/restore-drill-integrity.json](evidence/restore-drill-integrity.json) enthält dazu die synthetischen Tabellen-/Datei-Hashes und alten/neuen Objektkennungen. Lokale Shell-Prüfung, Compose-Konfigurationsprüfung und das Erzeugen der privaten Konfiguration wurden ausgeführt. Die Entwicklungsumgebung erlaubt keine vollständige UID-Zuordnung für einen isolierten Docker-Daemon (`newuidmap` weist den Bereich zurück); ein lokaler Containerbuild wird deshalb nicht als bestanden behauptet. Der separate [Operations-Workflow](../.github/workflows/operations.yml) baut die tatsächlichen Images auf GitHub, startet Compose, prüft Anmeldung/CRUD, führt den PostgreSQL-18.6-/S3-Restore-Drill sowie eine Sicherung des laufenden Stands und einen Neustart aus. Dessen jeweiliger Lauf und Artefakt `operations-evidence` sind der Nachweis für diesen zusätzlichen Containerstand. Ein Workflow ohne erfolgreichen Lauf gilt nicht als bestandene Containerprüfung.

Primärquellen für die Betriebsentscheidungen:

- [Offizielles PostgreSQL-Image](https://hub.docker.com/_/postgres): Bei Version 18 wird das Volume an `/var/lib/postgresql` eingebunden; das interne `PGDATA` liegt versionsabhängig darunter.
- [PostgreSQL 18.6](https://www.postgresql.org/docs/release/18.6/), [pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html) und [pg_restore](https://www.postgresql.org/docs/current/app-pgrestore.html): konsistente Sicherung, exportierte Snapshots und Restore-Werkzeuge.
- [SeaweedFS 4.48](https://github.com/seaweedfs/seaweedfs/releases/tag/4.48) und [S3-API des Projekts](https://github.com/seaweedfs/seaweedfs/wiki/Amazon-S3-API): Release sowie Versionierungs- und versionsgebundene Objektoperationen.
- [S3-Versionierung](https://docs.aws.amazon.com/AmazonS3/latest/userguide/Versioning.html): Neue Uploads erhalten neue Versionskennungen. Eine Kennung des Quell-Buckets kann beim Wiederherstellen nicht als Zielkennung vorgegeben werden.
- [.NET auf Debian](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian): .NET 10 unterstützt Debian 12 und die dort genannten Laufzeitbibliotheken. Das Operations-Image kombiniert diese Laufzeit mit den passenden PostgreSQL-Werkzeugen.
- [ASP.NET Core Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0): gemeinsamer Anwendungsname, Datenbank-Schlüsselring und Verschlüsselungszertifikat.

## Lokal starten

Voraussetzungen sind Docker mit Compose v2, Git und OpenSSL. Die nativen Tests benötigen zusätzlich .NET SDK 10.0.402, Node.js 24.19.0 und PostgreSQL-Clientprogramme. `scripts/verify.sh` verlangt eine echte PostgreSQL-Testverbindung; fehlende Datenbanktests werden dort nicht als erfolgreiche Gesamtprüfung behandelt.

Im Repository:

```bash
./scripts/init-local.sh
# .env öffnen: Bootstrap__Email und Bootstrap__TenantName setzen.
./scripts/start-local.sh
docker compose ps
```

`init-local.sh` erzeugt zufällige PostgreSQL-/S3-Zugangsdaten, ein langes Anfangspasswort und ein passwortgeschütztes RSA-PFX. Das Skript verweigert das Überschreiben vorhandener Zugangsdaten oder Zertifikate. Die anfänglichen Anmeldedaten stehen nur in der privaten `.env`. `.env`, `secrets/`, Backups und PFX-Dateien sind von Git und vom Docker-Build-Kontext ausgeschlossen.

`start-local.sh` baut die Images, startet PostgreSQL und S3, aktiviert Bucket-Versionierung, führt `--migrate` aus und erstellt mit `--bootstrap` den ersten Betrieb samt Geschäftsführung. Anschließend starten API und Worker. Die Anwendung ist unter `http://localhost:8080` erreichbar. Die Host-Ports sind an Loopback gebunden. Bei bereits belegten Ports können `API_PORT`, `POSTGRES_PORT` und `S3_PORT` in `.env` geändert werden.

Die einzelnen administrativen Schritte bleiben verfügbar:

```bash
docker compose run --rm storage-init
docker compose run --rm migrate
docker compose logs --tail=100 api worker
```

Ein Neustart mit `docker compose restart api worker` erhält Datenbank, Objekte und Schlüsselring. `docker compose down` entfernt die Container und das Netzwerk. `docker compose down -v` würde die persistenten Volumes löschen und gehört nicht zum normalen Start-, Update- oder Wiederherstellungsablauf.

Das lokale Compose verwendet ausdrücklich `Development`: HTTP auf Loopback und ein lokaler Schlüsselring sind für diesen Startweg vorgesehen. Ohne eingerichteten SMTP-Anbieter werden Versandaufträge sichtbar als `Failed/provider_unconfigured` abgeschlossen. Das spätere Eintragen eines Anbieters löst keinen automatischen Neuversand fehlgeschlagener Aufträge aus.

Die Oberfläche verwendet deutsche Darstellung; fachliche Betriebstage kommen aus der gespeicherten Zeitzone des Betriebs, standardmäßig `Europe/Berlin`. Zeitpunkte werden als UTC gespeichert. Die Container behalten ICU und Zeitzonendaten; der Globalisierungs-Invariantmodus ist deaktiviert. PDF-Ausgaben verwenden die mitgelieferten DejaVu-Schriften. `FIRSTBLICK_FONT_DIRECTORY` kann auf einen anderen kontrollierten Ordner mit den erwarteten Schriftdateien verweisen.

## Konfiguration für einen vorgesehenen Produktivbetrieb

`compose.yml` ist eine lokale Startkonfiguration. Vor einer externen Freigabe wird eine konkrete Hostingkonfiguration geprüft; es gibt weder ein automatisches Produktionsdeployment noch vorgegebene fremde Infrastruktur. Wesentliche Einstellungen sind:

| Einstellung | Erforderliche Verwendung |
|---|---|
| `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` | Außerhalb des lokalen Betriebs auf `Production` setzen |
| `ConnectionStrings__Database` | API und Worker verwenden dieselbe Datenbank; Betriebswerkzeuge erhalten die für Sicherung bzw. isolierte Zielerstellung nötige Rolle |
| `Storage__Endpoint`, `Storage__Region`, `Storage__Bucket` | API und Worker verwenden denselben versionierten Speicher; die API benötigt versionsgebundene Lese-/Schreibrechte |
| `Storage__AccessKey`, `Storage__SecretKey` | Über die Secret-Verwaltung des Betreibers bereitstellen |
| `DataProtection__CertificatePath`, `DataProtection__CertificatePassword` | Dasselbe RSA-PFX und dieselbe Entschlüsselungsmöglichkeit für API, Worker und Recovery-Werkzeug |
| `App__BaseUrl` | Tatsächliche öffentliche HTTPS-Adresse für Wiederherstellungslinks |
| `Smtp__Host`, `Smtp__From`, `Smtp__Port` | Bewusst eingerichteter E-Mail-Anbieter; üblicher STARTTLS-Port 587 |
| `Smtp__Username`, `Smtp__Password` | Secret-Verwaltung; STARTTLS bleibt erforderlich |
| `FIRSTBLICK_RELEASE` | Eindeutiger auslieferbarer Commit/Image-Stand, der zur Sicherung gehört |
| `Outbox__LeaseSeconds`, `Outbox__SendTimeoutSeconds`, `Outbox__MaximumAttempts` | Grenzen und Standardwerte siehe [jobs.md](jobs.md) |

Die API erzwingt außerhalb der lokalen Umgebungen sichere Cookies und HTTPS. Bei direkter Kestrel-Terminierung werden `Kestrel__Endpoints__Https__Url`, der HTTPS-PFX-Pfad und dessen Passwort konfiguriert. Das HTTPS-Zertifikat ist vom Data-Protection-Zertifikat zu unterscheiden. Bei TLS an einem Proxy müssen dessen Identität, weitergereichte Header, Cookie-Verhalten und HTTPS-Weiterleitung ausdrücklich in der Hostingkonfiguration geprüft werden. Ungeprüfte Forwarded-Header einer beliebigen Gegenstelle werden nicht als Vertrauensbasis verwendet.

Die lokale HTTP-Healthcheck-Adresse wird in einer solchen HTTPS-Konfiguration durch einen passenden HTTPS-Probe ersetzt. `/health/live` prüft den laufenden Prozess, `/health/ready` derzeit die Datenbankverbindung. Die Bereitschaft des Objektspeichers und die SMTP-Konfiguration sind zusätzliche Betriebsprüfungen und werden nicht durch einen grünen Datenbank-Healthcheck behauptet.

API und Worker laufen in den Images als unprivilegierter Benutzer; Compose setzt deren Dateisystem auf schreibgeschützt und stellt temporären Platz bereit. Die persistenten Daten liegen ausschließlich in PostgreSQL und S3. Das Data-Protection-PFX muss mit RSA-Privatschlüssel lesbar sein. Sein Passwort wird separat aufbewahrt. Ein neues PFX darf ein bestehendes Verschlüsselungszertifikat nicht ohne geplante Schlüsselübernahme ersetzen. Das Backup-Werkzeug versucht jeden vorhandenen Data-Protection-Schlüssel zu entschlüsseln und bricht ab, wenn das konfigurierte Zertifikat dafür nicht ausreicht.

## Eine vollständige Sicherung erzeugen

Das Operations-Werkzeug erhält die Konfiguration des zu sichernden Stands. Passwortwerte werden nicht in das Manifest geschrieben. Eine Allowlist übernimmt Betriebsparameter wie öffentliche Basisadresse, SMTP-Host/Absender/Port, Outbox-Grenzen, Zeitzone und Schriftpfad. Die Datenbank enthält Betriebe, Rollen, Module, Nummernkreise, Beziehungen, Änderungsnachweise, Befehlsbelege, Dokumentreferenzen und den Data-Protection-Schlüsselring. Die übrigen Zugangsdaten und gegebenenfalls TLS-Privatschlüssel müssen über die unabhängig gesicherte Secret-Verwaltung wiederherstellbar sein; die benötigten Secret-Kategorien stehen im Manifest.

Mit dem lokalen Compose:

```bash
FIRSTBLICK_OPS_CONTAINER=true ./scripts/backup.sh --output /backups/backup-2026-10-20
FIRSTBLICK_OPS_CONTAINER=true ./scripts/operations.sh verify --input /backups/backup-2026-10-20
```

Der Ordnername ist frei wählbar und muss neu sein. `/backups` im Container entspricht `backups/` im Repository-Verzeichnis. Das Operations-Profil startet keinen Worker. Bei einer vorhandenen Umgebung können die Befehle über `docker compose --profile operations run --rm --no-deps operations ...` ausgeführt werden.

Nativ, mit einem privaten Environment oder dem Secret-System des Betreibers:

```bash
export ConnectionStrings__Database='Host=127.0.0.1;Database=firstblick;Username=backup_role;Password=...'
export Storage__Endpoint='https://storage.example.invalid'
export Storage__Bucket='firstblick'
export Storage__Region='us-east-1'
export Storage__AccessKey='...'
export Storage__SecretKey='...'
export DataProtection__CertificatePath='/secure/firstblick-data-protection.pfx'
export DataProtection__CertificatePassword='...'
export FIRSTBLICK_RELEASE='the-reviewed-deployable-commit'
./scripts/backup.sh --output /secure/backups/backup-2026-10-20
```

Die Platzhalter werden nicht in die Shell-Historie eines gemeinsam genutzten Rechners kopiert. Eine private Environment-Datei oder die Secret-Injektion des Hosts ist für reale Zugangsdaten vorgesehen. `FIRSTBLICK_DOTNET` kann den Pfad zu einer vorhandenen .NET-Installation setzen. `FIRSTBLICK_PG_DUMP` und `FIRSTBLICK_PG_RESTORE` wählen bei mehreren installierten PostgreSQL-Versionen die konkreten Programme.

Die Sicherung arbeitet in dieser Reihenfolge:

1. Vorhandene Data-Protection-Schlüssel mit dem vorgesehenen Zertifikat öffnen und eine Probe erzeugen, die nach dem Restore wieder entschlüsselt werden muss.
2. Einen lesenden PostgreSQL-Repeatable-Read-Snapshot exportieren. Dokumentreferenzen, Tabellenprüfsummen und `pg_dump --snapshot` verwenden denselben Snapshot.
3. Alle Tabellenzeilen in definierter Reihenfolge und mit Längenrahmung per SHA-256 prüfen. Der vollständige Custom-Format-Dump enthält auch Schema, Fremdschlüssel, Sequenzen, Identity und Migrationstabelle.
4. Für jedes Dokument im Zustand `Ready` oder `Archived` genau die gespeicherte `VersionId` herunterladen. Byteanzahl und SHA-256 müssen dem Datenbankeintrag entsprechen. Ein fehlender oder unversionierter Verweis beendet die Sicherung mit Fehler.
5. Das verschlüsselte Data-Protection-PFX, Manifest und Datei-Prüfsummen aufnehmen. Erst nach vollständigem Erfolg wird `COMPLETE` mit dem Manifest-Hash geschrieben.

Während des Datenbank-Snapshots können normale Schreibvorgänge weiterlaufen. Später geschriebene Daten sind nicht Teil dieser Sicherung. Die übernommenen S3-Versionen müssen bis zum Abschluss verfügbar bleiben; eine gleichzeitige endgültige Löschung referenzierter Versionen würde den Vorgang abbrechen. Nicht referenzierte verwaiste Objekte und unfertige `Staged`-Uploads gehören nicht zum Dokumentinhalt einer Sicherung. Ihre vorhandenen Datenbankeinträge bleiben im Dump enthalten.

`COMPLETE` und Prüfsummen belegen Konsistenz und unbeabsichtigte Beschädigungen. Sie sind keine kryptografische Signatur eines fremden Backups. Es werden ausschließlich Sicherungen aus einer kontrollierten Herkunft wiederhergestellt. Der Backup-Ordner enthält vertrauliche Geschäfts- und Identitätsdaten sowie ein verschlüsseltes PFX; er erhält private Verzeichnisrechte und muss im vorgesehenen verschlüsselten, getrennten Backup-Speicher aufbewahrt werden. Das PFX-Passwort gehört nicht in denselben Sicherungsordner. Die synthetischen Drill-Zugangsdaten sind hiervon getrennt und dienen nur dem lokalen Test.

## In ein isoliertes Ziel wiederherstellen

Vor einem echten Umschalten bleiben Quellstand und Sicherung erhalten. Der Restore-Befehl aktiviert weder API noch Worker. Zielnamen sind auf neue `firstblick_restore_*`-Datenbanken und `firstblick-restore-*`-Buckets begrenzt:

```bash
FIRSTBLICK_OPS_CONTAINER=true ./scripts/restore.sh \
  --input /backups/backup-2026-10-20 \
  --database firstblick_restore_20261020 \
  --bucket firstblick-restore-20261020 \
  --report /backups/restore-2026-10-20.json
```

Für einen produktiven Stand muss `FIRSTBLICK_RELEASE` genau dem gesicherten auslieferbaren Stand entsprechen. Das Quell-PFX-Passwort muss zum mitgesicherten Zertifikat passen. Für den zuerst nachgewiesenen Restore ist dieselbe PostgreSQL-Hauptversion wie bei der Sicherung erforderlich; ein Hauptversionswechsel wird danach separat geprüft. `pg_dump`/`pg_restore` dürfen nicht älter als die gesicherte Server-Hauptversion sein.

Zunächst werden alle Dateien geprüft. Ein falsches PFX-Passwort oder beschädigte Datei wird vor der Anlage des Ziels zurückgewiesen. Ein vorhandener Bucket oder eine vorhandene Zieldatenbank wird nicht überschrieben. Nach Anlage und `pg_restore --single-transaction --exit-on-error` müssen alle Tabellenanzahlen und Zeilen-Hashes dem Snapshot entsprechen. Anschließend muss der wiederhergestellte Schlüsselring die vor der Sicherung erzeugte Probe öffnen können.

Das Werkzeug lädt jedes geprüfte Objekt in den neuen versionierten Bucket hoch und lädt dessen neue Version zur erneuten SHA-/Größenprüfung zurück. Danach werden die Dokumentreferenzen in einer Transaktion auf die **neuen** S3-Versionskennungen umgestellt. Dokumentrevisionen steigen; Zuordnung und Wiederherstellungsnachweis werden auditiert. Objektpfade, Dokument-IDs, tenantgebundene Beziehungen und fachliche Snapshot-Digests bleiben erhalten.

Dieselbe Transaktion setzt eine Wiederanlaufsperre für wiederhergestellte Berechtigungs- und Versandzustände:

- Offene externe `Pending`-/`Leased`-Jobs werden `Uncertain`, ihre Leases aufgehoben und `restore_requires_reconciliation` vermerkt. Auch ein zum Snapshot noch nicht gestarteter Auftrag könnte nach dem Snapshot bereits gesendet worden sein.
- Zugehörige offene Angebotsversuche werden `Uncertain`. Ein angenommenes Angebot bleibt angenommen; Kalkulation und Annahmezeit werden nicht aus einem Versandstatus abgeleitet.
- Alle Identity-SecurityStamps werden erneuert. Alte Sitzungen und zuvor ausgestellte Rücksetz-Tokens werden ungültig; die gespeicherten Passwort-Hashes bleiben für eine neue Anmeldung erhalten.
- Unbenutzte Einladungen werden gesperrt. Nicht verbrauchte Freigaben werden abgelaufen. Benötigte Einladungen und Freigaben müssen nach der Prüfung neu erstellt werden.
- Interne `entity.changed`-Marker bleiben interne Aufgaben. Fachliche Nummernkreise, Zahlungszuordnungen, Belege und Idempotenznachweise werden nicht pauschal zurückgesetzt.

Der Restore-Bericht enthält Snapshot-Zeit, Anwendungsversion, Tabellen-/Zeilenzahlen, Versionenzuordnungen, Zahl der gesperrten Aufgaben und die gemessene Dauer. Bei einem Fehler gibt es keinen erfolgreichen Abschlussbericht. Bereits erzeugte isolierte Ressourcen bleiben zur Untersuchung bestehen und werden nicht durch eine automatische weitere Wiederholung überschrieben. Ein neuer Versuch erhält neue Zielnamen.

## Umschalten und Rückfall

Die verantwortliche Person legt das akzeptable Datenverlustfenster und die Zielzeit für einen repräsentativen Betrieb fest. Diese Zielwerte sind Eingaben für eine Abnahme und werden nicht aus dem kleinen Testdatensatz erfunden. Vor einem geplanten Wechsel werden API-Schreibzugriffe und Worker angehalten; offene Offline-Änderungen der Geräte und bereits laufende externe Übertragungen werden erfasst.

Für einen geplanten Versionswechsel:

1. Den vorgesehenen Stand bauen und anhand Tests, Migrationsstand und isolierter Wiederherstellung prüfen. Bisherigen Image-Digest, Konfiguration und Secret-Referenzen festhalten.
2. Schreibphase beenden, Worker stoppen und eine finale zusammengehörige Sicherung erstellen. Den Zeitpunkt dokumentieren.
3. Migrationen bewusst mit dem geprüften API-Image und `--migrate` ausführen. Start des Workers erfolgt erst nach erfolgreicher Datenprüfung.
4. Anmeldung, Rollen, Dokumentdownload/PDF, Nummernkreis, offene Posten und relevante Arbeitsabläufe im Ziel prüfen. Unsichere externe Aufträge anhand tatsächlicher Providerbelege klären.
5. Erst anschließend Datenbank, Bucket, Zertifikat und Release des geprüften Ziels gemeinsam in die aktive Konfiguration übernehmen. Geräte müssen Daten und Revisionen neu laden. Der Worker wird als eigener letzter Schritt freigegeben.

Ein Rückfall auf das bisherige Image ist nur zulässig, wenn dessen Schema und Daten mit den inzwischen erfolgten Änderungen kompatibel sind. Eine Datenbank aus einer älteren Sicherung würde jüngere Vorgänge verlieren. Vor einem solchen Rückfall werden neu angelegte Rechnungsnummern, Zahlungen, Belege, Benutzer-/Rollenänderungen und externe Sendungen seit dem Snapshot vollständig übernommen oder mit einer ausdrücklich geprüften Übernahmeregel abgeglichen. Gesicherte alte Nummernkreise dürfen nicht blind zur erneuten Vergabe bereits extern verwendeter Nummern führen.

Die erste erfolgreiche Wiederherstellung stellt den gesicherten Snapshot wieder her. Sie behauptet keine Wiedergewinnung unbekannter Änderungen nach diesem Zeitpunkt. Historische Idempotenzantworten bleiben Belege ihrer ursprünglichen Ausführung; nach einem Restore werden aktuelle Dokument-/Objektrevisionen neu gelesen. Ungeklärte E-Mail-Ergebnisse werden über den auditierten Job-Klärungsbefehl abgeschlossen, ohne den Provider erneut aufzurufen. Ein tatsächlicher Neuversand ist ein neuer bewusster Geschäftsauftrag.

## Den Wiederherstellungstest wiederholen

Das Testwerkzeug erstellt zufällige, isolierte Quell- und Zielnamen. Es verwendet weder den konfigurierten Live-Bucket noch dessen Dokumente als Testdaten. Der Datenbankzugang braucht hierfür `CREATE DATABASE`; die S3-Zugangsdaten brauchen Rechte zur Anlage und späteren Löschung der eigenen Test-Buckets.

```bash
FIRSTBLICK_OPS_CONTAINER=true ./scripts/restore-drill.sh --output /backups/drill-2026-10-20
```

Der Test erzeugt zwei Betriebe, Identity-Anmeldung, tenantgebundene Beziehungen, ein angenommenes Angebot, Nummernkreis, Idempotenzbeleg, Freigabe, Einladung, offene interne/externe Aufgaben und zwei Dokumente. Ein späterer Upload überschreibt bewusst den neuesten Objektstand, während die Datenbank auf die ältere Version zeigt. Ein zusätzlich nach dem Backup geschriebener Betrieb darf im Restore nicht erscheinen.

Die Prüfung manipuliert außerdem ein gesichertes Objektbyte und erwartet einen Fehler vor der Zielanlage. Das richtige Byte wird danach wiederhergestellt. Ein falsches Zertifikatspasswort und ein bereits vorhandenes Restore-Ziel werden abgewiesen. Der erfolgreiche Durchlauf prüft anschließend alle Tabellen-Hashes, die historischen Objektbytes, archivierte Dokumente, Entschlüsselung, neue S3-Versionen, Nummernkreis, Identitätsanmeldung, gesperrte Sitzungen/Einladungen/Freigaben und die Versandquarantäne. Nach Erfolg werden ausschließlich die für diesen Drill zufällig angelegten Datenbanken und Buckets entfernt. Bundle und Maschinenberichte bleiben erhalten.

Ein repräsentativer produktiver Test muss zusätzlich Datenumfang, Transferzeit, externe Speicher-Aufbewahrung, verfügbare Geheimnisse, Hostingkonfiguration, tatsächlich erlaubtes Datenverlustfenster und organisatorische Umschaltzeit abdecken. Echte Altbestandsübernahme aus dem früheren System erfordert einen bereitgestellten Export und einen separat überprüften Zuordnungs- und Abgleichplan; ein synthetischer Restore ersetzt diesen Abgleich nicht.
