# Betriebskennzahlen und Prozessmetriken

Stand: Roadmap-Checkpoint 8, 20.10.2026. Zugeordnet zu DP-02-07 und DP-13-03.

## Betriebsbezogener Zustand

`GET /api/v1/operations/snapshot` liefert den Datenbankzustand des aktuell autorisierten Betriebs mit Schema `firstblick.operations/1.0.0`. Die Geschäftsführung erhält folgende Summen; andere Rollen erhalten keinen Zugriff:

| Gruppe | Kennzahlen | Auslegung |
|---|---|---|
| Outbox | `pending`, `due`, `leased`, `expiredLeases`, `failed`, `uncertain` | Anstehende Arbeit wird von bereits fälliger Arbeit getrennt. Eine fehlende oder abgelaufene Lease wird als abgelaufen gezählt. |
| Wartezeit | `oldestDueAgeSeconds` | Alter des ältesten fälligen Pending-Jobs, aus der Datenbankzeit berechnet; bei keiner fälligen Arbeit explizit null Sekunden. |
| Dokumente | `ready`, `staged`, `stalled` | Ready- und Staged-Zustände sowie seit mindestens 900 Sekunden nicht abgeschlossene Staging-Einträge. |
| Hinweise | `external_outcome_uncertain`, `failed_jobs`, `expired_leases`, `queue_delay`, `stalled_uploads` | Feste Hinweiskennungen mit Schwere und Anzahl betroffener Datensätze. Die Wartezeitwarnung beginnt bei 300 Sekunden. |

`failed` enthält auch historisch endgültig gescheiterte oder manuell als nicht zugestellt geklärte Vorgänge; es ist keine Zählung ausschließlich noch offener Klärungsfälle. Die Schwellen sind dokumentierte technische Anfangswerte, keine mit einem Betreiber vereinbarten SLOs. Der Snapshot enthält keine Belegnamen, Nutzernamen, Empfänger, Providerreferenzen, Fehlermeldungen oder Job-Payloads. Er wird mit `Cache-Control: no-store, private` ausgeliefert. Zusätzliche Query-Selektoren werden abgewiesen.

Nach Erwerb der betrieblichen Transaktionssperre werden die aktuelle Mitgliedschaft, Rolle und Recovery-Sperre erneut geprüft. Ein einzelnes SQL-Statement liest die beiden Aggregate mit einer einheitlichen Datenbankzeit. Je Tabelle werden die Datensätze des eigenen Betriebs aggregiert. Die Antwortgröße ist fest begrenzt; der Datenbankaufwand wächst weiterhin mit dessen Job- und Dokumenthistorie. Repräsentative Produktionslast ist damit noch nicht nachgewiesen.

Die Oberfläche `/operations` verwirft alte Antworten beim Betriebs- oder Rechtewechsel. Fehler und unvollständige Antworten ersetzen keine Messung durch einen erfundenen Nullstand. Der Datenbank-Snapshot misst keine Worker-Heartbeat-Verbindung, keinen S3-Download und keine externe Zustellung. Ein leerer Hinweisbereich ist deshalb keine Gesamtverfügbarkeitszusage.

## Metriken innerhalb eines Prozesses

`FirstBlickTelemetry` verwendet den über Dependency Injection verwalteten `IMeterFactory` und den Meter `FirstBlick.Operations`. Der gleichnamige `ActivitySource` erzeugt bei einem angeschlossenen Listener interne Command-Spans.

| Instrument | Typ / Einheit | Erfasster Vorgang |
|---|---|---|
| `firstblick.command.completed` | Counter / Commands | Beendete Aufrufversuche: `committed`, `replayed`, `rejected`, `cancelled` oder `failed`. Ein Replay erhöht keine Zahl neu gebuchter Fachvorgänge. |
| `firstblick.command.duration` | Histogram / Sekunden | Gesamtdauer des CommandExecutor-Versuchs einschließlich Prüfung und Sperrwartezeit. |
| `firstblick.command.lock_wait` | Histogram / Sekunden | Tatsächlich abgeschlossene Wartezeit auf die betriebliche Transaktionssperre. |
| `firstblick.outbox.transition` | Counter / Übergänge | Vom Prozess erfolgreich persistierte und committete Outbox-Abschlussübergänge; keine Providerzustellbestätigung. |

Command-Erfolg wird nach Rückkehr der erfolgreich abgeschlossenen Datenbanktransaktion klassifiziert. Eine wiederholte Idempotenzantwort erhält `replayed`; fachliche Ablehnungen werden von unerwarteten Fehlern getrennt. Der Outbox-Zähler wird erst nach `CommitAsync` erhöht. Ein veralteter Lease-Claim, der keinen Übergang persistieren darf, erzeugt keinen zusätzlichen Zähler. Claims und manuelle Jobauflösungen werden von diesem Finish-Zähler nicht erfasst. Die Command-Instrumente erfassen nur erreichte CommandExecutor-Aufrufe; bereits durch HTTP-Authentifizierung, CSRF, Tenant-Filter oder JSON-Binding abgewiesene Anfragen zählen nicht mit. Abbruch während des Sperrwartens erzeugt keinen `lock_wait`-Messwert.

