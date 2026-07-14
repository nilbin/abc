import React, { useEffect, useMemo, useState } from 'react';
import {
  Badge, Button, Group, Loader, Modal, Pagination, Stack, Table, Text, Title, UnstyledButton,
} from '@mantine/core';
import { ManifestField, enumLabel } from '@tam/core';
import { useTam } from './context';
import { OperationForm } from './OperationForm';

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

  // Unknown action ids are a manifest-integrity problem; skip them instead of crashing the grid.
  const allowed = (operationId: string) => {
    const operation = manifest.operations[operationId];
    return operation !== undefined && can(operation.permission);
  };
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

  // Stable dependency for the caller-supplied filter object (key order normalized).
  const queryKey = useMemo(
    () => JSON.stringify(Object.entries(props.query ?? {}).sort(([a], [b]) => a.localeCompare(b))),
    [props.query]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    client.view(gridDef.view, {
      ...props.query, page, pageSize, sort, dir: desc ? 'desc' : 'asc',
    }).then(result => {
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
    ...gridDef.columns
      .map(c => resultByName.get(c))
      .filter((f): f is ManifestField => f !== undefined),
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
