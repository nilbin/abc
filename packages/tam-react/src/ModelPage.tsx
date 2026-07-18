import React, { useEffect, useRef, useState } from 'react';
import { Button, Group, Modal, Stack, Tabs, Text, Title } from '@mantine/core';
import type { RecordSection } from '@tam/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { OperationForm } from './OperationForm';
import { displayFor } from './renderers';
import { PluginSlot, visiblePanels } from './PluginSlot';
import { readQuery, writeQuery } from './url';

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

/** A render-ready record tab: panel-tab markers already expanded to one tab per plugin. */
interface ResolvedTab {
  id: string;
  headingKey?: string;
  sections: RecordSection[];
  /** Set on an expanded plugin tab: render the slot filtered to this plugin. */
  slot?: string;
  plugin?: string;
}

/**
 * A framework-composed page (docs/32): ORDERED sections — grids and slots — plus an optional
 * RECORD surface opened by rows of the page's FIRST grid. A record is ALWAYS tabs (flat
 * authoring arrives as one implicit heading-less tab); tab chrome renders only when there is a
 * choice. Zero app React; registerPage() remains the escape hatch for custom pages.
 */
export function ModelPage(props: { page: string }) {
  const tam = useTam();
  const { manifest, client, t, can, invalidate } = tam;
  const page = manifest.pages?.[props.page];
  const [record, setRecord] = useState<OpenRecord | null>(null);
  // The active record tab, URL-backed (?tab): a record's documents tab is a bookmarkable
  // place. Null renders the first tab, exactly the old default.
  const [tab, setTab] = useState<string | null>(() => readQuery().tab);
  // The URL-sync effect reads the CURRENT open record through this ref — state in a listener
  // closure would be stale (the popstate handler outlives renders).
  const recordRef = useRef<OpenRecord | null>(null);
  recordRef.current = record;
  if (!page) throw new Error(`Unknown page '${props.page}'`);
  const rec = page.record;
  const primaryGrid = page.sections.find(s => s.kind === 'grid')?.id;

  // Fetch the record for an id (row click passes the row's tenant for cross-company subtree
  // rows; a deep link opens in the acting node). A missing row or a failed fetch clears the
  // record AND the URL param — the URL must never claim a record the modal doesn't show.
  // The URL's ?tenant restores to the GLOBAL acting node (client.actingAs); leaving a
  // cross-company record must put it back, not blank it.
  const globalTenant = () => client.actingAs ?? null;

  const openById = async (id: string, actAs?: string) => {
    if (!rec) return;
    try {
      const detail = await client.view(rec.detailView, { [rec.key]: id },
        actAs ? { actAs } : undefined);
      if (detail.rows[0]) {
        setRecord({ row: detail.rows[0], key: id, actAs });
      } else {
        setRecord(null);
        writeQuery({ record: null, tenant: globalTenant(), tab: null });
      }
    } catch {
      setRecord(null);
      writeQuery({ record: null, tenant: globalTenant(), tab: null });
    }
  };

  const openRecord = async (row: Record<string, unknown>) => {
    if (!rec) return;
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    // A cross-company subtree row carries its tenant into the URL: the deep link must
    // re-establish the scope the record LIVES in, not the scope it was found from.
    writeQuery({ record: String(row.id), tenant: actAs ?? globalTenant(), tab: null });
    await openById(String(row.id), actAs);
  };

  const closeRecord = () => {
    setRecord(null);
    writeQuery({ record: null, tenant: globalTenant(), tab: null });
  };

  // Deep-link + Back/forward: the URL's ?record (+?tenant when it differs from the acting
  // node, +?tab) drives what's open. NavPage keys this component by page, so a page switch
  // remounts with fresh state and this effect re-syncs.
  useEffect(() => {
    if (!rec) return;
    const sync = () => {
      const q = readQuery();
      setTab(q.tab);
      if (q.record === null) setRecord(null);
      else if (recordRef.current?.key !== q.record) {
        void openById(q.record,
          q.tenant && q.tenant !== client.actingAs ? q.tenant : undefined);
      }
    };
    sync();
    window.addEventListener('popstate', sync);
    return () => window.removeEventListener('popstate', sync);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Panel-tab markers expand into one tab per contributing PLUGIN (docs/31 D-X4): heading from
  // the plugin's first panel headingKey or its title — the host named neither.
  const resolvedTabs: ResolvedTab[] = (rec?.tabs ?? []).flatMap(tb => {
    if (!tb.slot) return [tb as ResolvedTab];
    const plugins = [...new Set(visiblePanels(manifest, can, tb.slot).map(p => p.plugin))];
    return plugins.map(plugin => ({
      id: `${tb.id}:${plugin}`,
      headingKey: visiblePanels(manifest, can, tb.slot!)
        .find(p => p.plugin === plugin)?.headingKey ?? `plugins.${plugin}.title`,
      sections: [],
      slot: tb.slot,
      plugin,
    }));
  });

  const firstForm = resolvedTabs.flatMap(tb => tb.sections).find(s => s.kind === 'form')?.id;
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
      // A child listing filtered off the open record: each bind param ← a record detail
      // field — or, for a "$ref:{entityKey}" bind (docs/35), the record's own EntityRef
      // ("order:{id}"): the documents-tab shape, filtering on WHICH record.
      const query = Object.fromEntries(
        Object.entries(s.bind ?? {}).map(([param, field]) => [param,
          field.startsWith('$ref:') ? `${field.slice(5)}:${record.key}` : record.row[field]]));
      return <ViewGrid key={s.id} grid={s.id} query={query} actAs={record.actAs} />;
    }
    return (
      <PluginSlot key={s.id} id={s.id}
        context={{ [rec.key]: record.key }} actAs={record.actAs} />
    );
  };

  // A record with NO form anywhere (a plugin read-model) shows the detail view's fields
  // read-only through the SAME display cascade as the grid cells.
  const readOnlyDetails = () => {
    if (!record || !rec) return null;
    if (resolvedTabs.some(tb => tb.sections.some(s => s.kind === 'form'))) return null;
    return (
      <Stack gap={6} mb="md">
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
    );
  };

  const tabBody = (tb: ResolvedTab): React.ReactNode =>
    tb.slot && record && rec
      ? <PluginSlot id={tb.slot} plugin={tb.plugin}
          context={{ [rec.key]: record.key }} actAs={record.actAs} />
      : <Stack gap="md">{tb.sections.map(section)}</Stack>;

  const recordBody = () => {
    if (!record || !rec) return null;
    // Tab chrome only when there is a choice: a single heading-less (implicit) tab renders
    // its sections directly.
    if (resolvedTabs.length === 1 && !resolvedTabs[0].headingKey) {
      return <Stack gap="md">{readOnlyDetails()}{tabBody(resolvedTabs[0])}</Stack>;
    }
    return (
      <Stack gap="md">
        {readOnlyDetails()}
        <Tabs
          value={resolvedTabs.some(tb => tb.id === tab) ? tab : resolvedTabs[0]?.id}
          onChange={id => { setTab(id); writeQuery({ tab: id }); }}>

          <Tabs.List>
            {resolvedTabs.map(tb => (
              <Tabs.Tab key={tb.id} value={tb.id}>
                {t(tb.headingKey ?? tb.id)}
              </Tabs.Tab>
            ))}
          </Tabs.List>
          {resolvedTabs.map(tb => (
            <Tabs.Panel key={tb.id} value={tb.id} pt="md">
              {tabBody(tb)}
            </Tabs.Panel>
          ))}
        </Tabs>
      </Stack>
    );
  };

  // A PAGE record is a workspace (docs/32): the routed record surface replaces the grid —
  // deep-linkable, and Back (or the button) returns to the listing. A MODAL record stays a
  // quick edit over it. The semantic came from the model; this is just the mapping to pixels.
  if (rec?.display === 'page' && record !== null) {
    return (
      <Stack gap="md">
        <Group gap="sm">
          <Button variant="subtle" size="compact-sm" onClick={closeRecord}>
            ← {t('common.back')}
          </Button>
          <Title order={3}>{title}</Title>
        </Group>
        {recordBody()}
      </Stack>
    );
  }

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
      {rec && rec.display !== 'page' && (
        <Modal opened={record !== null} onClose={closeRecord}
          title={<Title order={4}>{title}</Title>} size="lg">
          {recordBody()}
        </Modal>
      )}
    </Stack>
  );
}
