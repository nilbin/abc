// @tam/core manifest types — the wire shapes (docs/12).

import type { Px } from './px';

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
  /** Plugin-owned state (docs/31 D-X2) or a computed-display seat (docs/34 M5): extension
   * fields are excluded from forms; compiled form fields render DISABLED, fed by suggestions. */
  readOnly?: boolean;
  /** Reference field's lookup view (docs/34 M5): render a searchable picker over it. */
  lookup?: string;
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
    /** Set when the view reads across the acting node's subtree: the result field carrying
     *  each row's tenant id — render it as the company column + tenant filter, and target
     *  row actions at the row's own node. */
    subtree?: string;
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
    /** Plugin row actions contributed onto this grid (docs/31 D-X1), activation-filtered.
     *  bind maps operation input wire names to row column wire names. */
    contributedActions?: { operation: string; plugin: string; bind: Record<string, string> }[];
  }>;
  extensions: Record<string, ManifestField[]>;
  permissions: string[];
  actorPermissions?: string[];
  /** Plugins ACTIVE for this tenant — inactive plugins are absent from every collection. */
  plugins?: string[];
  /** Framework packages — always active, enumerable so clients can group admin surfaces. */
  packages?: string[];
  /** Declared navigation trees per surface class (docs/30); filter per actor at render. */
  nav?: Record<string, NavNode[]>;
  /** Framework-composed pages (docs/32): ORDERED sections (grids + slots) and an optional
   *  record surface whose sections (forms + slots) render in declaration order. */
  pages?: Record<string, {
    sections: { kind: 'grid' | 'slot'; id: string; headingKey?: string }[];
    record?: {
      detailView: string; key: string; titleField?: string;
      sections: { kind: 'form' | 'slot'; id: string }[];
    };
  }>;
  /** Host slots and the active plugins' panels in them (docs/31 D-X4). */
  slots?: Record<string, { grid: string; plugin: string; bind: Record<string, string> }[]>;
  /** Declared domain events with the active plugins subscribed to each (docs/31 D-X5). */
  events?: Record<string, { fields: string[]; subscribedBy: string[] }>;
  revision: number;
}

export interface NavTarget {
  grid?: string;
  page?: string;
  /** The generic "every grid this plugin contributed" fallback page. */
  plugin?: string;
}

export interface NavNode {
  id: string;
  kind: 'mode' | 'section' | 'page';
  labelKey: string;
  icon?: string;
  order?: number;
  target?: NavTarget;
  permission?: string;
  plugin?: string;
  children: NavNode[];
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
