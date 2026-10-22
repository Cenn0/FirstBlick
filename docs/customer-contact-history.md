# Änderungshistorie für Kundenansprechpartner

## Funktionsumfang

**DP-04-01** erhält einen durchgängigen Änderungsverlauf für die zusätzlichen Ansprechpartner eines Kunden. Die bestehende Kundenansicht bietet bei jedem aktiven oder archivierten Kontakt die Aktion **Änderungsverlauf**. Der Verlauf zeigt die gespeicherte Aktion, den damaligen Namen der bearbeitenden Person, den Zeitpunkt, den Kundenstand sowie geänderte Felder mit Vorher-/Nachherwerten. Die Benutzeroberfläche verwendet für die Zeitanzeige die lokale Zeitzone des Browsers; die gespeicherten Zeitpunkte sind UTC.

Die vier bestehenden Kontaktkommandos dokumentieren Anlage, Bearbeitung, Archivierung und Wiederherstellung. Wird ein neuer Hauptkontakt gewählt, erhält auch der bisherige Hauptkontakt einen eigenen Beleg über den Entzug dieser Zuordnung. Ein erneutes Speichern unveränderter Werte bleibt als solche Aktion sichtbar.

Die Funktion erzeugt **keine rückwirkend erfundene Historie**. Bestandskontakte ohne neue Änderung haben einen leeren Verlauf. Bei ihrer ersten Änderung wird ausschließlich der zu diesem Zeitpunkt vorliegende alte Kontaktstand als Vorherwert gesichert. Die zuvor vorhandenen allgemeinen Kundenfelder `ContactName`, `Email`, `Phone`, `Address` und `Notes` erhalten durch diese Erweiterung keine eigene Feldhistorie.

## Speicherung und Zugriff

Jede Änderung wird als separates `CustomerContactChange`-Original unter `customer-contact-changes` gespeichert. Der Datensatz enthält Kunden- und Kontaktkennung, Kundenrevision, Änderungsart, vollständigen vorherigen und neuen Kontaktstand, Akteurkennung, damaligen Anzeigenamen, UTC-Zeit und einen SHA-256-Inhaltsdigest. Bestehende Kunden-Snapshots erhalten kein neues Feld; die ursprüngliche Serialisierung bleibt unverändert.

Kontaktmutation, Historieneinträge, Audit, Outbox und Idempotenzbeleg werden in derselben bestehenden Kommandotransaktion geschrieben. Das Wiederholen eines bereits bestätigten Kommandos erzeugt keine zusätzlichen Historieneinträge. Optimistische Kundenrevision und aktuelle Rechte bleiben erforderlich. Pro Kunde, Kontakt und Kundenrevision ist höchstens ein Änderungsbeleg zulässig.

Der neue Leseendpunkt lautet:

```text
GET /api/v1/customers/{customerId}/contacts/{contactId}/history?page=1
```

Er verlangt das aktuelle Kundenleserecht, prüft Mandant und Kontaktzugehörigkeit und liefert höchstens 20 Änderungen je Seite, nach absteigender Kundenrevision. Die Antwort enthält den Kundenstand und den aktuellen Kontakt zum Vergleich. Archivierte Kontakte und archivierte Kunden behalten ihren lesbaren Verlauf. Für den Historien-Kind werden keine generischen Lese-, Änderungs-, Anlage- oder Löschrouten bereitgestellt.

Die Oberfläche bindet jede Antwort an den geöffneten Kunden, Betrieb, Kontakt, Kundenstand und die angeforderte Seite. Sie verwirft verspätete Antworten nach Betriebs-/Rechte-/Kontaktwechsel. Änderungen zwischen dem Öffnen des Kunden und dem Abrufen weiterer Verlaufsseiten verlangen ein erneutes Laden des Kunden. Die Oberfläche zeigt nur strukturell passende, korrekt zugeordnete Vorher-/Nachherfolgen; der Server prüft zusätzlich die gespeicherten Digests.

## Migration und Grenzen

Die kanonische Migrationsprüfung kontrolliert den Originaldigest, historische Akteure, die Kunden-/Kontaktbindung, die Kundenrevisionen und die zeitliche Folge. Alle gelieferten Änderungen eines Kontakts müssen eine durchgehende Vorher-/Nachherkette bilden. Der letzte gelieferte Nachherstand muss dem aktuellen Kontakt entsprechen. Ein erster Eintrag ohne Anlagebeleg ist bei einem Bestandskontakt ausdrücklich zulässig; er behauptet keine Vollständigkeit vor Beginn der Aufzeichnung.

Der Migrationsdrill ergänzt einen Kontakt mit Anlage, Bearbeitung, Archivierung und Wiederherstellung sowie negative Varianten für veränderte Originale, falsche Kundenrevision, fremde Kontaktkennung, unterbrochene Kette und doppelte Kundenrevision. Nach der Übernahme werden dieselben ursprünglichen Werte und Akteursangaben geprüft.

Die Anwendung bietet eine unveränderliche Historie innerhalb ihrer normalen Schreibwege. Diese ist kein extern manipulationssicheres WORM-Archiv und kein vollständiger Nachweis jeder früheren Änderung. Direkte Datenbankeingriffe mit administrativen Rechten liegen außerhalb dieses Schutzversprechens. Automatische Postfachübernahme, Kundenzusammenführung und externe Kontaktsynchronisierung bleiben offene CRM-Teilumfänge.

## Prüfumfang

- `CustomerContactHistoryTests`: vollständige Zustandsfolge, Hauptkontaktwechsel, Beginn bei Bestandskontakten, Inhalts-/Identitätsschutz und fehlende Kettenglieder.
- `CustomerContactHistoryApiTests`: echte HTTP-/Identity-/PostgreSQL-Kommandos, unveränderte Originale, Idempotenz, Rollen/Mandanten, archivierte Kunden, Pagination und korrupte gespeicherte Werte.
- `customer-contact-history.test.ts`: Clientverträge, Vorher-/Nachherdarstellung, ungültige Identität, Stand, Reihenfolge und Aktionsart.
- `customer-contact-history.spec.ts`: kontrollierte Browser-Fixtures für Desktop, Tablet und Mobil, lesende Rollen, Seitenwechsel, alte Kontakte, fremde Antworten und verspätete Antworten nach Betriebswechsel.
- `MigrationContactHistory.cs`: Erhalt und negative Importvarianten in der bestehenden Migrationsprüfung.

Geschriebene Testfälle allein sind kein Ausführungsnachweis. Die tatsächlich ausgeführten Server-, Client-, Browser- und Operations-Prüfungen werden am zusammengeführten Commit nachgewiesen.
