# Suche, Projektverlauf und eigene Datenkopie

Die API bietet eine gemeinsame Suche, einen sparsamen Projektverlauf und eine begrenzte Self-Service-Datenkopie. Alle drei Funktionen verlangen eine angemeldete persönliche Identität, eine aktive Mitgliedschaft und den ausgewählten Mandanten über `X-Tenant-Id`. Sie lesen ihre Zugriffsgrundlagen innerhalb derselben PostgreSQL-Mandantensperre wie Änderungen und prüfen die Mitgliedschaft erneut nach dem Erwerb der Sperre. Eine aktive Wiederherstellungssperre blockiert die Funktionen ebenfalls.

Implementierung: [DiscoveryEndpoints.cs](../src/FirstBlick.Api/DiscoveryEndpoints.cs), [DiscoveryAccess.cs](../src/FirstBlick.Api/DiscoveryAccess.cs). Die Datensatzrechte stammen aus der vorhandenen [RecordPolicy](../src/FirstBlick.Api/RecordPolicy.cs); die Funktionen definieren keine großzügigere zweite Berechtigungsregel. Jede erfolgreiche Antwort trägt `Cache-Control: no-store, private`.

## Gemeinsame Suche

`GET /api/v1/search?q=...&kind=...&page=1&pageSize=25`

`q` enthält nach dem Entfernen äußerer Leerzeichen 2 bis 200 Zeichen. Gesucht wird eine Teilzeichenfolge ohne Beachtung der Großschreibung; SQL-Platzhalterzeichen wie `%` und `_` sind gewöhnliche Suchzeichen. Alle Werte sind getrennte SQL-Parameter. `kind` ist optional und wählt genau eine freigegebene Ressource. Eine unbekannte oder ausdrücklich ausgeschlossene Ressource wird abgewiesen; eine für die aktuelle Rolle unzugängliche Ressource liefert `403`.

| Ressourcen | Suchbare und ausgegebene Bezeichnung | Zusätzlich suchbare Nummer |
|---|---|---|
| Kunden, Gebäude, Katalogartikel, Lieferanten, Formularvorlagen | `name` | keine |
| Projekte | `name` | `number` |
| Materialien | `name` | `sku` |
| Geräte | `name` | `inventoryNumber` |
| Angebote, Aufträge, Rechnungen, Bestellungen, Lieferantenrechnungen | `title` | `number` |
| Aufgaben, Nachträge, Mängel, Abnahmen, Wartungsverträge, Dachversionen, Aufmaße, Berechnungen | `title` | keine |
| Dokumente | `name` | keine |

Die Suche greift ausdrücklich nicht auf die allgemeine `SearchText`-Projektion zu. Adressen, Notizen, Beschreibungen, Personalprofile, Arbeitszeiten, Tagebuchtexte, Formulareingaben, Bankreferenzen, Preise, Kalkulationspositionen, Zugangsdaten und Portalunterlagen sind keine zusätzlichen Suchfelder. Ein Treffer durch ein solches verborgenes Feld würde bereits dessen Inhalt verraten. Texte in freigegebenen Titelfeldern bleiben normale Benutzereingaben und müssen in Clients als Text dargestellt werden.

Die Antwort hat den Vertrag `firstblick.search/1.0.0`:

```json
{
  "schema": "firstblick.search/1.0.0",
  "items": [{
    "kind": "projects", "id": "UUID", "revision": 1,
    "title": "Dachsanierung", "number": "P-2026-1", "updatedAt": "UTC timestamp"
  }],
  "total": 1, "page": 1, "pageSize": 25, "capturedAt": "UTC timestamp"
}
```

`page` liegt zwischen 1 und 10.000, `pageSize` zwischen 1 und 50. Ungültige Zahlen, doppelte Parameter und unbekannte Parameter werden abgewiesen; die API klemmt Werte nicht stillschweigend auf Grenzen. SQL zählt und paginiert **nach** Mandanten-, Rollen- und Datensatzfilterung. Sortiert wird nach Änderungszeit absteigend, Ressourcentyp und UUID. Verschiedene Seiten sind eigenständige aktuelle Lesevorgänge; zwischen ihnen können andere Nutzer Änderungen vornehmen. Eine über mehrere Requests unveränderliche Suchmomentaufnahme oder Volltext-Rangfolge wird nicht versprochen.

Archivierte Datensätze und archivierte Dokumente erscheinen nicht. Dokumente benötigen zusätzlich Leserecht auf ihre Quellressource und deren konkreten Datensatz. Für Mitarbeiter ist außerdem eine aktuell sichtbare Projektzuordnung erforderlich. Objektpfad, S3-Version, Dokumentinhalt und Personenprofile werden nicht ausgegeben.

## Projektverlauf

`GET /api/v1/projects/{id}/history?page=1&pageSize=25`

