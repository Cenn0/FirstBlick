/**
 * User-facing German messages for API Problem Details.
 * The API code stays machine-readable; arbitrary server details and stack traces
 * must not be copied into the UI. Keep the mapping independent of Angular.
 */
export interface ErrorProblem {
  code?: string;
  errors?: Record<string, string[]>;
}

const codeMessages: Readonly<Record<string, string>> = {
  recovery_hold: 'Dieser Betrieb ist nach einer Wiederherstellung vorübergehend gesperrt. Änderungen sind erst nach Freigabe durch die Betriebsverwaltung wieder möglich.',
  migration_activation_required: 'Dieses übernommene Konto benötigt eine neue persönliche Einladung. Erstellen Sie unter „Persönliche Zugänge“ eine Einladung an dieselbe E-Mail-Adresse. Anschließend kann die Person ihren Zugang aktivieren.',
  zero_invoice: 'Nach der Rundung bleibt kein Rechnungsbetrag offen. Ein Abschluss über 0 € wird derzeit nicht unterstützt. Prüfen Sie die Korrektur und stellen Sie die Rechnung gegebenenfalls neu aus. Es wurde nichts gebucht.',
  approval_required: 'Für diesen Vorgang ist eine zweite Freigabe erforderlich. Lassen Sie ihn durch eine berechtigte Person freigeben.',
  approval_invalid: 'Die Freigabe ist nicht mehr gültig. Bitte lassen Sie den aktuellen Vorgang erneut freigeben.',
  revision_required: 'Der gespeicherte Stand konnte nicht geprüft werden. Bitte laden Sie den Eintrag neu und versuchen Sie es erneut.',
  revision_conflict: 'Der Eintrag wurde zwischenzeitlich geändert. Bitte laden Sie ihn neu und vergleichen Sie Ihre Angaben.',
  concurrency_conflict: 'Der Eintrag wurde zwischenzeitlich geändert. Bitte laden Sie ihn neu und vergleichen Sie Ihre Angaben.',
  stale_revision: 'Der Eintrag wurde zwischenzeitlich geändert. Bitte laden Sie ihn neu und vergleichen Sie Ihre Angaben.',
  concurrent_change: 'Gleichzeitig wurde eine Änderung vorgenommen. Bitte prüfen Sie den aktuellen Stand, bevor Sie den Vorgang erneut ausführen.',
  idempotency_conflict: 'Diese Anfrage wurde bereits mit anderen Angaben verwendet. Bitte aktualisieren Sie die Seite und starten Sie den Vorgang neu.',
  idempotency_required: 'Die Anfrage konnte nicht eindeutig zugeordnet werden. Bitte laden Sie die Seite neu und versuchen Sie es erneut.',
  project_template_revision: 'Die Projektvorlage wurde geändert. Laden Sie sie neu und prüfen Sie Ihre Auswahl.',
  project_template_preview_changed: 'Die Vorschau der Projektvorlage ist nicht mehr aktuell. Bitte erstellen und prüfen Sie eine neue Vorschau.',
  project_template_already_applied: 'Diese Projektvorlage wurde bereits angewendet. Prüfen Sie das Projekt, bevor Sie fortfahren.',
  gaeb_preview_changed: 'Die GAEB-Daten haben sich seit der Vorschau geändert. Bitte prüfen und bestätigen Sie eine neue Vorschau.',
  activity_report_changed: 'Die Daten des Tätigkeitsberichts haben sich geändert. Bitte erstellen Sie den Bericht erneut.',
  stale_cost_report: 'Die Kostenauswertung ist nicht mehr aktuell. Bitte berechnen und prüfen Sie sie erneut.',
  duplicate: 'Ein Eintrag mit diesen Angaben oder dieser Nummer existiert bereits. Bitte prüfen Sie vorhandene Einträge.',
  invalid_reference: 'Ein benötigter zugehöriger Eintrag ist nicht mehr verfügbar. Bitte aktualisieren Sie die Auswahl.',
  not_found: 'Der angeforderte Eintrag wurde nicht gefunden. Er wurde möglicherweise entfernt. Bitte aktualisieren Sie die Ansicht.',
  tenant_required: 'Bitte wählen Sie zuerst einen Betrieb aus.',
  forbidden: 'Sie haben für diese Aktion keine Berechtigung. Wenden Sie sich bei Bedarf an die Betriebsverwaltung.',
  module_disabled: 'Diese Funktion ist für Ihren Betrieb nicht aktiviert. Bitte wenden Sie sich an die Betriebsverwaltung.',
  unauthorized: 'Sie sind nicht angemeldet oder Ihre Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.',
  csrf_invalid: 'Die Sicherheitsprüfung Ihrer Sitzung ist abgelaufen. Bitte laden Sie die Seite neu und versuchen Sie es erneut.',
  invalid_json: 'Die übermittelten Angaben konnten nicht gelesen werden. Bitte prüfen Sie Ihre Eingaben und versuchen Sie es erneut.',
  invalid_input: 'Mindestens eine Angabe ist ungültig. Bitte prüfen Sie Ihre Eingaben.',
  invalid_request: 'Die Anfrage konnte nicht verarbeitet werden. Bitte prüfen Sie Ihre Eingaben und versuchen Sie es erneut.',
  payload_too_large: 'Die übermittelten Daten sind zu groß. Bitte wählen Sie eine kleinere Datei oder reduzieren Sie den Umfang.',
  provider_unconfigured: 'Die benötigte externe Anbindung ist noch nicht eingerichtet. Bitte wenden Sie sich an die Betriebsverwaltung.',
  storage_unconfigured: 'Die Speicherung ist derzeit nicht eingerichtet. Bitte wenden Sie sich an die Betriebsverwaltung.',
  server_error: 'Ein technischer Fehler ist aufgetreten. Ob der Vorgang ausgeführt wurde, ist nicht bestätigt. Prüfen Sie den aktuellen Stand, bevor Sie es erneut versuchen.'
};

