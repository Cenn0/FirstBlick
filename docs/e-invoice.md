# E-Rechnung in FirstBlick – getrennte Vorprüfung, Originalvalidator und Geschäftskette

**Stand: 21.10.2026.** Die alte v1-Vorschau ist weiterhin lediglich eine strukturelle XML-Sichtung; sie ist keine XRechnung-Konformitätserklärung. Zusätzlich existiert eine eigenständige, versionsgebundene **v2-Validierung** und ein expliziter Ein- und Ausgangsworkflow.

## Verfahren und technische Grenzen

- `POST /api/v1/integrations/v1/e-invoice/preview`: strukturierte, begrenzte UBL-/CII-Sichtung, `externalValidationPerformed=false`. Weder Archivierung noch Buchung.
- `POST /api/v1/integrations/v2/e-invoice/validate`: Original-XML mit lokal gepinntem KoSIT-Validator 1.6.3 und XRechnung-Konfiguration 3.0.2 (2026-08-31), XSD und Geschäftsregeln. Prüfung endet mit eigenem Prüfbericht und quellgebundenem Digest; "XML lesbar" bedeutet nicht "angenommen".
- `.../e-invoice/import-preview` und `.../e-invoice/import`: abgenommene und ausdrücklich auf Lieferant, Rechnungsidentität und optionalen Bestellbezug abgebildete Rechnung; Speicherung als **Lieferantenrechnungsentwurf**, mit Original, Validatorbericht und menschlichem Prüfreceipt in getrennten versionierten Objekten. Der Import löst **keine Zahlung** aus.
- Lieferantenrechnung freigeben bzw. ablehnen: dedizierte Business-API; Banktransaktionen nur lesend einlesen, daraus abgeglichene Zahlungszuordnungen mit getrennten Vorzeichen-/Skonto- und Rückabwicklungsregeln vornehmen.
- `POST /api/v1/invoices/{id}/e-invoice/preview` und `.../export`: aus einer bereits ausgestellten und eingefrorenen Ausgangsrechnung wird nach menschlich geprüfter Steuer- und Empfängerzuordnung eine UBL-Ausgabe erzeugt, mit Originalregel-Validierung und versionierter Export-Evidenz. Kein automatischer Versand.

## Notwendige Einsatzvoraussetzungen

Versionierte Objektspeicherung, kontrollierter Java-Validatorprozess, geprüfte Original-XSD/Schematron-Ressourcen, aktive Mandantenberechtigungen, passende Geschäftspartner- und Steuerkategorien, realistische Steuer-/Gutschriftreferenzen und korrekte Rechnungsstammdaten sind erforderlich. Jede Freigabe verwendet einen Digest des tatsächlich gesehenen Prüfstands. Revisionswechsel und abweichende XML-Originalbytes erzwingen eine erneute Prüfung.

Das implementierte **XRechnung-UBL-Verfahren** deckt weder jede EN-16931-Syntaxvariante noch alle steuerlichen Spezialfälle ab. Insbesondere kein beliebiges ZUGFeRD-/Factur-X-PDF/A-3, keine automatische Peppol-Übertragung, keine DATEV-Schnittstellenzertifizierung und keine Steuerberater-/Archivierungsfreigabe. Technische Originalregelprüfung und rechtliche Konformität sind auseinanderzuhalten.

Die verbindlichen API-Verträge und Einschränkungen stehen in [erweiterte Austausch- und Bauphysik-Abläufe](advanced-exchange-and-physics.md). Die Original-Konfiguration und ihr festgelegter Hash werden in `scripts/standards-assets.py` gepflegt; die exakten Testläufe sind über GitHub Actions je Commit zu prüfen.

Offizielle Quelle: https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/tag/v2026-08-31
