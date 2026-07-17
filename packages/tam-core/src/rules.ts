// Rule-builder model (docs/22): the pure translation between the visual clause/action model the
// UI edits and the portable Px condition + closed action JSON the server stores. Kept here (not in
// the React layer) so it is framework-agnostic and unit-tested against the same Px the server runs.

import { Manifest, ManifestField } from './manifest';
import { Px } from './px';

/** A row of the rules.schema view: a compiled TARGET-ROW field, server-typed. */
export interface RuleSchemaRow {
  path: string;        // "row.status"
  labelKey: string;
  wireKind: string;
  options: string[];
  entityKey: string;   // the row entity's key — pull its extension fields from the manifest
}

export type RefNamespace = 'field' | 'ext' | 'row' | 'row.ext';

/** A field a rule may reference (condition) or write (set-field), assembled from the manifest the
 *  client already holds plus the rules.schema row fields. Its wireKind drives the value control. */
export interface RuleRef {
  path: string;        // the Px field name: "orderType", "ext.needsReview", "row.status", "row.ext.priority"
  labelKey: string;
  wireKind: string;
  options: string[];
  namespace: RefNamespace;
}

export type PxBinOp = 'eq' | 'ne' | 'gt' | 'ge' | 'lt' | 'le';
export type ClauseOp = PxBinOp | 'isNull' | 'isNotNull';

const ORDERED: PxBinOp[] = ['eq', 'ne', 'gt', 'ge', 'lt', 'le'];

/** The operators a field's TYPE supports — the server's wireKind decides, so the same rule the
 *  view executor and Px evaluator apply drives the operator dropdown. */
export function operatorsFor(wireKind: string): ClauseOp[] {
  switch (wireKind) {
    case 'number':
    case 'integer':
    case 'date':
    case 'datetime':
      return [...ORDERED, 'isNull', 'isNotNull'];
    case 'boolean':
      return ['eq', 'ne', 'isNull', 'isNotNull'];
    default: // string / selection / anything else compares by equality
      return ['eq', 'ne', 'isNull', 'isNotNull'];
  }
}

/** True when the operator needs no right-hand value (unary presence checks). */
export function isUnary(op: ClauseOp): op is 'isNull' | 'isNotNull' {
  return op === 'isNull' || op === 'isNotNull';
}

function ref(f: ManifestField, path: string, namespace: RefNamespace): RuleRef {
  return { path, labelKey: f.labelKey, wireKind: f.wireKind, options: f.options ?? [], namespace };
}

/**
 * Every field a condition on this trigger may reference. Input/payload fields and extension fields
 * come from the manifest the client already holds; the row.* compiled fields come from the
 * rules.schema view (the one thing the manifest cannot supply). Server-authoritative typing
 * throughout — the client only assembles.
 */
export function conditionRefs(
  manifest: Manifest, schema: RuleSchemaRow[], trigger: string, kind: 'operation' | 'event',
): RuleRef[] {
  const refs: RuleRef[] = [];
  if (kind === 'event') {
    // Event payload fields are declared as NAMES only (no types) — default to string/equality.
    for (const name of manifest.events?.[trigger]?.fields ?? [])
      refs.push({ path: name, labelKey: name, wireKind: 'string', options: [], namespace: 'field' });
  } else {
    const op = manifest.operations[trigger];
    if (op) {
      for (const f of op.fields)
        if (f.wireKind !== 'object') refs.push(ref(f, f.name, 'field'));
      const entity = op.extensibleEntity;
      if (entity)
        for (const f of manifest.extensions[entity] ?? [])
          refs.push(ref(f, `ext.${f.name}`, 'ext'));
    }
  }
  // The target row's compiled fields (rules.schema) and its extension fields (manifest overlay).
  const entityKey = schema[0]?.entityKey;
  for (const r of schema)
    if (r.wireKind !== 'object')
      refs.push({ path: r.path, labelKey: r.labelKey, wireKind: r.wireKind, options: r.options ?? [], namespace: 'row' });
  if (entityKey)
    for (const f of manifest.extensions[entityKey] ?? [])
      refs.push(ref(f, `row.ext.${f.name}`, 'row.ext'));
  return refs;
}

/** The target row's WRITABLE extension fields — the set-field action's legal targets. Read-only
 *  (plugin-owned) fields are excluded, exactly as the define operation refuses them. */