const fieldNames: Readonly<Record<string, string>> = {
  email: 'E-Mail-Adresse', emailaddress: 'E-Mail-Adresse', password: 'Passwort',
  displayname: 'Name', name: 'Name', title: 'Bezeichnung',
  description: 'Beschreibung', projectid: 'Projekt', customerid: 'Kunde',
  employeeid: 'Mitarbeiter', tenantid: 'Betrieb', buildingid: 'Gebäude',
  supplierid: 'Lieferant', invoiceid: 'Rechnung', invoicenumber: 'Rechnungsnummer',
  date: 'Datum', startdate: 'Startdatum', enddate: 'Enddatum',
  duedate: 'Fälligkeitsdatum', amount: 'Betrag', quantity: 'Menge',
  unitprice: 'Einzelpreis', price: 'Preis', street: 'Straße',
  city: 'Ort', postalcode: 'Postleitzahl', token: 'Einladungs- oder Wiederherstellungscode',
  revision: 'Datenstand', phone: 'Telefonnummer', percentage: 'Prozentwert',
  projectname: 'Projektname', number: 'Nummer', notes: 'Notiz'
};

function fieldName(path: string): string | undefined {
  // Use an explicit label catalog; server-supplied property paths are not UI copy.
  const parts = path.replace(/\[\d+\]/g, '').split(/[.$/]/).filter(Boolean);
  const normalized = (parts.at(-1) ?? '').toLowerCase();
  return fieldNames[normalized];
}

