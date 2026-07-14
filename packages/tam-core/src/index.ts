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
  }>;
  forms: Record<string, {
    operation: string;
    fields: ManifestField[];
    includeExtensions: boolean;
    serverDependencies: string[];
  }>;
  grids: Record<string, {
    view: string;
    columns: string[];
    rowActions: string[];
    toolbarActions: string[];
    includeExtensions: boolean;
  }>;
  extensions: Record<string, ManifestField[]>;
  permissions: string[];
  actorPermissions?: string[];
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

  private url(path: string, params?: Record<string, unknown>): string {
    const search = new URLSearchParams({ culture: this.culture });
    for (const [k, v] of Object.entries(params ?? {})) {
      if (v !== undefined && v !== null && v !== '') search.set(k, String(v));
    }
    return `${this.baseUrl}${path}?${search}`;
  }

  async manifest(): Promise<Manifest> {
    const response = await fetch(this.url('/api/manifest'), { headers: this.headers });
    if (!response.ok) throw new Error(`manifest: ${response.status}`);
    return await response.json();
  }

  async view(viewId: string, params?: Record<string, unknown>): Promise<ViewResponse> {
    const response = await fetch(this.url(`/api/views/${viewId}`, params), { headers: this.headers });
    if (!response.ok) throw new Error(`view ${viewId}: ${response.status}`);
    return await response.json();
  }

  async operation(
    operationId: string,
    body: Record<string, unknown>,
    options?: { idempotencyKey?: string },
  ): Promise<OperationResponse> {
    const response = await fetch(this.url(`/api/operations/${operationId}`), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...this.headers,
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
    const response = await fetch(this.url(`/api/forms/${formId}/resolve`), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...this.headers },
      body: JSON.stringify({ input, changed, revision }),
    });
    if (!response.ok) throw new Error(`resolve ${formId}: ${response.status}`);
    return await response.json();
  }
}
