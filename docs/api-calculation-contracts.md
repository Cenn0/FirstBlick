# Versionierter OpenAPI-Mindestvertrag der Rechenvorschauen

DP-02-02/DP-12-04 erhalten einen begrenzten, maschinengeprüften Vertrag für **18 technische Rechenvorschauen**. Die [Liste der Pfade, Schemaidentitäten und wesentlichen Ergebnisfelder](contracts/technical-calculations-v1.json) ist versioniert. Sie umfasst die existierenden Wärme-, Feuchte-, Dachgeometrie-, Gefälle-, PV-, Wind-, Schnee- und Abflussvorschauen. Die Serverberechnung und ihre fachlichen Grenzen bleiben unverändert.

## Bereitstellung und Fehler

`GET /openapi/v1.json` benötigt weiterhin persönliche Authentifizierung. Jeder erfasste POST enthält ein erforderliches JSON-Eingabeschema, einen stabilen Operationsnamen und sein konkretes JSON-Ergebnisschema. Die geschützten Rechenendpunkte benötigen zusätzlich die aktuelle Betriebsmitgliedschaft, `X-Tenant-Id`, CSRF und `technical-results.write`.

Die Metadaten beschreiben 400, 403, 413, 415, 423 und 500 als Problem-Details mit `code` und `traceId`. 401 kann ohne Antwortkörper zurückgegeben werden. Ein `RouteHandlerOptions.ThrowOnBadRequest = true` hält auch fehlerhafte Minimal-API-JSON-/Medientypbindung außerhalb der Entwicklungsumgebung auf dem bestehenden ErrorMiddleware-Pfad: Status und generischer Problemtext bleiben erhalten; rohe Eingaben und innere Parserdetails werden nicht ausgegeben. Eine vor der Anwendung abgewiesene Proxy-/Serveranfrage kann weiterhin eine andere Fehlerform haben.

Die Herstellergrundlage für diese Mechanismen ist Microsofts [OpenAPI-Metadatendokumentation für ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0) und [ThrowOnBadRequest-Referenz](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.routing.routehandleroptions.throwonbadrequest?view=aspnetcore-10.0), geprüft am 23.10.2026. Der Build belegt technische Verwendbarkeit im gepinnten Projekt; die Dokumentation belegt die offizielle Mechanik. Daraus folgt keine vollständige Empfehlung oder Freigabe sämtlicher API- oder Normverfahren.

## Prüfungen und Grenzen

[TechnicalOpenApiTests](../tests/FirstBlick.Api.Tests/TechnicalOpenApiTests.cs) lädt das tatsächlich ausgelieferte authentifizierte OpenAPI-Dokument über HTTP und prüft alle 18 Mindestverträge. Der gleiche Prüfpfad kontrolliert fehlerhaftes JSON, falsche Typen und einen falschen Medientyp in der nicht als Development konfigurierten Testanwendung. Ein Canary im Rohtext darf nicht in der Fehlerantwort erscheinen.

[AdvancedTechnicalWireApiTests](../tests/FirstBlick.Api.Tests/AdvancedTechnicalWireApiTests.cs) gleicht sechs Client-Fixtures mit den echten HTTP-Ausgaben und der Projektablage ab. Das schließt die wichtige Unterscheidung ein: Der typisierte HTTP-Preview verwendet den vorhandenen Dezimalstring-Converter, während eingebettete technische Ergebnis-JSON Geldwerte als Zahlen bewahrt. Der Client vergleicht nur diese ausdrücklich benannten Geldfelder semantisch centgenau; Quellen und Maße werden nicht allgemein umgewandelt. Für diese Prüfung ist echtes PostgreSQL verpflichtend, sobald CI `FIRSTBLICK_REQUIRE_POSTGRES=true` setzt.

Der Mindestvertrag ist **keine vollständige OpenAPI-Kompatibilitätsprüfung der gesamten Anwendung**. Er prüft weder sämtliche verschachtelten Einschränkungen und Nullfähigkeiten noch alle CRUD-/Finanz-/Datei-/Webhookverträge. Die übrigen Endpunkte, externe Ereigniszustellung und eine vereinbarte Kompatibilitätspolitik bleiben offen; beide Roadmap-Aufgaben bleiben T. Metadaten allein erzwingen keine Laufzeitkonformität. Die getrennten HTTP-Regressionsfälle und finalen CI-/Operations-Prüfungen sind daher erforderlich.
