import { describeApiFailure } from './error-catalog';

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
  constructor(
    public readonly status: number,
    public readonly problem: ApiProblem,
    requestId?: string | null,
    public readonly path = ''
  ) {
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

async function failure(response: Response, fallback = `Anfrage fehlgeschlagen (${response.status}).`, path = ''): Promise<ApiError> {
  const raw = await response.json().catch(() => undefined) as unknown;
  const problem = problemFrom(raw, response.status, fallback);
  const header = response.headers.get('X-Request-Id');
  return new ApiError(response.status, problem, header === null ? problem.requestId : safeRequestId(header) ?? null, path);
}

export class ApiNetworkError extends Error {}

async function safelyFetch(input: RequestInfo | URL, init: RequestInit): Promise<Response> {
  try {
    return await fetch(input, init);
  } catch (error) {
    if (error instanceof Error && error.name === 'AbortError')
      throw new ApiNetworkError('Die Anfrage wurde abgebrochen. Prüfen Sie vor einem erneuten Speichern den aktuellen Stand.');
    if (error instanceof TypeError)
      throw new ApiNetworkError('Die Verbindung zum Server ist fehlgeschlagen. Prüfen Sie Ihre Internetverbindung und versuchen Sie es erneut. Prüfen Sie vor einem erneuten Speichern den aktuellen Stand.');
    throw error;
  }
}

export function csrfToken(): string | undefined {
  return document.cookie.split('; ').find(part => part.startsWith('XSRF-TOKEN='))?.slice(11);
}

export async function request<T>(path: string, init: RequestInit = {}, tenantId?: string): Promise<T> {
  const response = await requestResponse(path, init, tenantId);
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
  try { return await response.json() as T; }
  catch (error) {
    if (error instanceof SyntaxError) throw new Error('Der Server hat eine unvollständige Antwort gesendet. Bitte laden Sie die Seite neu.');
    throw error;
  }
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
      const response = await safelyFetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store', headers: { 'ngsw-bypass': '1' } });
      if (!response.ok) throw await failure(response, 'Sitzung konnte nicht geprüft werden.', '/api/v1/auth/csrf');
      token = csrfToken();
    }
    if (token) headers.set('X-XSRF-TOKEN', decodeURIComponent(token));
  }
  const response = await safelyFetch(path, { ...init, method, headers, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    throw await failure(response, undefined, path);
  }
  return response;
}

/** One presentation path across forms, technical workbenches and offline flows. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const message = error.status === 423 && error.problem.code === 'recovery_hold'
      ? recoveryHoldMessage
      : describeApiFailure(error.status, error.problem, error.path);
    // A request ID helps support diagnose server failures. Do not clutter field errors.
    return error.status >= 500 && error.requestId
      ? `${message} Anfragekennung: ${error.requestId}.`
      : message;
  }
  if (error instanceof ApiNetworkError) return error.message;
  // Native runtime exceptions can contain source paths or implementation details.
  if (error instanceof Error && error.constructor === Error &&
      error.message.length > 0 && error.message.length <= 500 && !/[\r\n]/.test(error.message))
    return error.message;
  return 'Ein unerwarteter Fehler ist aufgetreten. Bitte laden Sie die Seite neu und versuchen Sie es erneut.';
}
