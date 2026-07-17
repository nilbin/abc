import React, { useEffect, useState } from 'react';
import { Group, Modal, Stack, Tabs, Text, Title } from '@mantine/core';
import type { RecordSection } from '@tam/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { OperationForm } from './OperationForm';
import { displayFor } from './renderers';
import { PluginSlot } from './PluginSlot';

/**
 * An open record: the detail view's row plus its IDENTITY kept separate from its data — the
 * record key (the list row's id) and the acting node (the row's tenant, when a subtree grid
 * hands us a child company's row). The (id, tenant) convention is read ONCE, in openRecord.
 */
interface OpenRecord {
  row: Record<string, unknown>;
  key: string;
  actAs?: string;
}

// The open record rides the URL (?record=<id>) alongside nav's ?mode=&page= (arc 3c), so a
// record view is deep-linkable and the browser Back button closes it. Written here, not in
// NavProvider, so record routing stays local to the page that owns the record.
const readRecordParam = () =>
  typeof window === 'undefined' ? null
    : new URLSearchParams(window.location.search).get('record');

const writeRecordParam = (id: string | null) => {
  if (typeof window === 'undefined') return;
  const p = new URLSearchParams(window.location.search);
  id ? p.set('record', id) : p.delete('record');
  const qs = p.toString();
  window.history.pushState(null, '', qs ? `?${qs}` : window.location.pathname);
};

/**
 * A framework-composed page (docs/32): ORDERED sections — grids and slots — plus an optional
 * RECORD surface opened by rows of the page's FIRST grid. The record renders as flat sections
 * (form / grid / slot) or grouped into TABS. Declaration order is layout order. Zero app React;
 * registerPage() remains the escape hatch for genuinely custom pages.
 */
export function ModelPage(props: { page: string }) {
  const tam = useTam();
  const { manifest, client, t, can, invalidate } = tam;
  const page = manifest.pages?.[props.page];
  const [record, setRecord] = useState<OpenRecord | null>(null);
  if (!page) throw new Error(`Unknown page '${props.page}'`);
  const rec = page.record;
  const primaryGrid = page.sections.find(s => s.kind === 'grid')?.id;

  // Fetch + open the record for an id (deep-link / row click). Row click also passes the row's
  // tenant for cross-company subtree rows; a deep-link opens in the acting node.
  const openById = async (id: string, actAs?: string) => {
    if (!rec) return;
    const detail = await client.view(rec.detailView, { [rec.key]: id },
      actAs ? { actAs } : undefined);
    setRecord(detail.rows[0] ? { row: detail.rows[0], key: id, actAs } : null);
  };

  const openRecord = async (row: Record<string, unknown>) => {
    if (!rec) return;
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    await openById(String(row.id), actAs);
    writeRecordParam(String(row.id));
  };

  const closeRecord = () => { setRecord(null); writeRecordParam(null); };

  // Deep-link + Back/forward: the URL's ?record drives what's open. On mount (or popstate) open
  // it if present, close if gone. Keyed on the page so switching pages re-evaluates.
  useEffect(() => {
    if (!rec) return;
    const sync = () => {
      const id = readRecordParam();
      if (id) { if (record?.key !== id) void openById(id); }
      else setRecord(null);
    };
    sync();
    window.addEventListener('popstate', sync);
    return () => window.removeEventListener('popstate', sync);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.page]);

  const firstForm = rec?.sections.find(s => s.kind === 'form')?.id
    ?? rec?.tabs.flatMap(tb => tb.sections).find(s => s.kind === 'form')?.id;
  const titleOperation = firstForm ? manifest.forms[firstForm]?.operation : undefined;
  const title = rec && record
    ? [
        titleOperation ? t(`operations.${titleOperation}.title`) : '',
        rec.titleField ? String(record.row[rec.titleField] ?? '') : '',
      ].filter(Boolean).join(' — ')
    : '';

  const section = (s: RecordSection): React.ReactNode => {
    if (!record || !rec) return null;
    if (s.kind === 'form') {
      const form = manifest.forms[s.id];
      if (!form || !can(manifest.operations[form.operation]?.permission ?? '')) return null;
      return (
        <OperationForm
          key={s.id}
          form={s.id}
          actAs={record.actAs}
          initialValues={Object.fromEntries(form.fields
            .map(f => [f.name, f.name === rec.key ? record.key : record.row[f.name]])
            .filter(([, v]) => v !== undefined))}
          initialExtensions={(record.row.extensions as Record<string, unknown>) ?? {}}
          onSuccess={r => { closeRecord(); invalidate(r.effects); }}
        />
      );
    }
    if (s.kind === 'grid') {
      // A child listing filtered off the open record: each bind param ← a record detail field.
      const query = Object.fromEntries(
        Object.entries(s.bind ?? {}).map(([param, field]) => [param, record.row[field]]));
      return <ViewGrid key={s.id} grid={s.id} query={query} actAs={record.actAs} />;
    }
    return (
      <PluginSlot key={s.id} id={s.id}
        context={{ [rec.key]: record.key }} actAs={record.actAs} />
    );
  };

  const recordBody = () => {
    if (!record || !rec) return null;
    if (rec.tabs.length > 0) {
      return (
        <Tabs defaultValue={rec.tabs[0].id}>
          <Tabs.List>
            {rec.tabs.map(tb => (
              <Tabs.Tab key={tb.id} value={tb.id}>{t(tb.headingKey)}</Tabs.Tab>
            ))}
          </Tabs.List>
          {rec.tabs.map(tb => (
            <Tabs.Panel key={tb.id} value={tb.id} pt="md">
              <Stack gap="md">{tb.sections.map(section)}</Stack>
            </Tabs.Panel>
          ))}
        </Tabs>
      );
    }
    // Flat: a record with NO form (a plugin read-model) shows the detail fields read-only
    // through the SAME display cascade as the grid cells, then its sections.
    return (
      <Stack gap="md">
        {!rec.sections.some(s => s.kind === 'form') && (
          <Stack gap={6}>
            {(manifest.views[rec.detailView]?.resultFields ?? [])
              .filter(f => f.name !== 'id' && f.name !== 'version'
                && f.wireKind !== 'object' && record.row[f.name] !== undefined
                && record.row[f.name] !== null)
              .map(f => (
                <Group key={f.name} gap="xs" wrap="nowrap">
                  <Text size="sm" c="dimmed" w={160}>{t(f.labelKey)}</Text>
                  {displayFor(f)({ field: f, value: record.row[f.name], tam })}
                </Group>
              ))}
          </Stack>
        )}
        {rec.sections.map(section)}
      </Stack>
    );
  };

  return (
    <Stack gap="lg">
      {page.sections.map(s => (
        <Stack key={s.id} gap="xs">
          {s.headingKey && <Title order={5}>{t(s.headingKey)}</Title>}
          {s.kind === 'grid'
            ? (
              <ViewGrid
                grid={s.id}
                onRowClick={rec && s.id === primaryGrid ? row => void openRecord(row) : undefined}
              />
            )
            : <PluginSlot id={s.id} context={{}} />}
        </Stack>
      ))}
      {rec && (
        <Modal opened={record !== null} onClose={closeRecord}
          title={<Title order={4}>{title}</Title>} size="lg">
          {recordBody()}
        </Modal>
      )}
    </Stack>
  );
}
