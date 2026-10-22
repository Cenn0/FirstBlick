# Aufgabenabhängigkeiten

## Umfang in DP-04-03 und DP-04-04

Der Arbeitsplatz `/task-dependencies` ergänzt bestehende Projektaufgaben um ausdrücklich gewählte unmittelbare Vorgänger. Pro Aufgabe sind bis zu 16 Vorgänger möglich; ein Projektgraph umfasst höchstens 1.000 Aufgaben. Die Auswahl enthält nur Aufgaben desselben Projekts und Betriebs. Selbstbezüge, doppelte Kennungen, archivierte oder fehlende Quellen und indirekte Kreise werden abgewiesen.

Sobald ein Projekt Abhängigkeiten enthält, dürfen neue Aufgaben, Projektwechsel und Vorlagenübernahmen seine Aufgabenanzahl nicht über 1.000 erhöhen. Vorlagen werden als vollständiger Batch vor dem Speichern geprüft. Bestehende Aufgaben bleiben bearbeitbar; für Altprojekte ohne Abhängigkeiten entsteht keine neue Mengenbeschränkung.

Eine Aufgabe kann erst in **In Bearbeitung** oder **Erledigt** wechseln, wenn alle ihre Vorgänger **Erledigt** sind. **Entfallen** erfüllt diese Bedingung nicht. Eine nachträgliche Rücknahme eines erledigten Vorgängers wird gesperrt, solange eine davon abhängige Aufgabe begonnen oder abgeschlossen ist. Die Prüfung gilt ebenso für generische Aufgabenbearbeitung und direkte HTTP-Aufrufe. Referenzierte Vorgänger können nicht archiviert werden.

Die Vorgängerliste wird nur bei **offenen** Aufgaben geändert. Eine notwendige Umplanung erfolgt bewusst von den Folgeaufgaben zurück: deren Status auf offen zurücknehmen, Abhängigkeiten prüfen und danach die Vorgänger ändern. Termine, Personaleinsätze und Projektvorlagen werden nicht automatisch umgeplant; Aufgaben ohne Abhängigkeiten behalten ihre bisherige Bearbeitung.

## Bedienung und Rechte

1. Ein Projekt auswählen und seinen vollständigen Aufgabenstand laden.
2. Die gewünschte offene Aufgabe auswählen und unmittelbare Vorgänger markieren.
3. **Abhängigkeiten speichern** übernimmt die Auswahl mit der angezeigten Aufgabenrevision und lädt den Graph erneut.
4. Offene Vorgänger sind einzeln sichtbar. Der Aufgabenstatus kann nach ihrer Erledigung geändert werden.

Geschäftsführung, Büro und Bauleitung können gemäß den bestehenden Aufgabenrechten ändern. Die Buchhaltung kann lesen. Die projektweite Übersicht steht persönlichen Feldrollen nicht offen: Eine einzelne zugewiesene Aufgabe gibt ihnen keinen zusätzlichen Zugriff auf andere Projektaufgaben. Die bisherigen persönlichen Aufgabenansichten bleiben erhalten.

Nach Rechte- oder Betriebswechsel werden Daten und Auswahl verworfen. Verspätete Antworten werden nicht im neuen Kontext angezeigt. Bei Konflikten oder unklaren Antworten sperrt die Maske weitere Änderungen bis zum erneuten Laden. Eine erfolgreiche lokale Eingabe allein wird nicht als gespeicherter Serverstand behandelt.

## HTTP- und Datenvertrag

| Zugriff | Vertrag |
|---|---|
| `GET /api/v1/projects/{id}/task-dependencies` | Begrenzter konsistenter Projektgraph mit Kennungen, Titeln, Status, Revisionen und Vorgängerlisten. |
| `PUT /api/v1/tasks/{id}/dependencies` | Explizite `predecessorTaskIds`; `If-Match`, aktuelle Rechte und `Idempotency-Key` erforderlich. |
| `POST /api/v1/tasks/{id}/dependency-status` | Expliziter Aufgabenstatus unter derselben Graphprüfung. |

Neue Kanten liegen in `TaskItem.PredecessorTaskIds` und den gewöhnlichen mandantengebundenen Fremdschlüsseln. Alte Aufgaben ohne das Feld werden unverändert deserialisiert; bei fehlendem Wert wird es nicht in historische JSON-Digests hineingeschrieben. Generische ältere Editoren behalten vorhandene Kanten bei und dürfen sie nicht unbemerkt entfernen. Die kanonische Datenübernahme prüft Kanten und Projektgraphen ebenfalls.

Alle Änderungen verwenden die bestehende transaktionale Betriebssperre: Graphprüfung, gespeicherte Aufgabe, Referenzen, Audit, Outbox und Replaybeleg gehören zu derselben Transaktion. Gleichzeitiges Eintragen gegenläufiger Kanten kann deshalb keinen Kreis festschreiben. Auch die Graphlesung hält diese Sperre kurz und prüft die Mitgliedschaft nach einem möglichen Warten erneut mit `ReadCommitted`. Replay prüft aktuelle Rollen und Modulfähigkeiten erneut.

## Prüfung und verbleibende Grenzen

Corefälle prüfen Statusfolgen, Rücknahmen, indirekte Kreise, Quellen, Grenzen und Kompatibilität älterer Editoren. Die API-Suite verwendet echtes HTTP, Identity und PostgreSQL für gleiche/andere Mandanten, Referenzen, Revisionskonflikte, Replay, aktuelle Rechte und gleichzeitig eingetragene gegenläufige Kanten. Clientfälle prüfen unvollständige Antworten; responsive Browser-Fixtures prüfen Auswahl, Sperren, Konflikte und verspätete Antworten mit kontrollierten HTTP-Antworten. Testdateien sind erst nach erfolgreicher Ausführung für den jeweiligen Quellstand ein Ausführungsnachweis.

Die synthetische Migrationsprobe enthält drei zusätzliche unabhängige Aufgaben mit der Kette **Erledigt → Erledigt → Offen**. Sie vergleicht Vorgängerkennungen, Revisionen und Status vor und nach der Übernahme. Vier veränderte Quellpakete prüfen gezielt Kreisbildung, einen Vorgänger in einem tatsächlich vorhandenen anderen Projekt, die Rücknahme eines benötigten Abschlusses und einen archivierten Vorgänger. Die Graphprüfung wird je Betrieb und Projekt einmal ausgeführt; auch fehlerhafte Ergebnisse werden für die weiteren Aufgabendatensätze wiederverwendet.

Dies ist eine End-to-start-Abhängigkeit zwischen vorhandenen Aufgaben. Mehrstufige Arbeitspakete, Vorlauf-/Nachlaufzeiten, kritischer Pfad, automatische Terminrechnung, Feiertagsbewertung, Ressourcenplanung und Erinnerungszustellung sind damit nicht umgesetzt. DP-04-03 und DP-04-04 werden deshalb nicht pauschal als vollständig erledigt umklassifiziert.
