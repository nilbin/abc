// @tam/core — framework-agnostic manifest types, portable expression evaluator,
// localization, and the HTTP client. The React layer builds on this.

export type Severity = 'information' | 'warning' | 'error';

export interface Finding {
  code: string;
  severity: Severity;
  args: Record<string, unknown>;
  targets: string[];
  blocksSubmission: boolean;
  message?: string;
}

export interface FieldConflict {
  field: string;
  originalValue: unknown;
  currentValue: unknown;
  submittedValue: unknown;
}

// Portable expression AST — mirror of Tam.Core/PortableExpressions.cs. One AST, two evaluators.
export type Px =
  | { t: 'const'; v: unknown }
  | { t: 'field'; f: string }
  | { t: 'un'; op: 'not' | 'isNull' | 'isNotNull'; x: Px }
  | { t: 'bin'; op: 'eq' | 'ne' | 'gt' | 'ge' | 'lt' | 'le' | 'and' | 'or'; l: Px; r: Px };

export function evalPx(px: Px, get: (field: string) => unknown): unknown {
  switch (px.t) {
    case 'const':
      return px.v;
    case 'field':
      return normalize(get(px.f));
    case 'un': {
      const v = evalPx(px.x, get);
      if (px.op === 'not') return v !== true;
      if (px.op === 'isNull') return v === null || v === undefined;
      return v !== null && v !== undefined;
    }
    case 'bin': {
      if (px.op === 'and') return evalPx(px.l, get) === true && evalPx(px.r, get) === true;
      if (px.op === 'or') return evalPx(px.l, get) === true || evalPx(px.r, get) === true;
      const l = normalize(evalPx(px.l, get));
      const r = normalize(evalPx(px.r, get));
      switch (px.op) {
        case 'eq': return looseEquals(l, r);
        case 'ne': return !looseEquals(l, r);
        case 'gt': return compare(l, r) > 0;
        case 'ge': return compare(l, r) >= 0;
        case 'lt': return compare(l, r) < 0;
        case 'le': return compare(l, r) <= 0;
      }
    }
  }
}

function normalize(v: unknown): unknown {
  if (v === undefined || v === '') return null;
  return v;
}

function looseEquals(l: unknown, r: unknown): boolean {
  if (l === null && r === null) return true;
  if (typeof l === 'string' && typeof r === 'string') {
    // Enum wire values are camelCase; C# constants lower to PascalCase names.
    return l.toLowerCase() === r.toLowerCase();
  }
  return l === r;
}

function compare(l: unknown, r: unknown): number {
  if (l === null && r === null) return 0;
  if (l === null) return -1;
  if (r === null) return 1;
  if (typeof l === 'number' && typeof r === 'number') return l - r;
  return String(l) < String(r) ? -1 : String(l) > String(r) ? 1 : 0;
}

// ---- Manifest ----

export interface ManifestField {
  name: string;
  labelKey: string;
  type: string;
  wireKind: string;
  format?: string;
  required: boolean;
  maxLength?: number;
  options?: string[];
  changeSet: boolean;
  extension?: boolean;
  visibleWhen?: Px;
  requiredWhen?: Px;
  renderer?: string;
}

export interface Manifest {
  version: string;
  defaultCulture: string;
  catalogs: Record<string, Record<string, string>>;
  operations: Record<string, {
    permission: string;
    titleKey: string;
    fields: ManifestField[];
    extensibleEntity?: string;
    plugin?: string;
  }>;
  views: Record<string, {
    permission: string;
    queryFields: ManifestField[];
    resultFields: ManifestField[];
    sortable: string[];
    filterable: string[];
    defaultSort?: string;
    defaultSortDescending: boolean;
    extensibleEntity?: string;
    plugin?: string;
  }>;
  forms: Record<string, {
    operation: string;
    fields: ManifestField[];
    includeExtensions: boolean;
    serverDependencies: string[];
    plugin?: string;
  }>;
  grids: Record<string, {
    view: string;
    columns: string[];
    rowActions: string[];
    toolbarActions: string[];
    includeExtensions: boolean;
    plugin?: string;
  }>;
  extensions: Record<string, ManifestField[]>;
  permissions: string[];
  actorPermissions?: string[];
  /** Plugins ACTIVE for this tenant — inactive plugins are absent from every collection. */
  plugins?: string[];
  revision: number;
}

