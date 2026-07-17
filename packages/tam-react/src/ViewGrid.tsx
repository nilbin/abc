import React, { useEffect, useMemo, useState } from 'react';
import {
  Alert, Button, Group, Loader, Modal, Pagination, Select, Stack, Table, Text,
  Title, UnstyledButton,
} from '@mantine/core';
import { GridAction, ManifestField, toWireEnum } from '@tam/core';
import { useTam, useView } from './context';
import { OperationForm } from './OperationForm';
import { displayFor } from './renderers';
import { FilterControl } from './GridFilters';

export interface ViewGridProps {
  grid: string;
  query?: Record<string, unknown>;
  onRowClick?: (row: Record<string, unknown>) => void;
  pageSize?: number;
  /** Read/act in another standable node (docs/26 D-H4) — slot panels on cross-company rows. */
  actAs?: string;
}

export function ViewGrid(props: ViewGridProps) {
  const tam = useTam();
  const { manifest, client, t, culture, can, invalidate } = tam;
  const gridDef = manifest.grids[props.grid];
  if (!gridDef) throw new Error(`Unknown grid '${props.grid}'`);
  const view = manifest.views[gridDef.view];

  // Unknown action ids are a manifest-integrity problem; skip them instead of crashing the grid.
  const allowed = (operationId: string) => {
    const operation = manifest.operations[operationId];
    return operation !== undefined && can(operation.permission);
  };
  // ONE action list (docs/32): placement × mode decides rendering; a declared bind (plugin
  // contributions, docs/31 D-X1) replaces the same-name input convention.
  const actions = (gridDef.actions ?? []).filter(a => allowed(a.operation));
  const toolbarActions = actions.filter(a => a.placement === 'toolbar');
  const rowActionList = actions.filter(a => a.placement === 'row');

  const resultByName = useMemo(
    () => new Map(view.resultFields.map(f => [f.name, f])),
    [view]);

  const extensionColumns = useMemo(() =>
    gridDef.includeExtensions && view.extensibleEntity
      ? manifest.extensions[view.extensibleEntity] ?? []
      : [],
    [gridDef, view, manifest]);

  const [page, setPage] = useState(1);
  const [sort, setSort] = useState<string | undefined>(view.defaultSort);
  const [desc, setDesc] = useState(view.defaultSortDescending);
  // The open form modal: an operation, plus initial values when opened as a row EDIT (RowForm).
  const [modalAction, setModalAction] =
    useState<{ operation: string; initial?: Record<string, unknown> } | null>(null);
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

  // The view read is a TanStack query (context.useView): keyed by view + params + act-as +
  // culture, so two grids on the same view dedupe, remounts hit cache, and a committed write
  // invalidates exactly this key. loading/error are the query's own states — no hand-rolled
  // fetch effect, no rows/total/loading useState.
  const query = { ...props.query, ...filters, page, pageSize, sort, dir: desc ? 'desc' : 'asc' };
  const result = useView(gridDef.view, query, props.actAs ? { actAs: props.actAs } : undefined);
  const rows = result.data?.rows ?? [];
  const total = result.data?.total ?? 0;
  const loading = result.isPending;
  const loadError = result.isError;

  // A cell is a value display (renderers.displayFor) — the same cascade the record read view
  // uses. The one grid-local concern is the subtree column, which maps a tenant id to its
  // company display name; everything else is the shared registry.
  const cell = (row: Record<string, unknown>, field: ManifestField): React.ReactNode => {
    const value = field.extension
      ? (row.extensions as Record<string, unknown> | undefined)?.[field.name]
      : row[field.name];
    if (field.name === subtreeField) {
      return value === null || value === undefined
        ? <Text c="dimmed" size="sm">—</Text>
        : <Text size="sm">{companyName(value)}</Text>;
    }
    return displayFor(field)({ field, value, tam });
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

  // Execute-mode: the declared bind (input ← row column) wins; without one, required inputs
  // map to same-named row columns with the row's id as fallback (orderId → row.id).
  const runRowAction = async (action: GridAction, row: Record<string, unknown>) => {
    const body: Record<string, unknown> = {};
    if (action.bind) {
      for (const [input, column] of Object.entries(action.bind)) body[input] = row[column];
    } else {
      for (const field of manifest.operations[action.operation].fields) {
        if (!field.required) continue;
        body[field.name] = row[field.name] ?? row.id;
      }
    }
    const rowTenant = (subtreeField ? (row[subtreeField] as string | undefined) : undefined)
      ?? props.actAs;
    const response = await client.operation(action.operation, body,
      rowTenant && rowTenant !== acting ? { actAs: rowTenant } : undefined);
    invalidate(response.effects);
  };

  // Form-mode on a row (docs/32): the row's result fields prefill same-named form fields —
  // the contract an upsert operation's list view provides deliberately (rules.list ↔ define).
  const openRowForm = (operationId: string, row: Record<string, unknown>) => {
    const form = formForOperation(operationId);
    if (!form) return;
    const initial: Record<string, unknown> = {};
    for (const field of manifest.forms[form].fields) {
      if (row[field.name] !== undefined) initial[field.name] = row[field.name];
    }
    setModalAction({ operation: operationId, initial });
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
            <Button key={action.operation} size="sm"
              onClick={() => action.mode === 'form'
                ? setModalAction({ operation: action.operation })
                : void client.operation(action.operation, {}).then(r => invalidate(r.effects))}>
              {t(`operations.${action.operation}.title`)}
            </Button>
          ))}
        </Group>
      </Group>

      {loadError && (
        <Alert color="red" variant="light">{t('grid.load-failed')}</Alert>
      )}

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
              {rowActionList.length > 0 && <Table.Th />}
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
                {rowActionList.length > 0 && (
                  <Table.Td onClick={e => e.stopPropagation()}>
                    <Group gap="xs" justify="flex-end">
                      {rowActionList.map(action => (
                        <Button key={action.operation} size="compact-xs" variant="light"
                          onClick={() => action.mode === 'form'
                            ? openRowForm(action.operation, row)
                            : void runRowAction(action, row)}>
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
        title={modalAction
          ? <Title order={4}>{t(`operations.${modalAction.operation}.title`)}</Title>
          : null}
        size="lg"
      >
        {modalAction && (() => {
          const form = formForOperation(modalAction.operation);
          return form
            ? <OperationForm form={form} initialValues={modalAction.initial}
                onSuccess={r => { setModalAction(null); invalidate(r.effects); }} />
            : <Text>{modalAction.operation}</Text>;
        })()}
      </Modal>
    </Stack>
  );
}
