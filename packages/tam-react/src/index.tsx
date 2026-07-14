// @tam/react — generic manifest-driven runtime: OperationForm + ViewGrid + renderer registry,
// with a default renderer pack built on Mantine. Server-defined semantics, client-defined
// presentation (docs/06): nothing here knows about orders or customers.

import React, {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState,
} from 'react';
import {
  Alert, Badge, Box, Button, Checkbox, Group, Loader, Modal, NumberInput, Pagination,
  Select, Stack, Table, Text, TextInput, Textarea, Title, UnstyledButton,
} from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import {
  Finding, FieldConflict, Manifest, ManifestField, OperationResponse, ResolveResponse,
  TamClient, enumLabel, evalPx, findingMessage, translate,
} from '@tam/core';

// ---------------------------------------------------------------- context

export interface TamContextValue {
  client: TamClient;
  manifest: Manifest;
  culture: string;
  setCulture: (culture: string) => void;
  refreshManifest: () => Promise<void>;
  t: (key: string) => string;
  /** Effective-permission check from the manifest's actor overlay (decision D1). */
  can: (permission: string) => boolean;
  /** Subscribe to committed-operation effects over SSE (decision D5); returns unsubscribe. */
  subscribeEffects: (callback: () => void) => () => void;
}

const TamContext = createContext<TamContextValue | null>(null);

export function useTam(): TamContextValue {
  const context = useContext(TamContext);
  if (!context) throw new Error('useTam must be used inside <TamProvider>');
  return context;
}

export function TamProvider(props: {
  client: TamClient;
  initialCulture?: string;
  children: React.ReactNode;
}) {
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [culture, setCultureState] = useState(props.initialCulture ?? props.client.culture);
  const effectListeners = useRef(new Set<() => void>());

  const refreshManifest = useCallback(async () => {
    setManifest(await props.client.manifest());
  }, [props.client]);

  useEffect(() => { void refreshManifest(); }, [refreshManifest]);

  useEffect(() => {
    const source = new EventSource(`${props.client.baseUrl}/api/events`);
    source.onmessage = () => effectListeners.current.forEach(listener => listener());
    return () => source.close();
  }, [props.client.baseUrl]);

  const setCulture = useCallback((next: string) => {
    props.client.culture = next;
    setCultureState(next);
  }, [props.client]);

  const value = useMemo<TamContextValue | null>(() => manifest && ({
    client: props.client,
    manifest,
    culture,
    setCulture,
    refreshManifest,
    t: (key: string) => translate(manifest, culture, key),
    can: (permission: string) => {
      const granted = manifest.actorPermissions ?? ['*'];
      return granted.includes('*') || granted.includes(permission);
    },
    subscribeEffects: (callback: () => void) => {
      effectListeners.current.add(callback);
      return () => effectListeners.current.delete(callback);
    },
  }), [manifest, culture, props.client, setCulture, refreshManifest]);

  if (!value) {
    return <Group justify="center" p="xl"><Loader /></Group>;
  }
  return <TamContext.Provider value={value}>{props.children}</TamContext.Provider>;
}

// ---------------------------------------------------------------- renderer registry

export interface FieldRendererProps {
  field: ManifestField;
  label: string;
  value: unknown;
  onChange: (value: unknown) => void;
  required: boolean;
  error?: string;
  warning?: string;
  options?: { value: unknown; label: string }[];
  tam: TamContextValue;
}

export type FieldRenderer = (props: FieldRendererProps) => React.ReactNode;

const registry = new Map<string, FieldRenderer>();

export function registerRenderer(key: string, renderer: FieldRenderer): void {
  registry.set(key, renderer);
}

function rendererFor(field: ManifestField): FieldRenderer {
  return registry.get(field.renderer ?? '')
    ?? registry.get(`type:${field.type}`)
    ?? DefaultRenderer;
}