export interface ResolvedFieldState {
  visible: boolean;
  enabled: boolean;
  required: boolean;
  suggestedValue?: unknown;
  options?: { value: unknown; label: string }[];
  findings: Finding[];
}

export interface ResolveResponse {
  fields: Record<string, ResolvedFieldState>;
  findings: Finding[];
  revision: number;
}

export interface OperationResponse {
  output?: Record<string, unknown>;
  findings: Finding[];
  effects: Record<string, unknown>[];
  newVersion?: number;
  auditReference?: string;
  conflicts?: FieldConflict[];
}

export interface ViewResponse {
  rows: Record<string, unknown>[];
  total: number;
  page: number;
  pageSize: number;
}

// ---- Localization (docs/21): resolve locally from catalogs; server message is the fallback ----

export function translate(
  manifest: Manifest, culture: string, key: string,
  args?: Record<string, unknown>): string {
  const template = manifest.catalogs[culture]?.[key]
    ?? manifest.catalogs[manifest.defaultCulture]?.[key]
    ?? key;
  if (!args) return template;
  return template.replace(/\{(\w+)\}/g, (_, k) =>
    args[k] !== undefined ? String(args[k]) : `{${k}}`);
}

export function findingMessage(
  manifest: Manifest, culture: string, finding: Finding): string {
  const template = manifest.catalogs[culture]?.[finding.code];
  if (!template) return finding.message ?? finding.code;
  return template.replace(/\{(\w+)\}/g, (_, k) =>
    finding.args?.[k] !== undefined ? String(finding.args[k]) : `{${k}}`);
}

/** PascalCase enum name → camelCase wire value (single shared conversion). */
export function toWireEnum(name: string): string {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

export function enumLabel(manifest: Manifest, culture: string, value: unknown): string {
  if (value === null || value === undefined) return '';
  const s = String(value);
  const kebab = toWireEnum(s).replace(/[A-Z]/g, m => '-' + m.toLowerCase());
  const key = `enums.${kebab}`;
  const hit = manifest.catalogs[culture]?.[key] ?? manifest.catalogs[manifest.defaultCulture]?.[key];
  return hit ?? s;
}

// ---- Client ----

export class TamClient {
  /** Extra headers on every request (e.g. demo role selection, auth tokens). */
  public headers: Record<string, string> = {};

  constructor(
    readonly baseUrl: string = '',
    public culture: string = 'sv') {}

  /**
   * Called with no args when a request comes back 401 (the access token expired). If it resolves
   * true, the one failed request is retried once with the (now refreshed) headers. TamAuth wires
   * this to its silent refresh; unset, a 401 simply surfaces.
   */
  public onUnauthorized?: () => Promise<boolean>;

  private url(path: string, params?: Record<string, unknown>): string {
    const search = new URLSearchParams({ culture: this.culture });
    for (const [k, v] of Object.entries(params ?? {})) {
      if (v !== undefined && v !== null && v !== '') search.set(k, String(v));
    }
    return `${this.baseUrl}${path}?${search}`;
  }

  /** Every request routes through here so the bearer header and 401→refresh→retry live in one place. */
  private async send(url: string, init: RequestInit = {}): Promise<Response> {
    const withHeaders = (): RequestInit => ({ ...init, headers: { ...(init.headers ?? {}), ...this.headers } });
    let response = await fetch(url, withHeaders());
    if (response.status === 401 && this.onUnauthorized && (await this.onUnauthorized())) {
      response = await fetch(url, withHeaders());   // retry once with the refreshed token
    }
    return response;
  }

  async manifest(): Promise<Manifest> {
    const response = await this.send(this.url('/api/manifest'));
    if (!response.ok) throw new Error(`manifest: ${response.status}`);
    return await response.json();
  }

  async view(viewId: string, params?: Record<string, unknown>): Promise<ViewResponse> {
    const response = await this.send(this.url(`/api/views/${viewId}`, params));
    if (!response.ok) throw new Error(`view ${viewId}: ${response.status}`);
    return await response.json();
  }

  async operation(
    operationId: string,
    body: Record<string, unknown>,
    options?: { idempotencyKey?: string },
  ): Promise<OperationResponse> {
    const response = await this.send(this.url(`/api/operations/${operationId}`), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(options?.idempotencyKey ? { 'X-Idempotency-Key': options.idempotencyKey } : {}),
      },
      body: JSON.stringify(body),
    });
    // Deliberately no response.ok check: 403/409/422 carry the findings envelope as the body.
    return await response.json();
  }

  async resolve(
    formId: string,
    input: Record<string, unknown>,
    changed: string[] | null,
    revision: number,
  ): Promise<ResolveResponse> {
    const response = await this.send(this.url(`/api/forms/${formId}/resolve`), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ input, changed, revision }),
    });
    if (!response.ok) throw new Error(`resolve ${formId}: ${response.status}`);
    return await response.json();
  }
}