function validationMessages(errors?: Record<string, string[]>): string | undefined {
  if (!errors) return undefined;
  const messages: string[] = [];
  for (const [path, problems] of Object.entries(errors).slice(0, 32)) {
    if (!Array.isArray(problems) || !problems.length) continue;
    const label = fieldName(path);
    const text = problems.filter(value => typeof value === 'string').join(' ').slice(0, 4096);
    let instruction = 'Bitte überprüfen Sie den Wert.';
    if (/required|must not be empty|cannot be empty|pflicht|erforderlich|darf nicht leer/i.test(text))
      instruction = 'Bitte füllen Sie dieses Pflichtfeld aus.';
    else if (/valid e.?mail|email address|e-mail-adresse|e-mail-adresse/i.test(text))
      instruction = 'Bitte geben Sie eine gültige E-Mail-Adresse ein.';
    else if (/maximum|maxlength|too long|must be at most|höchstens|zu lang/i.test(text))
      instruction = 'Die Eingabe ist zu lang. Bitte kürzen Sie sie.';
    else if (/minimum|minlength|too short|at least|mindestens|zu kurz/i.test(text))
      instruction = 'Die Eingabe ist zu kurz. Bitte ergänzen Sie sie.';
    else if (/number|numeric|decimal|integer|range|between|zahl|bereich/i.test(text))
      instruction = 'Bitte geben Sie einen gültigen Zahlenwert im zulässigen Bereich ein.';
    const message = label ? `„${label}“: ${instruction}` : 'Bitte prüfen Sie die markierten Eingabefelder.';
    if (!messages.includes(message)) messages.push(message);
    if (messages.length >= 3) break;
  }
  if (!messages.length) return undefined;
  return messages.join(' ') + (Object.keys(errors).length > 3 ? ' Prüfen Sie auch die weiteren Eingaben.' : '');
}

/** Maps every HTTP error to an actionable message, even for unknown server codes. */
export function describeApiFailure(status: number, problem: ErrorProblem, path = ''): string {
  if (status === 401 && /^\/api\/v1\/auth\/login(?:\?|$)/.test(path))
    return 'Die Anmeldung war nicht erfolgreich. Bitte prüfen Sie E-Mail-Adresse und Passwort. Falls nötig, setzen Sie Ihr Passwort zurück.';
  if (status === 401) return codeMessages['unauthorized'];
  if (status === 403 && problem.code !== 'module_disabled') return codeMessages['forbidden'];
  if (status === 412) return codeMessages['revision_conflict'];
  const fields = status < 500 ? validationMessages(problem.errors) : undefined;
  if (fields) return fields;
  if (problem.code && Object.hasOwn(codeMessages, problem.code)) return codeMessages[problem.code];
  switch (status) {
    case 400: case 422: return 'Die Angaben konnten nicht verarbeitet werden. Bitte prüfen Sie Ihre Eingaben.';
    case 404: return codeMessages['not_found'];
    case 405: return 'Diese Aktion wird hier nicht unterstützt. Bitte aktualisieren Sie die Seite.';
    case 408: case 504: return 'Die Anfrage hat zu lange gedauert. Prüfen Sie den aktuellen Stand, bevor Sie es erneut versuchen.';
    case 409: return 'Der Vorgang steht im Konflikt mit dem aktuellen Datenstand. Bitte aktualisieren Sie die Ansicht und prüfen Sie Ihre Angaben.';
    case 413: return codeMessages['payload_too_large'];
    case 415: return 'Dieses Dateiformat wird hier nicht unterstützt. Bitte wählen Sie eine passende Datei.';
    case 423: return 'Der Vorgang ist vorübergehend gesperrt. Bitte wenden Sie sich an die Betriebsverwaltung.';
    case 428: return codeMessages['revision_required'];
    case 429: return 'Es wurden zu viele Anfragen gesendet. Bitte versuchen Sie es in einigen Minuten erneut.';
    case 500: return codeMessages['server_error'];
    case 502: case 503: return 'Der benötigte Dienst ist momentan nicht verfügbar. Bitte versuchen Sie es später erneut.';
    default: return status >= 500 ? codeMessages['server_error'] : 'Der Vorgang konnte nicht abgeschlossen werden. Bitte aktualisieren Sie die Ansicht und versuchen Sie es erneut.';
  }
}
