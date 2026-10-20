# Checkpoint 9: Finanzabläufe und geprüfte Produktionsoberfläche

Stand: 20.10.2026. Aufbauend auf dem veröffentlichten Backend-Checkpoint [`2ac1310`](https://github.com/Cenn0/FirstBlick/commit/2ac13105c78d077101350fc61f05c541a3728506). Der maschinenlesbare [Nachweis](evidence/checkpoint9.json) enthält die getrennten Läufe, synthetischen Ergebnisbelege, Bildprüfsummen, tatsächlichen Produktionsassets und Quell-SHA-256. Die abschließenden GitHub-Läufe für den veröffentlichten Stand werden in [PR #1](https://github.com/Cenn0/FirstBlick/pull/1) verlinkt.

## Ergebnis für den Betrieb

**Rechnungsfolgen behalten ihre ursprünglichen Netto- und Steuer-Cents.** Die Berechnung berücksichtigt ursprüngliche Quellpositionen und bereits tatsächlich berechnete Beträge. Korrekturen verbrauchen die Centbeträge ihres Ursprungsbelegs. Die Oberfläche und PDF-Ausgabe zeigen diese tatsächlichen Beträge samt kumulativer Herleitung. Historische Belege behalten ihre bisherigen Digests. [Rechenvertrag und Grenzen](invoice-rounding.md).

**Forderungen besitzen einen eigenen bearbeitbaren Vorgang.** Zuständigkeit, Wiedervorlage, aktuelle Salden, manuelle Mahnstufen, Streitfälle, Kontakte, Rücknahme, Abschluss und Wiederöffnung sind nachvollziehbar. Eine genehmigte Vorschau ist an ihre konkreten Quellen und den Fallstand gebunden. Der echte Test öffnet eine historische Forderung über 119 EUR, bucht 20 EUR über die echte Zahlungsschnittstelle, weist die alte Vorschau ab und genehmigt erst den neu geprüften Text über 99 EUR. Die Freigabe dokumentiert einen internen Entwurf; eine tatsächliche Kontaktaufnahme wird separat mit Beleg erfasst. [Forderungsablauf](collections.md).

**Die Nachkalkulation lässt sich aus ihren Quellen bedienen und prüfen.** Der geprüfte Fall verwendet 7,5 freigegebene Arbeitsstunden zu ausdrücklich angesetzten 40 EUR, 80 EUR zugeordnete Fremdleistung und 100 EUR begründete Restkosten. Das ergibt 380 EUR Istkosten, 480 EUR Prognose und 520 EUR Überschuss bei 1.000 EUR Vertragsnetto. Unbekannte Kosten bleiben sichtbar. Der JSON-Bericht erhält Originalbeträge, Quellen, Revisionen und Digest. [Kostenmodell](cost-analysis.md) und [Bedienung](checkpoint8-ui.md).

**Die Geschäftsführung sieht den gespeicherten Betriebszustand.** `/operations` zeigt eigene fällige Jobs, abgelaufene Bearbeitungssperren, Fehler, unklare externe Ergebnisse und nicht abgeschlossene Dateiübernahmen. Die API prüft Rechte und Recovery-Sperre nach dem Warten auf die Mandantensperre erneut. Prozessmetriken unterscheiden Commit, Idempotenz-Replay, Ablehnung und tatsächliche Outbox-Abschlussübergänge. [Betriebsbeobachtung](observability.md).

## Konkrete Fehlerkorrekturen

Die zusätzliche Prüfung hat mehrere reproduzierbare Fehler geschlossen:

- Ein bekannter HTTP-401-/403-Fehler entfernt geladene Rechnungen und ihre Quellenkopien. Alte Detailantworten dürfen die Daten nach einem Rechtewechsel nicht wieder einsetzen. Dokumentdownloads prüfen ebenfalls den noch gültigen Kontext.
- Beim Auftragswechsel lässt sich das Verwerfen ungespeicherter Kostenansätze abbrechen. Große Dezimalbeträge bleiben in der Anzeige zifferngenau; auch ein gültiges Vertragsaggregat von `100000000000000.01` EUR verliert keinen Cent durch Gleitkommakonvertierung.
- Ein reiner Kalendertag wie `2026-11-01` bleibt auch in einer Zeitzone westlich von UTC der 1. November. Tablet-Positionswerte bleiben zusammen; die Tabelle besitzt einen eigenen Scrollbereich.
- Der Produktionsbuild lädt sein globales Stylesheet direkt. Die bisherige Optimierung erzeugte eine `media=print`-Verknüpfung, deren Inline-Aktivator von der echten CSP blockiert wurde. `inlineCritical=false` beseitigt diese Abhängigkeit; `script-src 'self'` bleibt erhalten. Der Container-Smoke prüft nun tatsächlich ausgeliefertes HTML, CSP und Bildschirm-CSS.
- Browserprüfungen verwenden ausdrücklich gewählte aktuelle Server. Fixture-Tests schalten HMR und Live-Reload aus; Live- und Offline-Tests verlangen `FIRSTBLICK_LIVE_URL` und greifen nicht implizit auf einen alten Server zurück.

## Tatsächlich ausgeführte Prüfungen

| Prüfung | Ergebnis | Genaue Einordnung |
|---|---:|---|
| Vollständiger Serverlauf | 251 Core + 126 HTTP-/PostgreSQL-/Worker-Fälle | Ein vollständiger Lauf, null Fehler und null übersprungen; Servercode seit Checkpoint 8 unverändert. |
| Angular-Unit-Suite | 45 / 45 | Zehn Testdateien, einschließlich exakter Dezimaldarstellung. |
| Vollständige Browser-Fixtures | 216 / 216 | Desktop, Tablet und Mobil; null übersprungen oder flaky. Vor dem letzten Collections-Guard und dem erklärenden Operations-Satz. |
| Collections nach dem letzten Guard | 27 / 27 | Zusätzliche gezielte Ausführung derselben Collections-Testidentitäten, keine Addition zu 216 unterschiedlichen Fällen. |
| Neue echte Finanz-/Mengenketten | 12 / 12 | Rechnungsfolge, Nachkalkulation, Mengenübernahme und CSV-/Exportablauf auf drei Viewports. |
| Neuer echter Forderungsablauf | 3 / 3 | Historische synthetische Ausgangsbelege, echte Zahlung, veraltete Vorschau abgewiesen und neuer Entwurf genehmigt. |
| Betriebsübersicht, Sanierung, Portal und Discovery | 12 / 12 unterschiedliche Kombinationen | Drei anfänglich erfolgreiche Desktopfälle, drei korrigierte Sanierungsfälle und sechs verbleibende Tablet-/Mobilfälle in getrennten Läufen. |
| Echter Offline-Arbeitstag | 3 / 3 | Vollständiger Browserneustart, persönliche Wiederanmeldung, genau zwei Übertragungen, Idempotenz und Offline-Logout. |
| Finaler Produktionsbuild und HTTP-Assetcheck | Bestanden | Das tatsächlich ausgelieferte HTML stimmt mit dem finalen Build überein; normales Bildschirm-CSS und unveränderte CSP geprüft. |

Damit sind **30 unterschiedliche echte Browserkombinationen** aus zehn logischen Abläufen und drei Ansichten erfolgreich belegt: 27 online und drei offline. Es handelt sich um getrennte Läufe. Frühere fehlgeschlagene Versuche zählen nicht dazu. Die Prüfungen verwendeten synthetische persönliche Konten, eine neue isolierte PostgreSQL-16.15-Datenbank und einen neuen versionierten SeaweedFS-4.48-Bucket. Browsergröße und Gerätesimulation ersetzen keine physische Geräteabnahme.

Die Bilder der neuen Finanz-/Mengenketten, Forderungsbearbeitung und Offline-Abschlüsse wurden für alle drei Ansichten visuell geprüft. Die ergänzenden Operations-/Dokumentenbilder zeigen korrekt geladene Styles und passende mobile Umbrüche. PDF-Rechnungsseiten mit Netto- und Steuerübertrag wurden gerendert und geprüft.

Der finale Index hat SHA-256 `36745aac6b693ba6a889b36812328502aadeda7a3fa096e31a4f2f065a5b7c23`, das Service-Worker-Manifest `ab379f57a70347bce7c23e10b830c8f5a3457ba22dd615bfa6b3d831bb61f6cd`. Nach dem erfolgreichen neuen 12er-Live-Lauf änderte sich ausschließlich der erklärende Operations-Satz. Die übrigen echten Abschlussläufe und der Assetcheck nutzten den finalen Build.

## Roadmap und verbleibender Umfang

Die [Nachweismatrix](roadmap-evidence.md) ordnet den Fortschritt insbesondere DP-02-07, DP-05-06, DP-06-08, DP-08-01/02/04/08, DP-12 und DP-13 zu. Der nun durch Bedienung und echte Fälle belegte Kostenvergleich und die eigenständige Forderungsbearbeitung erhalten **K – technischer Kern belegt**. Die Matrix umfasst damit **49 K, 41 T und 10 O**. Die originalen 100 Aufgaben und ihre Abnahmekriterien bleiben erhalten; diese Zählung ist kein Fertigstellungsprozentsatz.

Ein Null-Euro-Mengenabschluss, zusätzliche Steuer-/E-Rechnungsprofile und die fachliche Buchhaltungsabnahme bleiben offen. Die anfänglichen Ausgabeabfragen für Rechnungen laden weiterhin mandantenweite Beleglisten. Kostenberichte werden nicht als dauerhafte Forecast-Versionen gespeichert; eine Eingangsbelegzuordnung wird nicht projektübergreifend reserviert. Forderungsentwürfe besitzen keinen automatischen Versand und keine rechtliche Gebühren-/Zinsberechnung.

Für den Betrieb fehlen weiterhin repräsentative Lastnachweise, externer Metrikexport, verteiltes Tracing, Alarmwege und vereinbarte SLOs. Die technische Roadmap benötigt reale Altbestände, freigegebene Providerverträge und lizenzierte Norm-/Herstellerdaten samt unabhängiger Referenzprüfung. Sechs verfügbare Demo-Installer wurden statisch inventarisiert; Flachdach- und Steildach-Demo sind in der aktuellen Übertragung weiterhin nicht verfügbar. Das [Inventar](evidence/attachment-inventory-2026-10-20.json) trennt diese Beobachtung vom älteren historischen Flachdach-Befund.