Die API prüft zuerst, ob die aktuelle Rolle das konkrete Projekt lesen darf. Anschließend folgt sie ausschließlich eingehenden Verweisen von projektbezogenen Datensätzen: beispielsweise Projekt → Angebot → Auftrag → Rechnung → Zahlungszuordnung. Die tatsächliche Verweisrichtung in der Datenbank zeigt jeweils vom untergeordneten Datensatz zu seiner Quelle; der Verlauf sucht die dazugehörigen abhängigen Datensätze.

Er folgt nicht den ausgehenden Verweisen zu gemeinsamen Kunden, Gebäuden, Mitarbeitern, Materialstammdaten oder Bankbuchungen. Zwei Zahlungen aus derselben Bankbuchung machen daher die Rechnung eines anderen Projekts nicht zu einem Ereignis dieses Projekts. Dokumente werden über ihren ausdrücklichen Projektbezug oder eine aktuell zugeordnete Quelle berücksichtigt und nochmals durch die Dokumentrechte gefiltert. Personalprofile, Schulungsnachweise, Abwesenheiten, Benachrichtigungsregeln und Portalvorgänge gehören nicht zu diesem Projektverlauf.

Jedes Ereignis enthält nur Audit-ID, Zeitpunkt, einen kontrollierten Aktionscode, Ressourcentyp, Datensatz-ID, Revision, einen allgemeinen deutschen Titel, `performedByCurrentUser` und gegebenenfalls einen gültigen Inhaltsdigest. Titel lauten etwa „Zeiteintrag geändert“, „Rechnung ausgestellt“ oder „Dokument bereitgestellt“. Namen, E-Mail-Adressen, Tätigkeitsbeschreibungen, Geldbeträge, rohe Audit-Aktionstexte, Akteur-IDs und interne Korrelationskennungen werden nicht in Ereignistitel oder zusätzliche Profilfelder übernommen. Unbekannte Aktionen werden als `record.changed` dargestellt. Ein Digest ist ein technischer Inhaltsbezug und keine externe Signatur oder rechtliche Beweiskraftzusage.

Die Antwort enthält `schema: firstblick.project-history/1.0.0`, `projectId`, `associationScope: current-accessible-project-links`, `items`, `total`, `page`, `pageSize` und `capturedAt`. Jedes Element hat die Felder `id`, `occurredAt`, `action`, `kind`, `entityId`, `revision`, `title`, `performedByCurrentUser`, `contentDigest`. Sortiert wird nach Auditzeit und Audit-ID, jeweils absteigend. Für Zählung und Pagination gelten die aktuellen Datensatzrechte; ein Mitarbeiter erhält beispielsweise keine Ereignisse fremder Arbeitszeiten oder interner Finanzbelege.

**Die Projektzuordnung ist aktuell, nicht historisch rekonstruiert.** Das Audit speichert IDs und Digests, jedoch keine vollständigen früheren Projektverknüpfungen. Wird eine zulässig verschiebbare Aufgabe einem anderen Projekt zugeordnet, erscheint ihre vorhandene Historie beim neuen Projekt und nicht mehr beim alten. Der Endpunkt behauptet deshalb weder einen vollständigen historischen Projektbestand noch eine Historie früherer Zugriffsrechte. Lesbare archivierte, weiterhin verknüpfte Datensätze können im Verlauf vorkommen; persönliche Rechte verwenden weiterhin die bestehende Regel für aktuelle, nicht archivierte Profile und Zuordnungen.

## Eigene Self-Service-Datenkopie

`GET /api/v1/self/data-copy`

Der Endpunkt akzeptiert keine Queryparameter und insbesondere keine fremde Benutzer-, Mitarbeiter- oder Mandanten-ID. Auch eine Geschäftsführung erhält hier nur eine Kopie für ihre **eigene angemeldete Identität** im ausgewählten Mandanten. Partner können ihre eigenen sicheren Kontofelder und ihre aktuelle Mitgliedschaft kopieren, erhalten dadurch aber keine internen Geschäftsdaten.

Die ZIP-Datei enthält vier UTF-8-JSON-Dateien:

| Datei | Inhalt |
|---|---|
| `profile.json` | Eigene ID, Anzeigename, E-Mail-Adresse und Bestätigung, Telefonnummer und Bestätigung |
| `membership.json` | Eigene aktive Mitgliedschaft mit Rolle und Revision; Name und Zeitzone des ausgewählten Betriebs |
| `records.json` | Eigene nicht archivierte, aktuell lesbare Arbeitszeiten, Tagebucheinträge, selbst abgegebene Formularantworten und eigene Benachrichtigungsregeln |
| `manifest.json` | Version `firstblick.self-data-copy/1.0.0`, Kopie-ID, Zeitpunkt, eigener Benutzer und Mandant, enthaltene Kategorien und Zählungen, Grenzen, Ausschlüsse sowie Dateigrößen und SHA-256 der anderen drei Dateien |

