# Projektvorlagen und nachvollziehbare Aufgabenübernahme

Stand: 22.10.2026 · FirstBlick · **DP-04-03 / DP-04-04**.

Der Arbeitsplatz **Projektvorlagen** verwaltet wiederverwendbare Aufgabenlisten für
Sanierung, Reparatur und Wartung. Eine Vorlage enthält einen Namen, eine Beschreibung,
den Projekttyp und eine geordnete Liste aus 1 bis 50 Aufgaben. Jede Aufgabe besitzt
Titel, Beschreibung und einen Abstand in **Kalendertagen** zum gewählten Bezugsdatum.
Negative Abstände erlauben vorbereitende Aufgaben vor dem Projektbeginn.

## Vorlage prüfen und übernehmen

1. Eine aktive Vorlage und ein vorhandenes, offenes Projekt desselben Typs auswählen.
2. Den geplanten Projektstart verwenden oder ein ausdrückliches Bezugsdatum eingeben.
3. Die Vorschau mit allen konkreten Fälligkeiten prüfen.
4. Die Übernahme bewusst bestätigen. Der Server speichert sämtliche Aufgaben,
   den unveränderlichen Anwendungsnachweis, die neue Projektrevision, Audit- und
   Idempotenzbelege in einer gemeinsamen Datenbanktransaktion.

Die neuen Aufgaben sind **offen und keiner Person zugewiesen**. Die normale
Aufgabenverwaltung erlaubt anschließend Zuständigkeiten, Bearbeitung und Abschluss.
Die Vorlage erzeugt weder Personaleinsätze noch Materialreservierungen, Angebote oder
Aufträge. Wochenenden und Feiertage werden als gewöhnliche Kalendertage gezählt;
eine Arbeitskalender- oder Abhängigkeitsplanung ist damit nicht enthalten.

Beispiel: Bei Bezugsdatum **28.02.2028** führen die Abstände **−2, 0, +1, +3** zu
**26.02., 28.02., 29.02. und 02.03.2028**. Abstände sind auf −3650 bis +3650 Tage
begrenzt. Ein Ergebnis außerhalb des unterstützten Datumsbereichs wird vollständig
abgewiesen. Ohne geplanten Projektstart ist ein ausdrückliches Bezugsdatum erforderlich.

## Vorschau, Revisionen und Originalerhalt

Die Vorschau hält die damalige Vorlage und das damalige Projekt einschließlich ihrer
Revisionen und Digests fest. Eine Änderung an einer Quelle verlangt eine neue
Vorschau. Der Bestätigungsbefehl prüft Projekt- und Vorlagenrevision, Bezugsdatum,
Vorschaudigest, aktuellen Betriebszugriff und die zulässigen Projektzustände erneut.
Abgeschlossene, abgesagte oder archivierte Projekte sowie archivierte Vorlagen sind
für eine neue Anwendung gesperrt.

Dieselbe Vorlage lässt sich **höchstens einmal je Projekt** anwenden, auch nach einer
späteren Vorlagenänderung oder mit einem anderen `Idempotency-Key`. Ein identischer
Request-Key wiederholt bei weiterhin ausreichender Berechtigung das ursprüngliche
Ergebnis, ohne neue Aufgaben zu erzeugen. Ein Datenbank-Schlüssel ergänzt diese
Regel gegen konkurrierende Übernahmen.

Der Anwendungsnachweis speichert Quelle, konkrete Fälligkeiten, erzeugte Aufgaben-
kennungen, ursprüngliche Reihenfolge, erfassende Person und Zeitpunkt. Jede erzeugte
Aufgabe trägt eine geschützte Herkunft mit Anwendungskennung und Digest. Spätere
Aufgabenänderungen oder die Archivierung einer Aufgabe verändern diesen Nachweis
nicht. Die Herkunft lässt sich weder entfernen noch einem anderen Projekt zuordnen.

Vorlagen können für künftige Projekte geändert, archiviert und wiederhergestellt
werden. Ihre früheren Anwendungen bleiben unverändert und lesbar. Allgemeine
JSON-Endpunkte dürfen weder Anwendungen erzeugen noch Herkunftsnachweise fälschen.
Bestehende Aufgaben ohne Vorlagenherkunft behalten ihre bisherige JSON-Darstellung.

