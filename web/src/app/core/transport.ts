export interface ApiProblem {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
  requestId?: string;
}

export const recoveryHoldMessage='Dieser Betrieb ist nach einer Wiederherstellung vorübergehend gesperrt. Neue Änderungen und Übertragungen sind erst nach Freigabe durch die Betriebsverwaltung möglich.';

export class ApiError extends Error {
  readonly requestId?: string;
  constructor(public readonly status: number, public readonly problem: ApiProblem, requestId?: string | null) {
    super(problem.detail || problem.title || 'Die Anfrage konnte nicht verarbeitet werden.');
    this.requestId = requestId === null ? undefined : safeRequestId(requestId ?? problem.requestId);
  }
}

function safeRequestId(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 && value.length <= 128 && /^[A-Za-z0-9:_-]+$/.test(value) ? value : undefined;
}

function boundedText(value: unknown): string | undefined {
  return typeof value === 'string' && value.length <= 2048 ? value : undefined;
}

function problemFrom(value: unknown, status: number, fallback: string): ApiProblem {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return { status, title: fallback };
  const source = value as Record<string, unknown>;
  const errors: Record<string, string[]> = Object.create(null) as Record<string, string[]>;
  if (source['errors'] !== null && typeof source['errors'] === 'object' && !Array.isArray(source['errors'])) {
    for (const [field, messages] of Object.entries(source['errors'] as Record<string, unknown>).slice(0, 32)) {
      if (Array.isArray(messages)) errors[field] = messages.slice(0, 8).filter((entry): entry is string => typeof entry === 'string' && entry.length <= 2048);
    }
  }
  return {
    status, title: boundedText(source['title']) ?? fallback,
    detail: boundedText(source['detail']), code: boundedText(source['code']),
    requestId: safeRequestId(source['requestId']), errors
  };
}

async function failure(response: Response, fallback = `Anfrage fehlgeschlagen (${response.status}).`): Promise<ApiError> {
  const raw = await response.json().catch(() => undefined) as unknown;
  const problem = problemFrom(raw, response.status, fallback);
  const header = response.headers.get('X-Request-Id');
  return new ApiError(response.status, problem, header === null ? problem.requestId : safeRequestId(header) ?? null);
}

export function csrfToken(): string | undefined {
  return document.cookie.split('; ').find(part => part.startsWith('XSRF-TOKEN='))?.slice(11);
}

export async function request<T>(path: string, init: RequestInit = {}, tenantId?: string): Promise<T> {
  const response = await requestResponse(path, init, tenantId);
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
  return await response.json() as T;
}

export async function requestResponse(path: string, init: RequestInit = {}, tenantId?: string, accept = 'application/json'): Promise<Response> {
  if (!path.startsWith('/api/v1/') || path.startsWith('//')) throw new Error('Ungültiger API-Pfad.');
  const headers = new Headers(init.headers);
  headers.set('Accept', accept);
  // Authentication and business responses always use the browser network stack,
  // even when an older application-shell service worker controls this page.
  headers.set('ngsw-bypass', '1');
  if (init.body && !(init.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  if (tenantId) headers.set('X-Tenant-Id', tenantId);
  const method = (init.method || 'GET').toUpperCase();
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    let token = csrfToken();
    if (!token) {
      const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store', headers: { 'ngsw-bypass': '1' } });
      if (!response.ok) throw await failure(response, 'Sitzung konnte nicht geprüft werden.');
      token = csrfToken();
    }
    if (token) headers.set('X-XSRF-TOKEN', decodeURIComponent(token));
  }
  const response = await fetch(path, { ...init, method, headers, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    throw await failure(response);
  }
  return response;
}

export function errorMessage(error: unknown): string {
  const message = plainErrorMessage(error);
  return error instanceof ApiError && error.requestId ? `${message} Anfragekennung: ${error.requestId}.` : message;
}

function plainErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if(error.status===423&&error.problem.code==='recovery_hold')return recoveryHoldMessage;
    if(error.problem.code==='migration_activation_required')return 'Dieses übernommene Konto benötigt eine neue persönliche Einladung. Erstellen Sie unter „Persönliche Zugänge“ eine Einladung an dieselbe E-Mail-Adresse; die Person aktiviert den Zugang anschließend selbst.';
    if(error.problem.code==='zero_invoice')return 'Die Restmenge hat nach Rundung keinen offenen Rechnungsbetrag. Ein Null-Euro-Abschluss wird noch nicht unterstützt. Prüfen Sie eine vollständige Korrektur und Neuausstellung; es wurde nichts gebucht.';
    if (error.problem.code === 'approval_required') return 'Für diesen Vorgang ist eine zweite Freigabe erforderlich.';
    if (error.status === 412 || ['revision_conflict','concurrency_conflict','stale_revision'].includes(error.problem.code || '')) return 'Dieser Eintrag wurde zwischenzeitlich geändert. Bitte neu laden und Ihre Angaben mit dem aktuellen Stand abgleichen.';
    if (error.status === 401) return 'Ihre Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.';
    if (error.status === 403) return 'Für diese Aktion fehlt die Berechtigung oder das Modul ist deaktiviert.';
    const fields = Object.values(error.problem.errors ?? {}).flat().join(' ');
    return fields || error.message;
  }
  if (error instanceof TypeError) return 'Der Server ist nicht erreichbar. Bitte prüfen Sie Ihre Verbindung.';
  return error instanceof Error ? error.message : 'Ein unerwarteter Fehler ist aufgetreten.';
}