Dimensionen sind ausschließlich feste Ressourcenarten, Command-Ergebnisse, bekannte Jobtypen und Zustände. Unbekannte Arten oder Typen werden zu `other`. Mandanten-, Nutzer-, Objekt- und Idempotenzkennungen, Texte und Ausnahmen werden weder in diese Metrikdimensionen noch in die eigenen Command-Spans geschrieben. Die prozessweiten Metriken sind kein mandantenbezogener API-Export und nicht aus der Benutzeroberfläche abrufbar.

Aktuell ist kein externer OpenTelemetry-Exporter, Prometheus-Endpunkt, Alarmdienst oder verteiltes Tracing zwischen API, Worker und Provider eingerichtet. Instrumente allein speichern keine dauerhafte Messhistorie. Ein Betreiber muss den Export, Aufbewahrung, Zugriff, Alarmwege und SLOs bewusst konfigurieren und prüfen. Vorhandene .NET/ASP.NET-Hostinstrumente können separat erfasst werden; dafür gelten deren eigene Konfiguration und Datenfelder.

## Ausgeführte Prüfung

Der vollständige Serverlauf dieses Checkpoints bestand mit **251 Core- und 126 echten HTTP-/PostgreSQL-/Worker-Testfällen**, jeweils null Fehlern und null übersprungenen Fällen. Darin enthalten sind vier `OperationsApiTests` und drei `TelemetryTests`:

- Eigene Aggregate trotz fremder Job-/Dokumenthistorie; fällige und künftige Arbeit, abgelaufene Leases, Staging und private Cache-Header.
- Geschäftsführung gegenüber allen anderen Rollen, unangemeldeten Anfragen und nicht berechtigten Betriebskennungen.
- Rollenentzug während eines über PostgreSQL tatsächlich beobachteten Advisory-Lock-Wartens.
- Explizite leere Zähler ohne erfundene Worker-/Speicherverfügbarkeit.
- Factory-gebundene Messung, begrenzte Dimensionen und Spans ohne private Eingangstexte.
- Getrennte Messung von echtem Commit, Idempotenz-Replay und abgewiesenem Konflikt.
- Outbox-Messung nach persistiertem Übergang; ein erneut verwendeter alter Claim zählt nicht doppelt.

Die UI besitzt zusätzlich kontrollierte Browser-Fixtures für Desktop, Tablet und Mobil. Diese ersetzen keine Prüfung externer Dienstverfügbarkeit. Der genaue abschließende Quellstand und die getrennten Läufe stehen in den Checkpoint-Nachweisen.

## Quellen

- [API-Snapshot](../src/FirstBlick.Api/OperationsEndpoints.cs), [Instrumente](../src/FirstBlick.Infrastructure/FirstBlickTelemetry.cs), [Command-Transaktionen](../src/FirstBlick.Api/ApiInfrastructure.cs), [Outbox](../src/FirstBlick.Infrastructure/OutboxProcessor.cs).
- [API-Tests](../tests/FirstBlick.Api.Tests/OperationsApiTests.cs), [Metriktests](../tests/FirstBlick.Api.Tests/TelemetryTests.cs).
- Microsoft, [Creating metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation): Meter-Lebensdauer über `IMeterFactory`, Instrumente und Dimensionen.
- Microsoft, [ASP.NET Core metrics](https://learn.microsoft.com/en-us/aspnet/core/metrics/overview?view=aspnetcore-10.0): Hostinstrumentierung und getrennte Erfassung.

## HTTP-Anfragekorrelation (23.10.2026)

Angular zeigt eine syntaktisch begrenzte Anfragekennung von höchstens 128 Zeichen aus dem Antwortheader an; nur wenn er fehlt, kann das `requestId`-Problemfeld einspringen. Ein vorhandener ungültiger Header wird verworfen. `traceId` dient nicht als Ersatz. Unbekannte oder fehlerhafte Problem-JSON-Daten werden vor der Anzeige auf bekannte Felder und begrenzte Texte reduziert. Die Kennung erscheint im vorhandenen Fehlertext und wird nicht an einen weiteren Dienst gesendet.

API-, Gesundheits- und Fehlerantworten tragen den vom ASP.NET-Core-Host vergebenen
`X-Request-Id`-Header. Für durch `ErrorMiddleware` erzeugte Problem-Details
entspricht der Header dem ergänzten JSON-Feld `requestId`. Das bereits
existierende `traceId` kann stattdessen eine W3C-Activity-Kennung enthalten
und muss deshalb nicht mit `requestId` übereinstimmen. Ein vom Client gesetzter
`X-Request-Id` wird **nicht** übernommen; damit kann niemand über den Header
fremde Kennungen in der Antwort vortäuschen. Die Kennung enthält weder
Mandanten- noch Benutzeridentität oder Anfrageinhalt. Der Header erleichtert
die Zuordnung eines beobachteten Fehlers zu einem serverseitigen Logeintrag.

Dies ist **keine** W3C-`traceparent`-Weitergabe an Worker oder Provider,
kein OTLP-/Prometheus-Export, keine persistierte Messhistorie und kein Nachweis
übergreifender Ende-zu-Ende-Traces. `DP-02-07` bleibt Teilumfang. Der neue
`CorrelationApiTests`-Fall prüft am realen HTTP-/PostgreSQL-Testhost Erfolg,
Fehler, serverseitige Kennung und Ablehnung einer klientenseitig vorgegebenen
Kennung. Erst ein erfolgreiches CI-Ergebnis auf dem betreffenden Commit ist
ein Ausführungsnachweis.