Arbeitszeiten und Tagebucheinträge werden über die aktuelle Zuordnung des Mitarbeiterprofils zur angemeldeten Identität ausgewählt. Formularantworten werden über `SubmittedBy`, Benachrichtigungsregeln über `UserId` ausgewählt. Jede Auswahl wird zusätzlich mit den bestehenden Leserechten geschnitten. Eine deaktivierte persönliche Mitarbeiterverknüpfung entfernt deshalb die darüber zuvor lesbaren Arbeitszeiten eines Mitarbeiters; eine Rolle ohne Feldzugriff erhält keine solchen Einträge. Der Endpunkt erweitert keine Rechte auf archivierte oder inzwischen unzugängliche Inhalte. Bereits vorhandene Formularantworten behalten die in `RecordPolicy` definierte persönliche Sichtbarkeit des Einreichers.

Die Dateien enthalten die validierten typisierten eigenen Datensätze einschließlich ihrer ursprünglichen Werte und eingefrorenen Formularfelder. Sie enthalten keine dazugehörigen fremden Personalprofile oder automatisch ergänzten Dokumente. Die Datenbank-Metadaten zu Mandant, ID, Revision und Archivstatus müssen mit dem gespeicherten Datensatz übereinstimmen; andernfalls wird die gesamte Kopie abgewiesen.

Passworthashes, Sicherheitsstempel, Wiederherstellungstokens, Einladungsgeheimnisse, Schlüsselmaterial, Sitzungsdaten und Zugangsschlüssel werden für diese Antwort nicht geladen. Der Audit-Eintrag `self.data-copy` enthält einen Digest über Kopie-ID, Version, Anzahl und Datei-/ZIP-Digests, nicht die exportierten Texte. HTTP liefert `Content-Type: application/zip`, eine Dateianlage, `X-FirstBlick-Export-Schema` und `X-Content-SHA256` für die gesamte ZIP-Datei. Manifest und äußere ZIP-Prüfsumme sind Inhaltsprüfungen, keine unabhängige Beglaubigung.

Dies ist eine **begrenzte Self-Service-Datenkopie, keine vollständige datenschutzrechtliche Auskunft**. Nicht erfasst sind insbesondere andere Mandanten, archivierte oder aktuell nicht zugängliche Datensätze, Personalprofile, Schulungsnachweise, Dateien und Anhänge, Audit und Kommunikation, andere Geschäftsdaten, frühere Datenstände sowie Daten außerhalb dieser Anwendung. Eine umfassendere individuelle Auskunft muss der zuständige Betrieb gesondert prüfen und bearbeiten; aus dem erfolgreichen ZIP-Download folgt keine vollständige Erfüllung einer solchen Anfrage.

## Grenzen und überprüfbare Nachweise

Die derzeitige zentrale Datensatzregel lädt einige abhängige Sammlungen. Vor ihrer Verwendung prüfen Suche, Verlauf und Datenkopie gemeinsam höchstens 10.000 solche Datensätze und 12 MiB JSON. Überschreitung führt zu `discovery_policy_budget`; Berechtigungsgrundlagen werden niemals abgeschnitten. Der Verlauf akzeptiert höchstens 10.000 aktuell verknüpfte Quell-Datensätze (`history_budget`). Die Datenkopie akzeptiert höchstens 5.000 eigene Datensätze und 10 MiB Quell-JSON (`data_copy_budget`). Sie liefert bei Überschreitung kein unvollständiges ZIP. Das sind Produktbudgets dieser Version, keine Aussagen über eine erfolgreich gemessene Großinstallation.

Die echten HTTP-/PostgreSQL-Regressionstests stehen in [DiscoveryApiTests.cs](../tests/FirstBlick.Api.Tests/DiscoveryApiTests.cs). Sie prüfen sichere Suchfelder, Literalbehandlung von SQL-Sonderzeichen, strenge Pagination, Mandantentrennung, aktuelle Mitarbeiterzuordnungen, Dokumentquellrechte, gerichtete Projektverknüpfungen einschließlich gemeinsam genutzter Bankbuchungen, zurückhaltende Ereignisprojektion, eigene Exportauswahl, ZIP-/Datei-SHA, Profil-/Rollenentzug und widersprüchliche gespeicherte Metadaten. Zwei zusätzliche Fälle beobachten einen tatsächlichen PostgreSQL-Sperrwartenden vor einem Mitgliedschaftsentzug und weisen 5.001 gültige eigene Datensätze ohne Teilarchiv zurück. [Der ausgeführte Nachweis](evidence/discovery-checkpoint7.json) dokumentiert 11 bestandene echte HTTP-/PostgreSQL-Fälle, keine übersprungenen, und SHA-256 des geprüften Quellstands. Die neuen API-Endpunkte allein belegen noch keine Bedienoberfläche oder produktive Datenschutzabnahme.