// ---- Auth (Authorization Code + PKCE, docs/26) ----
//
// The framework renders the login + tenant picker server-side; the app is a public client that only
// starts the redirect and redeems the code. All the PKCE mechanics — challenge, callback exchange,
// token storage, and wiring the bearer header onto the client — live HERE so an app never hand-rolls
// them (it was boilerplate in every app otherwise). Framework-agnostic; the React hook is a thin wrap.

export interface TamUser {
  sub: string;
  name: string;
}

export interface TamSession {
  token: string;
  user: TamUser;
}

export interface TamAuthOptions {
  /** The public client id registered with the token server (EnsureSpaClientAsync). */
  clientId: string;
  /** Where the code is returned; must be a registered redirect URI. Default: origin + '/callback'. */
  redirectUri?: string;
  authorizePath?: string;   // default '/connect/authorize'
  tokenPath?: string;       // default '/connect/token'
  callbackPath?: string;    // default '/callback'
  storageKey?: string;      // default 'tam-token'
}

function b64urlBytes(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeJwt(token: string): Record<string, unknown> {
  const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  const pad = part.length % 4 ? '='.repeat(4 - (part.length % 4)) : '';
  return JSON.parse(atob(part + pad)) as Record<string, unknown>;
}

export class TamAuth {
  private readonly clientId: string;
  private readonly redirectUri: string;
  private readonly authorizePath: string;
  private readonly tokenPath: string;
  private readonly callbackPath: string;
  private readonly storageKey: string;
  private readonly refreshKey: string;
  private readonly verifierKey = 'tam-pkce-verifier';
  private readonly stateKey = 'tam-pkce-state';
  private refreshing: Promise<boolean> | null = null;

  /** Invoked when the session ends involuntarily (a refresh failed). The React hook uses this to
   * flip back to anonymous so the app shows the sign-in surface again. */
  public onSessionEnded?: () => void;

  constructor(private readonly client: TamClient, options: TamAuthOptions) {
    this.clientId = options.clientId;
    this.redirectUri = options.redirectUri ?? `${window.location.origin}/callback`;
    this.authorizePath = options.authorizePath ?? '/connect/authorize';
    this.tokenPath = options.tokenPath ?? '/connect/token';
    this.callbackPath = options.callbackPath ?? '/callback';
    this.storageKey = options.storageKey ?? 'tam-token';
    this.refreshKey = `${this.storageKey}-refresh`;
    // Silent renewal: when the client hits a 401, try to refresh and let it retry the request once.
    this.client.onUnauthorized = () => this.refresh();
  }

  /** True when the browser is on the redirect-back URL and a code needs redeeming. */
  isCallback(): boolean {
    return typeof window !== 'undefined' && window.location.pathname === this.callbackPath;
  }

  /** Rehydrate a stored token on load and wire it onto the client, or null if none. */
  restore(): TamSession | null {
    const token = typeof window === 'undefined' ? null : sessionStorage.getItem(this.storageKey);
    return token ? this.adopt(token) : null;
  }

  /** Begin sign-in: mint a PKCE verifier/challenge and redirect to the framework login. */
  async signIn(): Promise<void> {
    const verifier = b64urlBytes(crypto.getRandomValues(new Uint8Array(32)));
    const state = b64urlBytes(crypto.getRandomValues(new Uint8Array(16)));
    sessionStorage.setItem(this.verifierKey, verifier);
    sessionStorage.setItem(this.stateKey, state);
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    const params = new URLSearchParams({
      client_id: this.clientId,
      response_type: 'code',
      redirect_uri: this.redirectUri,
      code_challenge: b64urlBytes(new Uint8Array(digest)),
      code_challenge_method: 'S256',
      scope: 'offline_access',   // ask for a refresh token so the session can renew silently
      state,
    });
    window.location.href = `${this.client.baseUrl}${this.authorizePath}?${params}`;
  }

  /** Redeem the returned code for a token (state-checked), store it, clean the URL. */
  async completeCallback(): Promise<TamSession | null> {
    const query = new URLSearchParams(window.location.search);
    const code = query.get('code');
    const state = query.get('state');
    try {
      if (!code || !state || state !== sessionStorage.getItem(this.stateKey)) return null;
      const response = await fetch(`${this.client.baseUrl}${this.tokenPath}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'authorization_code',
          code,
          redirect_uri: this.redirectUri,
          client_id: this.clientId,
          code_verifier: sessionStorage.getItem(this.verifierKey) ?? '',
        }),
      });
      const data = await response.json().catch(() => ({}));
      return typeof data.access_token === 'string'
        ? this.adopt(data.access_token, data.refresh_token)
        : null;
    } finally {
      sessionStorage.removeItem(this.verifierKey);
      sessionStorage.removeItem(this.stateKey);
      window.history.replaceState({}, '', '/');
    }
  }

  /**
   * Exchange the stored refresh token for a fresh access token. De-duplicated, so a burst of 401s
   * triggers exactly one network refresh and they all await it. Returns false (and ends the session)
   * when there is no usable refresh token; a transient network error returns false without ending it.
   */
  async refresh(): Promise<boolean> {
    return (this.refreshing ??= this.doRefresh().finally(() => { this.refreshing = null; }));
  }

  private async doRefresh(): Promise<boolean> {
    const refreshToken = typeof window === 'undefined' ? null : sessionStorage.getItem(this.refreshKey);
    if (!refreshToken) { this.endSession(); return false; }
    let response: Response;
    try {
      response = await fetch(`${this.client.baseUrl}${this.tokenPath}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'refresh_token',
          refresh_token: refreshToken,
          client_id: this.clientId,
        }),
      });
    } catch {
      return false;   // network hiccup: keep the session, let the original request's failure surface
    }
    const data = await response.json().catch(() => ({}));
    if (!response.ok || typeof data.access_token !== 'string') { this.endSession(); return false; }
    // Refresh tokens rotate (the server issues a new one and revokes the old), so store the new one.
    this.adopt(data.access_token, data.refresh_token);
    return true;
  }

  /** Forget the tokens and unwire the client. (Re-auth is a fresh signIn.) */
  signOut(): void {
    sessionStorage.removeItem(this.storageKey);
    sessionStorage.removeItem(this.refreshKey);
    delete this.client.headers['Authorization'];
  }

  private endSession(): void {
    this.signOut();
    this.onSessionEnded?.();
  }

  private adopt(token: string, refreshToken?: string): TamSession {
    sessionStorage.setItem(this.storageKey, token);
    // The refresh token is stored to survive reloads. sessionStorage (not localStorage) keeps it to
    // the tab session; a hardened deployment would move refresh handling behind a BFF cookie.
    if (typeof refreshToken === 'string' && refreshToken.length > 0)
      sessionStorage.setItem(this.refreshKey, refreshToken);
    this.client.headers['Authorization'] = `Bearer ${token}`;
    const payload = decodeJwt(token);
    return {
      token,
      user: { sub: String(payload.sub ?? ''), name: String(payload.name ?? payload.sub ?? 'User') },
    };
  }
}
