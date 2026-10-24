# Verständliche Fehlermeldungen in FirstBlick

**Stand:** 24.10.2026 · Web-Client `web/src/app/core/transport.ts` / `error-catalog.ts`

## Geltungsbereich

Alle API-Aufrufe über `request`, `requestResponse` und `ApiService` liefern
für die Weboberfläche einheitliche deutsche Meldungen über `errorMessage(error)`.
Bereits vorhandene lokale Prüfungen (`throw new Error('...')`) bleiben erhalten.
API-Problem-Details (insbesondere `code`, Status und `requestId`) bleiben für
Diagnose und weitere Clients strukturiert verfügbar.

| Fehlerart | Botschaft / nächste sinnvolle Handlung |
| --- | --- |
| Anmeldung (401 auf Login) | Anmeldedaten prüfen bzw. Passwort zurücksetzen; keine Information darüber, ob ein Konto existiert |
| Abgelaufene Sitzung (401) | Erneut anmelden |
| Rechte (403) / deaktiviertes Modul | Betriebsverwaltung kontaktieren |
| Validierung (400/422, `errors`) | Pflichtfeld, ungültiger Wert, Länge oder Zahlenbereich mit deutschem Feldnamen benennen |
| Konkurrierende Änderungen (409/412/428) | Daten aktualisieren, Eingaben vergleichen bzw. neue Vorschau erzeugen |
| Vorhandene Einträge / Referenzkonflikte | Bestehende Einträge und Referenzen prüfen |
| Nicht unterstützte Datei / zu großer Inhalt (413/415) | Dateigröße oder Dateityp anpassen |
| Ratenlimit (429) | Später erneut versuchen |
| Externe Anbindung (503) | Dienst später erneut prüfen bzw. Betriebsverwaltung kontaktieren |
| Netzfehler / Abbruch | Verbindung und aktuellen Stand kontrollieren, vor erneutem Speichern prüfen |
| Unbekannter API-Fehler (5xx) | Technischen Fehler verständlich melden, Erfolg des Vorgangs **nicht** behaupten; Anfragekennung zur Unterstützung anzeigen |

## Sicherheits- und Datenintegritätsregeln

1. **Keine ungeprüften API-Texte ausgeben.** `detail`, `title` und Felder mit
   unbekannten Bezeichnungen können technische Informationen enthalten. Die
   Anzeige nutzt bevorzugt geprüfte `code`-Texte und ansonsten HTTP-Fallbacks.
2. **Keine falschen Erfolgs- oder Retry-Zusagen.** Bei Netzwerkunterbrechung
   bzw. 5xx ist die Ausführung eines schreibenden Vorgangs unklar. Nutzer sollen
   zuerst den aktuellen Datenstand prüfen. Idempotenzschlüssel bleiben erhalten.
3. **Support-Korrelation:** Servergenerierte `X-Request-Id` kann bei 5xx
   angezeigt werden; ungültige Kennungen und `traceId` werden nicht übernommen.
4. **Keine Account-Enumeration:** Der Login-Fehler für unbekannte Konten und
   falsche Passwörter ist identisch.
5. **Spezialisierte Fehler behalten Vorrang:** z. B. Wiederherstellungssperre,
   Freigabe, GAEB-Vorschau, Null-Euro-Rechnung und Projektvorlagen-Konflikt.
6. **Keine Normativität suggerieren:** Berechnungsfehler und fachliche
   Warnungen dürfen nicht als normativer Nachweis umformuliert werden.

## Erweiterungen

- Für weitere API-Codes gezielte deutsche Nachricht in `error-catalog.ts`
  hinzufügen. Präzise, überprüfbare Aussage und konkrete nächste Handlung.
- Bei neuen Formularfeldern den Feldnamen in `fieldNames` eintragen, damit
  ASP.NET-Validierungsfehler ohne technische Property-Namen lesbar bleiben.
- Regressionen in `web/tests/transport.test.ts` abdecken: HTTP-Status,
  strukturierter Code, Nutzertext, Netzwerk, Grenzfälle und Nichtweitergabe
  technischer Details.
- **Grenze:** Nur Fehler, die `errorMessage` bzw. diese Transport-Schicht
  verwenden, sind durch das zentrale Mapping abgedeckt. Direkt in Seiten
  eingebettete Texte und Meldungen externer Validierungsdokumente brauchen
  separate UI- und fachliche Prüfungen. Ein vollständiger visueller/A11y-Test
  ist durch die Unit-Tests nicht ersetzt.
