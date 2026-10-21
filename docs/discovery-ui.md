# Suche, Projektverlauf und eigene Datenkopie

Die Seite `/discovery` und der Abschnitt „Meine Datenkopie“ unter `/settings` verwenden die mandanten- und berechtigungsgebundenen [Discovery-APIs](discovery-data-copy.md). Die Anzeige folgt den aktuellen Zugriffsrechten des persönlich angemeldeten Kontos. Antworten werden weder als Offline-Geschäftsdaten gespeichert noch durch den Service Worker zwischengespeichert.

## Suche und Projektverlauf

Die Suche akzeptiert zwei bis 200 Zeichen und einen optionalen fachlichen Bereich. Gesucht werden ausschließlich die vom Server freigegebenen Bezeichnungen, Titel und Nummern. Ein Suchtreffer enthält keine privaten Notizen, Personal- oder Bankinhalte. Die Oberfläche fordert ausdrücklich Seiten mit jeweils 25 Treffern an; weitergeblättert wird anhand der serverseitig berechtigungsgefilterten Gesamtzahl. Bereichsauswahl und Treffer folgen den aktuellen Rechten. Eine leere Seite erklärt, dass nur zugängliche Datensätze erscheinen.

Projekttreffer und Projektakten führen zu `/discovery?projectId=<id>`. Der Verlauf nennt generische Ereignistitel, Zeitpunkt, Datensatzart und Revision. Die Kennzeichnung „Von Ihnen“ enthält keine fremde persönliche Identität. Verweise öffnen die vorhandene Datensatzansicht. Der Verlauf erläutert ausdrücklich seine Grenze: Er folgt den heutigen Projektzuordnungen; frühere Projektzuordnungen einer inzwischen verschobenen Aufgabe werden nicht rekonstruiert. Angezeigte Zeitpunkte verwenden die Gerätezeitzone.

Bei entzogenen Rechten, fehlenden Projekten, einem ausgeschöpften Lesebudget oder einem echten API-Fehler entfernt die Oberfläche zuvor angezeigte Ergebnisse und zeigt den konkreten Fehler beziehungsweise den passenden Leerzustand. Sie ersetzt einen fehlgeschlagenen Zugriff nicht durch alte Treffer. Ein Wechsel des Betriebs, eine geänderte Zugriffssignatur oder ein Seitenwechsel macht ausstehende Antworten unwirksam.

## Meine Datenkopie

Der Abschnitt hat keine Auswahl einer fremden Person oder eines anderen Datengegenstands. Er beschreibt die tatsächlich erfassten Kategorien: eigene Kontodaten sowie aktuell lesbare eigene Zeiten, Tagebucheinträge, Formularantworten und Benachrichtigungsregeln des ausgewählten Betriebs. Die Begrenzung auf diese Self-Service-Kopie und die ausgeschlossenen Anhänge, Archive und sonstigen Geschäftsunterlagen stehen unmittelbar am Downloadknopf. Eine vollständige datenschutzrechtliche Auskunft wird damit nicht behauptet.

Vor dem Download kontrolliert der gemeinsame Exporthelfer den ZIP-Inhaltstyp, den Versionsheader `firstblick.self-data-copy/1.0.0` und den SHA-256 der tatsächlich empfangenen ZIP-Bytes. Erst dann entsteht der lokale Download. Der angezeigte Dateiname ist fest vorgegeben und enthält das Download-Datum; eine Detailanzeige nennt die kontrollierte Prüfsumme. Ein falscher Hash erzeugt eine Fehlermeldung und keinen Download. Betriebswechsel und tatsächlich abgeschlossene Navigation unterdrücken auch eine später eintreffende Antwort.

Die Oberfläche entpackt die ZIP-Datei nicht. Der Server liefert `profile.json`, `membership.json`, `records.json` und `manifest.json`; die Integrität der drei Datenfiles steht im Manifest. Die zusätzliche unabhängige Kontrolle dieser internen Dateiprüfsummen gehört zum beschriebenen Live-Test.

## Ausgeführte Nachweise

Der [Discovery-Browsernachweis](evidence/live-discovery-checkpoint7.json) enthält zwei getrennte abgeschlossene Läufe auf Desktop 1440 × 1000, Tablet 1024 × 768 und Mobil 390 × 844:

- **18 Browser-Fixtures** mit kontrollierten API-Antworten prüfen wörtliche Suchbegriffe, Seitenwechsel, Rechte-/Leerzustände, 403/404, späte Suchantworten nach Betriebswechsel, erlaubte ZIP-Downloads, falsche ZIP-Hashes und späte Downloadantworten nach Betriebs- und Seitenwechsel.
- **Drei echte Browserabläufe** verwenden persönlich angemeldete synthetische Konten und echte HTTP-/PostgreSQL-Antworten ohne API-Interception. Die Einrichtung von Kunde, Objekt, Projekt, Aufgabe und einer eigenen inaktiven Benachrichtigungsregel erfolgt über authentifizierte HTTP-Befehle. Suche, Projektverlauf und Datenkopie werden anschließend ausschließlich durch die gerenderte Angular-Oberfläche bedient. Private Notizinhalte liefern keinen Suchtreffer. Der Projektverlauf zeigt die echten allgemeinen Erstellungsereignisse. Die heruntergeladene ZIP-Datei stimmt mit dem Antwortdigest überein; alle Manifest-Dateigrößen und -prüfsummen werden unabhängig geprüft. Das Profil enthält nur die vorgesehenen eigenen Kontofelder und die eigene Benachrichtigungsregel ist enthalten.

Alle drei echten Fälle enthalten vier Dateien und einen eigenen Datensatz. Die vollständige Vier-Kategorien-Auswahl, konkurrierender Rechteentzug unter beobachtetem Datenbanksperr-Warten und die Ablehnung eines Archivs über dem Budget sind zusätzlich durch die elf echten [API-/PostgreSQL-Fälle](evidence/discovery-checkpoint7.json) belegt. Diese Bereiche werden nicht als durch einen einzelnen Live-Browserfall vollständig geprüft ausgegeben.

Die Browserberichte nennen Chromium-, Node- und Quellversionen, Prüfsummen und Ergebnisse; sie enthalten keine Passwörter oder Sitzungsdaten. Die echten Browserläufe verwenden den lokalen Angular-Entwicklungsserver. Der produktive Service-Worker-/Offline-Nachweis bleibt ein [eigenständiger Testumfang](evidence/live-offline-checkpoint7.json). Physische Endgeräte, Speicherverlust und eine betriebliche Datenschutzabnahme sind dadurch nicht nachgewiesen.