export function setFieldTargets(manifest: Manifest, entityKey: string | undefined): RuleRef[] {
  if (!entityKey) return [];
  return (manifest.extensions[entityKey] ?? [])
    .filter(f => !f.readOnly)
    .map(f => ref(f, `ext.${f.name}`, 'ext'));
}

// ---- condition (clause model <-> Px) ------------------------------------------------------------

export interface RuleClause {
  path: string;
  op: ClauseOp;
  value?: unknown;
  /** When set (including 0), the right-hand side is today+relativeDays via a Px `fn` node, not a
   *  literal — the relative-date feature the server evaluates. Only meaningful for date fields. */
  relativeDays?: number | null;
}

export interface ParsedCondition {
  match: 'all' | 'any';    // and | or
  clauses: RuleClause[];
}

function clauseToPx(c: RuleClause): Px {
  const field: Px = { t: 'field', f: c.path };
  if (isUnary(c.op)) return { t: 'un', op: c.op, x: field };
  const right: Px = c.relativeDays !== undefined && c.relativeDays !== null
    ? { t: 'fn', op: 'today', days: c.relativeDays }
    : { t: 'const', v: c.value ?? null };
  return { t: 'bin', op: c.op, l: field, r: right };
}

/** Fold the clauses into one Px condition. No clauses → the always-true constant (a rule with an
 *  empty condition fires on every trigger — the same shape the server accepts). */
export function buildCondition(parsed: ParsedCondition): Px {
  const nodes = parsed.clauses.filter(c => c.path).map(clauseToPx);
  if (nodes.length === 0) return { t: 'const', v: true };
  const join: 'and' | 'or' = parsed.match === 'any' ? 'or' : 'and';
  return nodes.reduce((l, r) => ({ t: 'bin', op: join, l, r }));
}

function flatten(px: Px, op: 'and' | 'or'): Px[] {
  if (px.t === 'bin' && px.op === op) return [...flatten(px.l, op), ...flatten(px.r, op)];
  return [px];
}

function toClause(p: Px): RuleClause | null {
  if (p.t === 'un' && (p.op === 'isNull' || p.op === 'isNotNull') && p.x.t === 'field')
    return { path: p.x.f, op: p.op };
  if (p.t === 'bin' && ORDERED.includes(p.op as PxBinOp) && p.l.t === 'field') {
    if (p.r.t === 'const') return { path: p.l.f, op: p.op as PxBinOp, value: p.r.v };
    if (p.r.t === 'fn' && p.r.op === 'today') return { path: p.l.f, op: p.op as PxBinOp, relativeDays: p.r.days ?? 0 };
  }
  return null;
}

/**
 * Px → the clause model, or null when the expression does not fit the flat all/any-of-clauses
 * shape (nested groups, mixed operators, computed sides). Null tells the builder to fall back to
 * raw-JSON editing rather than silently dropping structure.
 */
export function parseCondition(px: Px | null | undefined): ParsedCondition | null {
  if (!px) return { match: 'all', clauses: [] };
  if (px.t === 'const' && px.v === true) return { match: 'all', clauses: [] };

  let match: 'all' | 'any' = 'all';
  let parts: Px[] = [px];
  if (px.t === 'bin' && (px.op === 'and' || px.op === 'or')) {
    match = px.op === 'or' ? 'any' : 'all';
    parts = flatten(px, px.op);
  }
  const clauses: RuleClause[] = [];
  for (const p of parts) {
    const c = toClause(p);
    if (!c) return null;
    clauses.push(c);
  }
  return { match, clauses };
}

// ---- action (model <-> stored JSON) -------------------------------------------------------------

export type RuleActionType = 'finding' | 'set-field' | 'publish-event';

export interface RuleActionModel {
  type: RuleActionType;
  field?: string;      // set-field: "ext.{key}" on the target row
  value?: unknown;
}

/** The stored Action string, or null for a blocking-finding rule (no action field). */
export function buildAction(a: RuleActionModel): string | null {
  if (a.type === 'set-field') return JSON.stringify({ type: 'set-field', field: a.field, value: a.value ?? null });
  if (a.type === 'publish-event') return JSON.stringify({ type: 'publish-event' });
  return null;
}

export function parseAction(json: string | null | undefined): RuleActionModel {
  if (!json) return { type: 'finding' };
  try {
    const a = JSON.parse(json);
    if (a?.type === 'set-field') return { type: 'set-field', field: a.field, value: a.value };
    if (a?.type === 'publish-event') return { type: 'publish-event' };
  } catch { /* not clause-shaped JSON — treat as a finding rule */ }
  return { type: 'finding' };
}
