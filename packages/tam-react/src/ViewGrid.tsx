import React, { useEffect, useMemo, useState } from 'react';
import {
  Badge, Button, Group, Loader, Modal, NumberInput, Pagination, Select, Stack, Table, Text,
  TextInput, Title, UnstyledButton,
} from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import { ManifestField, enumLabel, toWireEnum } from '@tam/core';
import { useTam } from './context';
import { OperationForm } from './OperationForm';

export interface ViewGridProps {
  grid: string;
  query?: Record<string, unknown>;
  onRowClick?: (row: Record<string, unknown>) => void;
  refreshKey?: number;
  onAction?: () => void;
  pageSize?: number;
  /** Read/act in another standable node (docs/26 D-H4) — slot panels on cross-company rows. */
  actAs?: string;
}

export function ViewGrid(props: ViewGridProps) {
  const tam = useTam();
  const { manifest, client, t, culture, can, subscribeEffects } = tam;
  const gridDef = manifest.grids[props.grid];
  if (!gridDef) throw new Error(`Unknown grid '${props.grid}'`);
  const view = manifest.views[gridDef.view];

  // Unknown action ids are a manifest-integrity problem; skip them instead of crashing the grid.
  const allowed = (operationId: string) => {
    const operation = manifest.operations[operationId];
    return operation !== undefined && can(operation.permission);
  };
  const toolbarActions = gridDef.toolbarActions.filter(allowed);
  const rowActions = gridDef.rowActions.filter(allowed);
  // Plugin-contributed actions (docs/31 D-X1): rendered after host actions, same permission
  // gate; the DECLARED bind replaces the name-convention input mapping below.
  const contributedActions = (gridDef.contributedActions ?? []).filter(a => allowed(a.operation));

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
  const [filters, setFilters] = useState<Record<string, string>>({});
  const pageSize = props.pageSize ?? 15;

  // Subtree views (docs/26 D-H1): the manifest names the result field carrying each row's
  // tenant. The standable list supplies display names + the acting node; the company column
  // and tenant filter appear only when there is more than one company to tell apart, and row
  // actions execute in the ROW's node via per-call act-as (validated server-side).
  const subtreeField = view.subtree;
  const [companies, setCompanies] = useState<{ id: string; display: string }[]>([]);
  const [acting, setActing] = useState<string | null>(null);
  useEffect(() => {
    if (!subtreeField) return;
    let cancelled = false;
    client.standable()
      .then(info => { if (!cancelled) { setCompanies(info.nodes); setActing(info.active); } })
      .catch(() => undefined);
    return () => { cancelled = true; };
  }, [client, subtreeField]);
  const grouped = subtreeField !== undefined && companies.length > 1;
  const companyName = (id: unknown) =>
    companies.find(c => c.id === id)?.display ?? String(id ?? '');

  // Declared-filterable fields render controls mechanically — the control set derives from the
  // field's wire kind exactly as the server derives the operators: equality for enums/booleans,
  // from/to ranges for dates and numbers, substring for strings. Extension string fields filter
  // via "ext.{key}" — possible precisely because filtering is not baked into a Query record.
  const filterFields = view.filterable
    .map(f => view.resultFields.find(r => r.name === f))
    .filter((f): f is ManifestField => f !== undefined);
  const extensionFilterFields = extensionColumns.filter(f =>
    ['string', 'number', 'integer', 'date', 'boolean'].includes(f.wireKind));

  const setFilter = (key: string, value: string | null) => {
    setPage(1);
    setFilters(prev => {
      const next = { ...prev };
      if (value === null || value === '') delete next[key];
      else next[key] = value;
      return next;
    });
  };

  // Stable dependency for the caller-supplied filter object (key order normalized).
  const queryKey = useMemo(
    () => JSON.stringify(Object.entries({ ...props.query, ...filters })
      .sort(([a], [b]) => a.localeCompare(b))),
    [props.query, filters]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    client.view(gridDef.view, {
      ...props.query, ...filters, page, pageSize, sort, dir: desc ? 'desc' : 'asc',
    }, props.actAs ? { actAs: props.actAs } : undefined).then(result => {
      if (!cancelled) { setRows(result.rows); setTotal(result.total); }
    }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, gridDef.view, page, pageSize, sort, desc, culture,
      props.refreshKey, localRefresh, queryKey]);

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
    if (field.name === subtreeField) return <Text size="sm">{companyName(value)}</Text>;
    if (field.options) {
      return <Badge variant="light" color={badgeColor(String(value))}>{enumLabel(manifest, culture, value)}</Badge>;
    }
    switch (field.wireKind) {
      case 'boolean': return value === true ? '✓' : '—';
      case 'number': return (
        <Text size="sm" ta="right">
          {new Intl.NumberFormat(culture, (field.format === 'money' || field.renderer === 'money')
            ? { minimumFractionDigits: 2, maximumFractionDigits: 2 } : {}).format(Number(value))}
        </Text>
      );
      default: return <Text size="sm">{String(value)}</Text>;
    }
  };

  const columns: ManifestField[] = [
    ...gridDef.columns
      .map(c => resultByName.get(c))
      .filter((f): f is ManifestField => f !== undefined)
      .filter(f => f.name !== subtreeField || grouped),
    ...extensionColumns,
  ];

  const formForOperation = (operationId: string) =>
    Object.entries(manifest.forms).find(([, f]) => f.operation === operationId)?.[0];

  // Known limitation: required input fields map to same-named row columns (orderId → row.id
  // fallback). A declared action-input mapping in the manifest is the designed replacement.
  const runRowAction = async (operationId: string, row: Record<string, unknown>) => {
    const operation = manifest.operations[operationId];
    const body: Record<string, unknown> = {};
    for (const field of operation.fields) {
      if (!field.required) continue;
      body[field.name] = row[field.name] ?? row.id;
    }
    const rowTenant = (subtreeField ? (row[subtreeField] as string | undefined) : undefined)
      ?? props.actAs;
    await client.operation(operationId, body,
      rowTenant && rowTenant !== acting ? { actAs: rowTenant } : undefined);
    refresh();
  };

  const runContributedAction = async (
    action: { operation: string; bind: Record<string, string> },
    row: Record<string, unknown>,
  ) => {
    const body: Record<string, unknown> = {};
    for (const [input, column] of Object.entries(action.bind)) body[input] = row[column];
    const rowTenant = (subtreeField ? (row[subtreeField] as string | undefined) : undefined)
      ?? props.actAs;
    await client.operation(action.operation, body,
      rowTenant && rowTenant !== acting ? { actAs: rowTenant } : undefined);
    refresh();
  };

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="flex-end">
        <Group gap="xs">
          {filterFields.map(field => field.name === subtreeField
            ? (grouped ? <Select key={field.name} size="xs" clearable searchable
                placeholder={t(field.labelKey)}
                data={companies.map(c => ({ value: c.id, label: c.display }))}
                value={filters[field.name] ?? null}
                onChange={v => setFilter(field.name, v)} /> : null)
            : <FilterControl key={field.name} field={field}
                filters={filters} setFilter={setFilter} />)}
          {extensionFilterFields.map(field => <FilterControl key={`ext.${field.name}`}
            field={field} filterKey={`ext.${field.name}`}
            filters={filters} setFilter={setFilter} />)}
        </Group>
        <Group>
          {toolbarActions.map(action => (
            <Button key={action} size="sm" onClick={() => setModalAction(action)}>
              {t(`operations.${action}.title`)}
            </Button>
          ))}
        </Group>
      </Group>

      <Table.ScrollContainer minWidth={720}>
        <Table striped highlightOnHover withTableBorder>
          <Table.Thead>
            <Table.Tr>
              {columns.map(field => {
                // Extension columns sort mechanically ("sort=ext.{key}") like they filter.
                const sortKey = field.extension ? `ext.${field.name}` : field.name;
                const sortable = field.extension || view.sortable.includes(field.name);
                const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);
                return (
                  <Table.Th key={field.name}>
                    {sortable ? (
                      <UnstyledButton onClick={() => {
                        if (sort === sortKey) setDesc(d => !d);
                        else { setSort(sortKey); setDesc(false); }
                      }}>
                        <Text size="sm" fw={600}>
                          {label}{sort === sortKey ? (desc ? ' ↓' : ' ↑') : ''}
                        </Text>
                      </UnstyledButton>
                    ) : <Text size="sm" fw={600}>{label}</Text>}
                  </Table.Th>
                );
              })}
              {(rowActions.length > 0 || contributedActions.length > 0) && <Table.Th />}
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
                {(rowActions.length > 0 || contributedActions.length > 0) && (
                  <Table.Td onClick={e => e.stopPropagation()}>
                    <Group gap="xs" justify="flex-end">
                      {rowActions.map(action => (
                        <Button key={action} size="compact-xs" variant="light"
                          onClick={() => void runRowAction(action, row)}>
                          {t(`operations.${action}.title`)}
                        </Button>
                      ))}
                      {contributedActions.map(action => (
                        <Button key={action.operation} size="compact-xs" variant="light"
                          onClick={() => void runContributedAction(action, row)}>
                          {t(`operations.${action.operation}.title`)}
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
        <Text size="sm" c="dimmed">{t('grid.total', { count: total })}</Text>
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

/** One declared-filterable field → the control set its wire kind supports (mirrors the
 *  server). Extension fields pass filterKey="ext.{key}" — same controls, same operators. */
function FilterControl(props: {
  field: ManifestField;
  filters: Record<string, string>;
  setFilter: (key: string, value: string | null) => void;
  filterKey?: string;
}) {
  const { manifest, culture, t } = useTam();
  const { field, filters, setFilter } = props;
  const key = props.filterKey ?? field.name;
  const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);

  if (field.options) {
    return (
      <Select
        size="xs" w={150} placeholder={label} clearable
        data={field.options.map(o => ({
          value: toWireEnum(o),
          label: enumLabel(manifest, culture, o),
        }))}
        value={filters[key] ?? null}
        onChange={v => setFilter(key, v)}
      />
    );
  }

  switch (field.wireKind) {
    case 'boolean':
      return (
        <Select
          size="xs" w={120} placeholder={label} clearable
          data={[
            { value: 'true', label: t('common.yes') },
            { value: 'false', label: t('common.no') },
          ]}
          value={filters[key] ?? null}
          onChange={v => setFilter(key, v)}
        />
      );
    case 'number':
    case 'integer':
      return (
        <Group gap={4}>
          {(['from', 'to'] as const).map(bound => (
            <NumberInput
              key={bound} size="xs" w={110} hideControls
              placeholder={`${label} ${t(`filters.${bound}`)}`}
              value={filters[`${key}.${bound}`] ?? ''}
              onChange={v => setFilter(`${key}.${bound}`,
                v === '' || v === null ? null : String(v))}
            />
          ))}
        </Group>
      );
    case 'date':
      return (
        <Group gap={4}>
          {(['from', 'to'] as const).map(bound => (
            <DateInput
              key={bound} size="xs" w={130} clearable valueFormat="YYYY-MM-DD"
              placeholder={`${label} ${t(`filters.${bound}`)}`}
              value={filters[`${key}.${bound}`]
                ? dayjs(filters[`${key}.${bound}`]).toDate() : null}
              onChange={v => setFilter(`${key}.${bound}`,
                v ? dayjs(v).format('YYYY-MM-DD') : null)}
            />
          ))}
        </Group>
      );
    default:
      return (
        <TextInput
          size="xs" w={150} placeholder={label}
          value={filters[`${key}.contains`] ?? ''}
          onChange={e => setFilter(`${key}.contains`, e.currentTarget.value)}
        />
      );
  }
}

// Framework registry states only; the app registers its DOMAIN enum colors (docs/13: the
// framework owns semantics, the app owns pixels).
const badgeColors = new Map<string, string>([
  ['active', 'green'], ['retired', 'gray'], ['deprecated', 'yellow'],
]);

/** App-owned pixels: map enum wire values (lowercase) to Mantine badge colors. */
export function registerBadgeColors(colors: Record<string, string>): void {
  for (const [value, color] of Object.entries(colors)) badgeColors.set(value.toLowerCase(), color);
}

function badgeColor(value: string): string {
  return badgeColors.get(value.toLowerCase()) ?? 'gray';
}
