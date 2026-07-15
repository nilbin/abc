// Portable expression evaluator — mirror of Tam.Core/PortableExpressions.cs.

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
