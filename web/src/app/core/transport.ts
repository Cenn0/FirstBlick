export interface ApiProblem {
  title?: string;
  detail?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly problem: ApiProblem) {
    super(problem.detail || problem.title || 'Die Anfrage konnte nicht verarbeitet werden.');
  }
}

export function csrfToken(): string | undefined {
  return document.cookie.split('; ').find(part => part.startsWith('XSRF-TOKEN='))?.slice(11);
}

export async function request<T>(path: string, init: RequestInit = {}, tenantId?: string): Promise<T> {
  if (!path.startsWith('/api/v1/') || path.startsWith('//')) throw new Error('Ungültiger API-Pfad.');
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json');
  if (tenantId) headers.set('X-Tenant-Id', tenantId);
  const method = (init.method || 'GET').toUpperCase();
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    let token = csrfToken();
    if (!token) {
      const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) throw new ApiError(response.status, { title: 'Sitzung konnte nicht geprüft werden.' });
      token = csrfToken();
    }
    if (token) headers.set('X-XSRF-TOKEN', decodeURIComponent(token));
  }
  const response = await fetch(path, { ...init, method, headers, credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: `Anfrage fehlgeschlagen (${response.status}).` })) as ApiProblem;
    throw new ApiError(response.status, problem);
  }
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
  return await response.json() as T;
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409 || error.status === 412) return 'Dieser Eintrag wurde zwischenzeitlich geändert. Bitte neu laden und Ihre Angaben mit dem aktuellen Stand abgleichen.';
    if (error.status === 401) return 'Ihre Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.';
    if (error.status === 403) return 'Für diese Aktion fehlt die Berechtigung oder das Modul ist deaktiviert.';
    const fields = Object.values(error.problem.errors ?? {}).flat().join(' ');
    return fields || error.message;
  }
  if (error instanceof TypeError) return 'Der Server ist nicht erreichbar. Bitte prüfen Sie Ihre Verbindung.';
  return error instanceof Error ? error.message : 'Ein unerwarteter Fehler ist aufgetreten.';
}