## Berechtigungen und Schnittstellen

Geschäftsführung, Büro und Bauleitung verwalten und übernehmen Vorlagen. Buchhaltung
kann Vorlagen und Anwendungshistorien lesen. Mitarbeitende, Ausbilder und externe
Partner erhalten keinen Zugriff auf die vollständigen Vorlagen- und Anwendungsakten.
Ihre bestehenden projektbezogenen Aufgabenrechte bleiben maßgeblich. Die Funktion
verwendet das Modul `core`; der Server prüft zusätzlich Projekt- und Aufgabenrechte.

| Methode und Pfad | Zweck |
|---|---|
| `GET /api/v1/project-templates?search=&page=1&includeArchived=false` | Suche mit 20 Vorlagen je Seite. |
| `GET /api/v1/project-templates/{id}` | Vollständige Vorlage mit Revision und Archivstatus. |
| `POST /api/v1/project-templates` | `{name,description,projectType,tasks:[{title,description,offsetDays}]}`. |
| `PUT /api/v1/project-templates/{id}` | Vorlageninhalt ändern; `If-Match` mit Vorlagenrevision. |
| `POST /api/v1/project-templates/{id}/archive` | `{archived:true/false}`; Vorlagenrevision erforderlich. |
| `POST /api/v1/projects/{id}/template-preview` | `{templateId,templateRevision,anchorDate?}`; Projektrevision erforderlich, keine Aufgabenanlage. |
| `POST /api/v1/projects/{id}/template-apply` | Dieselben Felder plus `previewDigest`; Projektrevision und Idempotenzschlüssel erforderlich. |
| `GET /api/v1/projects/{id}/template-applications?page=1` | Anwendungshistorie mit 20 Einträgen je Seite. |
| `GET /api/v1/project-template-applications/{id}` | Unveränderlicher vollständiger Anwendungsnachweis. |

## Verifikation und weiterer Umfang

[Core](../src/FirstBlick.Core/Business/ProjectTemplates.cs),
[API](../src/FirstBlick.Api/ProjectTemplateEndpoints.cs) und die jeweiligen
Core-/HTTP-/PostgreSQL-/Client-/Browserfälle prüfen Quellenbindung, Grenzen,
Mehrfachübernahme, Mandanten, Rollen, Revisionen und verspätete UI-Antworten.

Die [Migrationsprüfung](../tools/FirstBlick.Operations/MigrationGraph.cs) prüft beide
Richtungen der Aufgabenherkunft: Jede erzeugte Aufgabe gehört zur festgehaltenen
Anwendung und jede dort aufgeführte Aufgabe muss tatsächlich vorhanden sein. Der
[Migrationsdrill](../tools/FirstBlick.Operations/MigrationProjectTemplates.cs) erhält
auch später bearbeitete oder archivierte Aufgaben, geänderte Vorlagen und historische
Akteure. Negative Varianten prüfen unter anderem fehlende Aufgaben, vertauschte
Positionen, entfernte Herkunft, veränderte Digests und doppelte Anwendungen.

Der [Container-/Neustartablauf](../scripts/project_templates_originals_smoke.py)
verwendet die tatsächliche API und Datenbank. Er führt eine geprüfte Übernahme aus,
ändert danach eine Aufgabe und die Vorlage und vergleicht nach dem Neustart dieselben
Originale. Browser-Fixtures verwenden kontrollierte HTTP-Antworten; diese Ergebnisse
sind von den tatsächlichen Infrastrukturprüfungen zu unterscheiden. Maßgeblich sind
die erfolgreichen Prüfläufe des veröffentlichten Commits.

Mehrstufige Arbeitspakete, Aufgabenabhängigkeiten, automatische Erinnerungen,
Arbeitskalender sowie eine umfassende betriebliche Projektorganisation bleiben
weiterer Umfang von DP-04-03/04. Die bestehenden ursprünglichen Roadmap-Kriterien
werden durch diesen Teilablauf nicht pauschal als erledigt markiert.