const DefaultRenderer: FieldRenderer = (p) => {
  const common = {
    label: p.label,
    required: p.required,
    error: p.error,
    description: p.warning,
  };
  const str = (v: unknown) => (v === null || v === undefined ? '' : String(v));

  if (p.field.renderer === 'hidden') return null;

  if (p.options && p.options.length >= 0 && (p.field.type === 'reference' || p.options.length > 0)) {
    return (
      <Select
        {...common}
        data={p.options.map(o => ({ value: String(o.value), label: o.label }))}
        value={p.value === null || p.value === undefined ? null : String(p.value)}
        onChange={v => p.onChange(v)}
        searchable
        clearable={!p.required}
      />
    );
  }

  switch (p.field.wireKind) {
    case 'boolean':
      return (
        <Checkbox
          label={p.label}
          checked={p.value === true}
          onChange={e => p.onChange(e.currentTarget.checked)}
          error={p.error}
        />
      );
    case 'number':
    case 'integer':
      return (
        <NumberInput
          {...common}
          value={typeof p.value === 'number' ? p.value : ''}
          onChange={v => p.onChange(typeof v === 'number' ? v : null)}
          thousandSeparator=" "
          decimalScale={p.field.format === 'money' ? 2 : undefined}
        />
      );
    case 'date':
      return (
        <DateInput
          {...common}
          value={p.value ? dayjs(String(p.value)).toDate() : null}
          onChange={v => p.onChange(v ? dayjs(v).format('YYYY-MM-DD') : null)}
          valueFormat="YYYY-MM-DD"
          clearable={!p.required}
        />
      );
    default:
      if (p.field.options?.length) {
        return (
          <Select
            {...common}
            data={p.field.options.map(o => ({
              value: o.charAt(0).toLowerCase() + o.slice(1),
              label: enumLabel(p.tam.manifest, p.tam.culture, o),
            }))}
            value={str(p.value) || null}
            onChange={v => p.onChange(v)}
            allowDeselect={!p.required}
          />
        );
      }
      if (p.field.format === 'multiline' || p.field.type === 'multiline-text') {
        return (
          <Textarea {...common} autosize minRows={2}
            value={str(p.value)} onChange={e => p.onChange(e.currentTarget.value || null)} />
        );
      }
      return (
        <TextInput {...common}
          type={p.field.format === 'email' ? 'email' : 'text'}
          maxLength={p.field.maxLength}
          value={str(p.value)} onChange={e => p.onChange(e.currentTarget.value || null)} />
      );
  }
};

// ---------------------------------------------------------------- OperationForm

export interface OperationFormProps {
  form: string;
  initialValues?: Record<string, unknown>;
  initialExtensions?: Record<string, unknown>;
  onSuccess?: (response: OperationResponse) => void;
  submitLabel?: string;
}

interface FieldRuntime {
  field: ManifestField;
  key: string;              // values map key; extensions use "ext:{name}"
}

