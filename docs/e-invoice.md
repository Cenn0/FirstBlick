# E-Rechnungen – sicherer XML-Previewvertrag, Profilvalidierung noch ausstehend

Stand: 20.10.2026. Die vorbereitete Empfangskomponente kann untrusted XML mit begrenzten Budgets, DTD-/XXE-Sperre und enger Typ-/Versionsabgrenzung lesen. Ihr Ergebnis ist **niemals** eine Freigabe zur Buchung, zum Versand oder zur steuerlichen Verwendung.

## Unterstützte Vorprüfung

`POST /api/v1/integrations/v1/e-invoice/preview` erwartet `application/xml` oder `text/xml`, UTF-8, höchstens 1 MiB. Der Endpoint fordert eine gültige persönliche Anmeldung, Mandantenmitgliedschaft und eine Finanzrolle (Geschäftsführung, Büro oder Buchhaltung).

- UBL 2.1: `Invoice` und `CreditNote` in den OASIS-Namensräumen mit eindeutigen Positionskennungen, expliziter Rechnungssumme, Nettobasis, Zahlbetrag, Währung EUR, Datum und beiden Geschäftspartnern.
- UN/CEFACT CII: ein `CrossIndustryInvoice`-XML mit Typ 380/381, Kopfdaten, Waren-/Leistungszeilen sowie den Geldsummen. Die Typbenennung `CII-D16B-structure` ist eine *Strukturerkennung*, kein getesteter Release-Konformitätsnachweis.
- Die Vorschau bindet Original-Bytes mit SHA-256, erhält Mengen, Währung, Beträge und Profilkennung ohne eine künstliche umsatzsteuerliche Klassifizierung.
- Sie zeigt fehlende oder fragliche Kopf- und Positionsangaben als `reviewIssues`. `externalValidationPerformed=false` und `mayPostAutomatically=false` sind ausnahmslos fest vorgegeben.

Dateiendungen oder XML-Namensräume genügen **nicht** für eine XRechnung-Freigabe. Eingebettete XML-Anhänge aus ZUGFeRD-/Factur-X-PDF/A-3, XML-Signaturen, XML-Extensions, Lieferantenmatching, Dublettenbehandlung, Herkunftsbelege und das sichere Buchen von Eingangsbelegen werden nicht behauptet oder stillschweigend ausgeführt.

## Stand der Standards

- Der UBL-/CII-Formalvertrag zur europäischen EN 16931 verlangt vollständige XSD- und Schematron-Regeln; ein korrekt parsebares Dokument ist nicht notwendig geschäftsregelkonform.
- KoSIT veröffentlicht eine **XRechnung-3.0.2**-Validator-Konfiguration mit Datum **2026-08-31**, KoSIT Validator 1.6.3 und den zugehörigen CEN-/CIUS-Regeln. Referenz: https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/tag/v2026-08-31
- Die gemeinsame ZUGFeRD-/Factur-X-Formatfamilie erhielt **ZUGFeRD 2.5.2 / Factur-X 1.09.2** im August 2026. Referenz: https://fnfe-mpe.org/factur-x/factur-x_en/
- Für künftig reale XRechnung-Buchung und strukturierten Versand ist die isolierte Ausführung **der gepinnten KoSIT-Konfiguration inklusive XSD/Schematron**, ein Profil-/Codelisten-Testkorpus, validierte Netto-/Steuerbasis, Pflichtfeld- und Empfängerprüfung sowie Geschäftsfreigabe erforderlich. Das darf nicht mit dem heutigen Preview-Endpoint verwechselt werden.

## Technische Grenzen und Testbelege

`src/FirstBlick.Core/Integrations/TradeXml.cs` sperrt DTD/Entity-Resolution, begrenzt XML-Größe, Elementzahl und Tiefe. `ElectronicInvoiceXml.cs` verwaltet die Syntaxerkennung und sichere Vorschau mit invariantem Dezimalformat. Der HTTP-Adapter ist `src/FirstBlick.Api/TradePreviewEndpoints.cs`.

Core-Regressionsfälle: `tests/FirstBlick.Core.Tests/Integrations/ElectronicInvoiceXmlTests.cs`; echte HTTP-/PostgreSQL-Fälle: `tests/FirstBlick.Api.Tests/ElectronicInvoicePreviewApiTests.cs`. Eine erfolgreiche CI ist keine Prüfung mit dem externen KoSIT-Validator und keine fachrechtliche Abnahme. **DP-08-03 ist bis zur vollständigen, versionsgeprüften Import-/Exportkette nur teilweise umgesetzt.**
