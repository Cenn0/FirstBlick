# Bautagebuch und geprüfte Tagesrapporte

## Durchgängiger Ablauf (DP-06-03)

Ein Tagesbericht wird weiterhin im bestehenden Bautagebuch angelegt und als Entwurf
auch offline gespeichert. Vor dem Einreichen müssen seine lokalen Änderungen
übertragen oder geklärt sein. Der Server prüft den aktuellen Datensatzstand erneut.

1. **Entwurf:** Datum, Projekt, Mitarbeiter, Arbeiten, Wetter, Behinderungen und
   Sicherheitshinweise bearbeiten.
2. **Einreichen:** Die ausdrückliche Bestätigung in der Oberfläche führt den
   revisionsgeschützten `POST /diary-entries/{id}/submit` aus. Der Server hält den
   vollständigen Inhalt, Betriebs-/Projekt-/Mitarbeiternamen und Baustellenanschrift
   zusammen mit einreichender Person, UTC-Zeit und SHA-256 fest.
3. **Prüfen:** Eine andere Person aus Bauleitung oder Geschäftsführung bestätigt
   genau diesen Inhaltsdigest über `POST /diary-entries/{id}/review`. Weder die
   einreichende Person noch das betroffene persönliche Mitarbeiterkonto kann
   selbst freigeben. Der Server prüft auch eine inzwischen geänderte Kontozuordnung.
4. **Zurückgeben:** Eine Rückgabe verlangt einen Grund. Der zurückgegebene Bericht
   wird wieder bearbeitbar; vorheriger Prüfvermerk und eingereichter Stand bleiben
   bis zum erneuten Einreichen sichtbar. Projekt und Mitarbeiter bleiben gebunden.
   Erneutes Einreichen erzeugt einen neuen Digest; der alte Prüfbezug wird abgewiesen.
   Die Auditfolge erhält die bisherigen Übergänge.
5. **Rapport:** Erst ein freigegebener Bericht erzeugt über
   `POST /diary-entries/{id}/pdf` ein vollständiges, mehrseitiges A4-PDF. Es enthält
   alle Tagebuchfelder, eingefrorene Stammdaten, Einreichung, Prüfung und Prüfvermerk.
   Nachträgliche Stammdatenänderungen verändern diesen Inhalt nicht.

Das PDF wird als konkrete Version im vorhandenen Objektspeicher gespeichert und
mit Bericht, Projekt, Renderer-Version und eigenem Inhaltsdigest verknüpft. Der
Download prüft Länge und SHA-256 sowohl im Server als auch im Browser. Fremde
Betriebe und nicht berechtigte Mitarbeiter erhalten keinen Zugriff. Späte Antworten
eines früher gewählten Betriebs oder Datensatzes werden in der Oberfläche verworfen.

## Zustände und technische Grenzen

| Zustand | Bearbeiten | Einreichen | Prüfen | Finales PDF |
|---|---|---|---|---|
| Draft | Ja | Ja | Nein | Nein |
| Submitted | Nein | Nein | Andere Bauleitung/Geschäftsführung | Nein |
| Returned | Ja, mit unveränderter Projekt-/Mitarbeiterbindung | Ja | Nein | Nein |
| Approved | Nein | Nein | Nein | Ja |

Vorhandene Tagesberichte ohne die neuen Felder werden als Entwürfe gelesen; es
wird keine rückwirkende Freigabe erfunden. Generische CRUD-Endpunkte dürfen keine
Einreichung oder Prüfung erzeugen, umschreiben oder entfernen. Eingereichte und
geprüfte Berichte lassen sich nicht generisch archivieren. Alle Kommandos verwenden
den vorhandenen Mandanten-Lock, `If-Match`, persönliche Autorisierung und atomare
Audit-/Idempotenzbelege. Ein paralleler Freigabeversuch kann nicht beide Versionen
akzeptieren.

Die Freigabe ist eine interne Dokumentation. Sie erklärt keine Kundenabnahme,
digitale Unterschrift, baurechtliche Bestätigung oder rechtssichere Archivierung.
Ein bestätigter Bericht wird nicht nachträglich still korrigiert; eine notwendige
Ergänzung wird als weiterer, separat geprüfter Tagesbericht dokumentiert.

## Prüfungen

- `DiaryWorkflowTests`: eingefrorene Inhalte, geänderte Stammdaten, Rückgabe,
  erneutes Einreichen, unabhängige Prüfung, alte Digests und unzulässige Quellen.
- `DiaryReportApiTests`: echte HTTP-/Identity-/PostgreSQL-Kommandos, Replay,
  konkurrierende Freigaben, Rollen, unveränderliche Evidenz, Fremdmandanten,
  mehrseitige PDF-Erzeugung und überprüfte Downloadbytes. Der Objektspeicher ist in
  dieser Testsuite ein deterministisches, versioniertes Testdouble.
- `web/tests/browser/diary-report.spec.ts`: Desktop, Tablet und Mobil; Einreichung,
  Rückgabe, Freigabe, PDF-Prüfung und Betriebswechsel mit verzögerter Antwort.
  Diese Browserfälle verwenden kontrollierte HTTP-Antworten und sind kein
  zusätzlicher Live-Datenbanknachweis.

Die endgültige Ausführungsevidenz gehört zum veröffentlichten Commit und dessen
CI-Läufen. Diese Dokumentation allein behauptet keinen erfolgreichen Testlauf.