export function OperationForm(props: OperationFormProps) {
  const tam = useTam();
  const { manifest, client, t, culture } = tam;
  const formDef = manifest.forms[props.form];
  if (!formDef) throw new Error(`Unknown form '${props.form}'`);
  const operation = manifest.operations[formDef.operation];

  const fields = useMemo<FieldRuntime[]>(() => {
    const own = formDef.fields.map(f => ({ field: f, key: f.name }));
    const extension = formDef.includeExtensions && operation.extensibleEntity
      ? (manifest.extensions[operation.extensibleEntity] ?? [])
          .map(f => ({ field: f, key: `ext:${f.name}` }))
      : [];
    return [...own, ...extension];
  }, [formDef, operation, manifest]);

  const initial = useMemo(() => {
    const values: Record<string, unknown> = { ...(props.initialValues ?? {}) };
    for (const [k, v] of Object.entries(props.initialExtensions ?? {})) values[`ext:${k}`] = v;
    return values;
  }, [props.initialValues, props.initialExtensions]);

  const [values, setValues] = useState<Record<string, unknown>>(initial);
  const [touched, setTouched] = useState<Set<string>>(new Set());
  const [resolveState, setResolveState] = useState<ResolveResponse | null>(null);
  const [response, setResponse] = useState<OperationResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const revision = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout>>();

  const getWire = useCallback(
    (name: string) => values[name] ?? values[`ext:${name}`] ?? null,
    [values]);

  // Batched, debounced, stale-rejecting server resolution (docs/05).
  const scheduleResolve = useCallback((changedField: string) => {
    if (!formDef.serverDependencies.includes(changedField)) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(async () => {
      const sent = ++revision.current;
      const input: Record<string, unknown> = {};
      for (const f of formDef.fields) {
        const v = values[f.name];
        if (v !== undefined && v !== null && !f.changeSet) input[f.name] = v;
      }
      input[changedField] = values[changedField] ?? null;
      try {
        const resolved = await client.resolve(props.form, input, [changedField], sent);
        if (sent === revision.current) {
          setResolveState(resolved);
          // Suggestions apply to untouched fields only: RecomputeIfUntouched (docs/05).
          for (const [name, state] of Object.entries(resolved.fields)) {
            if (state.suggestedValue !== undefined && state.suggestedValue !== null
                && !touched.has(name)) {
              setValues(prev => ({ ...prev, [name]: state.suggestedValue }));
            }
          }
        }
      } catch { /* resolve is advisory; submit re-validates authoritatively */ }
    }, 350);
  }, [client, formDef, props.form, values, touched]);

  const setField = useCallback((key: string, value: unknown) => {
    setValues(prev => ({ ...prev, [key]: value }));
    setTouched(prev => new Set(prev).add(key));
    setResponse(null);
  }, []);

  useEffect(() => {
    const last = [...touched].pop();
    if (last && !last.startsWith('ext:')) scheduleResolve(last);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [values]);

  const submit = useCallback(async (overrides?: Record<string, FieldConflict>) => {
    setSubmitting(true);
    try {
      const body: Record<string, unknown> = {};
      const extensions: Record<string, unknown> = {};

      for (const { field, key } of fields) {
        const value = values[key] ?? null;
        if (field.extension) {
          if (!touched.has(key) && initial[key] === undefined) continue;
          const original = overrides?.[`extensions.${field.name}`]?.currentValue
            ?? initial[key] ?? null;
          if (touched.has(key)) extensions[field.name] = { original, value };
          continue;
        }
        if (field.changeSet) {
          if (!touched.has(key)) continue;
          const original = overrides?.[field.name]?.currentValue ?? initial[key] ?? null;
          body[field.name] = { original, value };
          continue;
        }
        if (value !== null && value !== undefined) body[field.name] = value;
      }
      if (Object.keys(extensions).length > 0) body.extensions = extensions;

      const result = await client.operation(formDef.operation, body);
      setResponse(result);
      if (!result.findings.some(f => f.severity === 'error') && !result.conflicts?.length) {
        props.onSuccess?.(result);
      }
    } finally {
      setSubmitting(false);
    }
  }, [client, fields, formDef.operation, initial, props, touched, values]);

  const fieldError = (name: string): Finding | undefined => {
    const fromResolve = resolveState?.fields[name]?.findings
      .find(f => f.severity === 'error');
    const fromResponse = response?.findings
      .find(f => f.severity === 'error' && f.targets.includes(name));
    return fromResponse ?? fromResolve;
  };

  const globalFindings: Finding[] = [
    ...(resolveState?.findings ?? []),
    ...(response?.findings.filter(f => f.targets.length === 0) ?? []),
  ];

  const conflicts = response?.conflicts ?? [];

  return (
    <Stack gap="sm">
      {fields.map(({ field, key }) => {
        const visible = field.visibleWhen ? evalPx(field.visibleWhen, getWire) === true : true;
        if (!visible || field.renderer === 'hidden') return null;
        const required = field.required
          || (field.requiredWhen ? evalPx(field.requiredWhen, getWire) === true : false);
        const error = fieldError(field.extension ? `extensions.${field.name}` : field.name);
        const warning = resolveState?.fields[field.name]?.findings
          .find(f => f.severity === 'warning');
        const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);
        const Renderer = rendererFor(field);
        return (
          <Box key={key}>
            {Renderer({
              field,
              label,
              value: values[key] ?? null,
              onChange: v => setField(key, v),
              required,
              error: error ? findingMessage(manifest, culture, error) : undefined,
              warning: warning ? findingMessage(manifest, culture, warning) : undefined,
              options: resolveState?.fields[field.name]?.options,
              tam,
            })}
          </Box>
        );
      })}

      {globalFindings.map((f, i) => (
        <Alert key={i} color={f.severity === 'error' ? 'red' : f.severity === 'warning' ? 'yellow' : 'blue'}>
          {findingMessage(manifest, culture, f)}
        </Alert>
      ))}

      {conflicts.length > 0 && (
        <Alert color="orange" title={t('concurrency.field-conflict')}>
          <Stack gap="xs">
            {conflicts.map(conflict => (
              <Group key={conflict.field} justify="space-between">
                <Text size="sm">
                  <b>{conflict.field}</b>: «{String(conflict.currentValue ?? '—')}» ↔ «{String(conflict.submittedValue ?? '—')}»
                </Text>
                <Group gap="xs">
                  <Button size="compact-xs" variant="light" onClick={() => {
                    const key = conflict.field.startsWith('extensions.')
                      ? `ext:${conflict.field.slice('extensions.'.length)}`
                      : conflict.field;
                    setField(key, conflict.currentValue);
                    setResponse(null);
                  }}>
                    {culture === 'sv' ? 'Behåll aktuellt' : 'Keep current'}
                  </Button>
                  <Button size="compact-xs" onClick={() =>
                    void submit(Object.fromEntries(conflicts.map(c => [c.field, c])))}>
                    {culture === 'sv' ? 'Använd mitt' : 'Use mine'}
                  </Button>
                </Group>
              </Group>
            ))}
          </Stack>
        </Alert>
      )}

      <Group justify="flex-end" mt="xs">
        <Button loading={submitting} onClick={() => void submit()}>
          {props.submitLabel ?? t(`operations.${formDef.operation}.title`)}
        </Button>
      </Group>
    </Stack>
  );
}

