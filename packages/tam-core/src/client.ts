// The HTTP client and the PKCE auth flow (docs/26).

import type { Manifest, OperationResponse, ResolveResponse, ViewResponse } from './manifest';


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

  async view(
    viewId: string, params?: Record<string, unknown>,
    options?: { actAs?: string },
  ): Promise<ViewResponse> {
    const response = await this.send(this.url(`/api/views/${viewId}`, params), {
      ...(options?.actAs ? { headers: { 'X-Tam-Tenant': options.actAs } } : {}),
    });
    if (!response.ok) throw new Error(`view ${viewId}: ${response.status}`);
    return await response.json();
  }

  async operation(
    operationId: string,
    body: Record<string, unknown>,
    options?: { idempotencyKey?: string; actAs?: string },
  ): Promise<OperationResponse> {
    const response = await this.send(this.url(`/api/operations/${operationId}`), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(options?.idempotencyKey ? { 'X-Idempotency-Key': options.idempotencyKey } : {}),
        // Per-call act-as (docs/26 D-H4): a subtree grid's row action executes in the ROW's
        // node. Server-validated against the standable set like any act-as.
        ...(options?.actAs ? { 'X-Tam-Tenant': options.actAs } : {}),
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

  /** The account's standable companies (docs/26 D-H3): memberships + cascaded descendants. Routed
   *  through send(), so it gets the same 401→refresh→retry behavior as every other call. */
  async standable(): Promise<StandableInfo> {
    const response = await this.send(this.url('/api/tenants/standable'));
    if (!response.ok) throw new Error(`standable: ${response.status}`);
    return await response.json();
  }

  /** Acting company (docs/26 D-H4): sets/clears the act-as header on every subsequent request.
   *  The server validates the node against the account's standable set. */
  actAs(tenantId: string | null): void {
    if (tenantId) this.headers['X-Tam-Tenant'] = tenantId;
    else delete this.headers['X-Tam-Tenant'];
  }

  /** The currently acting company, or null when acting as the token's own node. */
  get actingAs(): string | null {
    return this.headers['X-Tam-Tenant'] ?? null;
  }
}

export interface StandableInfo {
  active: string;
  nodes: { id: string; display: string }[];
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
      return await this.tokenRequest({
        grant_type: 'authorization_code',
        code,
        redirect_uri: this.redirectUri,
        client_id: this.clientId,
        code_verifier: sessionStorage.getItem(this.verifierKey) ?? '',
      });
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
    let session: TamSession | null;
    try {
      // Refresh tokens rotate (the server issues a new one and revokes the old); tokenRequest
      // stores the rotated pair via adopt().
      session = await this.tokenRequest({
        grant_type: 'refresh_token',
        refresh_token: refreshToken,
        client_id: this.clientId,
      });
    } catch {
      return false;   // network hiccup: keep the session, let the original request's failure surface
    }
    if (!session) { this.endSession(); return false; }
    return true;
  }

  /** The one token-endpoint POST both grant flows share; adopts the returned token pair. */
  private async tokenRequest(params: Record<string, string>): Promise<TamSession | null> {
    const response = await fetch(`${this.client.baseUrl}${this.tokenPath}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(params),
    });
    const data = await response.json().catch(() => ({}));
    return response.ok && typeof data.access_token === 'string'
      ? this.adopt(data.access_token, data.refresh_token)
      : null;
  }

  /** Forget the tokens, revoke the refresh token server-side, and unwire the client. Revocation is
   *  fire-and-forget — local sign-out never waits on (or fails with) the network; an unreachable
   *  revocation endpoint still leaves the token to die by rotation reuse-detection or expiry. */
  signOut(): void {
    const refreshToken = typeof window === 'undefined' ? null : sessionStorage.getItem(this.refreshKey);
    if (refreshToken) {
      void fetch(`${this.client.baseUrl}/connect/revocation`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          token: refreshToken,
          token_type_hint: 'refresh_token',
          client_id: this.clientId,
        }),
      }).catch(() => undefined);
    }
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
    // the tab session (settled: no BFF). The server side carries the hardening: one-time-use
    // rotation with reuse detection (a replayed token revokes its whole family), revocation on
    // sign-out, and a pruned token store.
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