// ---------------------------------------------------------------- ViewGrid

export interface ViewGridProps {
  grid: string;
  query?: Record<string, unknown>;
  onRowClick?: (row: Record<string, unknown>) => void;
  refreshKey?: number;
  onAction?: () => void;
  pageSize?: number;
}

export function ViewGrid(props: ViewGridProps) {
  const tam = useTam();
  const { manifest, client, t, culture, can, subscribeEffects } = tam;
  const gridDef = manifest.grids[props.grid];
  if (!gridDef) throw new Error(`Unknown grid '${props.grid}'`);
  const view = manifest.views[gridDef.view];

  const allowed = (operationId: string) => can(manifest.operations[operationId].permission);
  const toolbarActions = gridDef.toolbarActions.filter(allowed);
  const rowActions = gridDef.rowActions.filter(allowed);

  const resultByName = useMemo(
    () => new Map(view.resultFields.map(f => [f.name, f])),
    [view]);

  const extensionColumns = useMemo(() =>
    gridDef.includeExtensions && view.extensibleEntity
      ? manifest.extensions[view.extensibleEntity] ?? []
      : [],
    [gridDef, view, manifest]);

  const [rows, setRows] = useState<Record<string, unknown>[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [sort, setSort] = useState<string | undefined>(view.defaultSort);
  const [desc, setDesc] = useState(view.defaultSortDescending);
  const [loading, setLoading] = useState(true);
  const [modalAction, setModalAction] = useState<string | null>(null);
  const [localRefresh, setLocalRefresh] = useState(0);
  const pageSize = props.pageSize ?? 15;

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    client.view(gridDef.view, {
      ...props.query, page, pageSize, sort, dir: desc ? 'desc' : 'asc',
    }).then(result => {
      if (!cancelled) { setRows(result.rows); setTotal(result.total); }
    }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [client, gridDef.view, page, pageSize, sort, desc, culture,
      props.refreshKey, localRefresh, JSON.stringify(props.query)]);

  const refresh = () => { setLocalRefresh(x => x + 1); props.onAction?.(); };

  // Live refresh from committed effects (D5): debounced, so bursts collapse into one reload.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const unsubscribe = subscribeEffects(() => {
      clearTimeout(timer);
      timer = setTimeout(() => setLocalRefresh(x => x + 1), 400);
    });
    return () => { clearTimeout(timer); unsubscribe(); };
  }, [subscribeEffects]);

  const cell = (row: Record<string, unknown>, field: ManifestField): React.ReactNode => {
    const value = field.extension
      ? (row.extensions as Record<string, unknown> | undefined)?.[field.name]
      : row[field.name];
    if (value === null || value === undefined) return <Text c="dimmed" size="sm">—</Text>;
    if (field.options) {
      return <Badge variant="light" color={badgeColor(String(value))}>{enumLabel(manifest, culture, value)}</Badge>;
    }
    switch (field.wireKind) {
      case 'boolean': return value === true ? '✓' : '—';
      case 'number': return (
        <Text size="sm" ta="right">
          {new Intl.NumberFormat(culture, field.format === 'money'
            ? { minimumFractionDigits: 2, maximumFractionDigits: 2 } : {}).format(Number(value))}
        </Text>
      );
      default: return <Text size="sm">{String(value)}</Text>;
    }
  };

  const columns: ManifestField[] = [
    ...gridDef.columns.map(c => resultByName.get(c)!).filter(Boolean),
    ...extensionColumns,
  ];

  const formForOperation = (operationId: string) =>
    Object.entries(manifest.forms).find(([, f]) => f.operation === operationId)?.[0];

  const runRowAction = async (operationId: string, row: Record<string, unknown>) => {
    const operation = manifest.operations[operationId];
    const body: Record<string, unknown> = {};
    for (const field of operation.fields) {
      if (!field.required) continue;
      body[field.name] = row[field.name] ?? row.id;
    }
    await client.operation(operationId, body);
    refresh();
  };

  return (
    <Stack gap="sm">
      <Group justify="flex-end">
        {toolbarActions.map(action => (
          <Button key={action} size="sm" onClick={() => setModalAction(action)}>
            {t(`operations.${action}.title`)}
          </Button>
        ))}
      </Group>

      <Table.ScrollContainer minWidth={720}>
        <Table striped highlightOnHover withTableBorder>
          <Table.Thead>
            <Table.Tr>
              {columns.map(field => {
                const sortable = !field.extension && view.sortable.includes(field.name);
                const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);
                return (
                  <Table.Th key={field.name}>
                    {sortable ? (
                      <UnstyledButton onClick={() => {
                        if (sort === field.name) setDesc(d => !d);
                        else { setSort(field.name); setDesc(false); }
                      }}>
                        <Text size="sm" fw={600}>
                          {label}{sort === field.name ? (desc ? ' ↓' : ' ↑') : ''}
                        </Text>
                      </UnstyledButton>
                    ) : <Text size="sm" fw={600}>{label}</Text>}
                  </Table.Th>
                );
              })}
              {rowActions.length > 0 && <Table.Th />}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {loading && rows.length === 0 && (
              <Table.Tr><Table.Td colSpan={columns.length + 1}>
                <Group justify="center" p="md"><Loader size="sm" /></Group>
              </Table.Td></Table.Tr>
            )}
            {rows.map((row, i) => (
              <Table.Tr key={String(row.id ?? i)}
                style={props.onRowClick ? { cursor: 'pointer' } : undefined}
                onClick={() => props.onRowClick?.(row)}>
                {columns.map(field => (
                  <Table.Td key={field.name}>{cell(row, field)}</Table.Td>
                ))}
                {rowActions.length > 0 && (
                  <Table.Td onClick={e => e.stopPropagation()}>
                    <Group gap="xs" justify="flex-end">
                      {rowActions.map(action => (
                        <Button key={action} size="compact-xs" variant="light"
                          onClick={() => void runRowAction(action, row)}>
                          {t(`operations.${action}.title`)}
                        </Button>
                      ))}
                    </Group>
                  </Table.Td>
                )}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>

      <Group justify="space-between">
        <Text size="sm" c="dimmed">{total}</Text>
        <Pagination total={Math.max(1, Math.ceil(total / pageSize))} value={page} onChange={setPage} size="sm" />
      </Group>

      <Modal
        opened={modalAction !== null}
        onClose={() => setModalAction(null)}
        title={modalAction ? <Title order={4}>{t(`operations.${modalAction}.title`)}</Title> : null}
        size="lg"
      >
        {modalAction && (() => {
          const form = formForOperation(modalAction);
          return form
            ? <OperationForm form={form} onSuccess={() => { setModalAction(null); refresh(); }} />
            : <Text>{modalAction}</Text>;
        })()}
      </Modal>
    </Stack>
  );
}

function badgeColor(value: string): string {
  switch (value.toLowerCase()) {
    case 'open': return 'blue';
    case 'completed': case 'active': return 'green';
    case 'cancelled': case 'retired': return 'gray';
    case 'deprecated': return 'yellow';
    case 'project': return 'grape';
    case 'service': return 'cyan';
    default: return 'gray';
  }
}
