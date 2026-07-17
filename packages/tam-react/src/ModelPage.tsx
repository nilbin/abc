import React, { useState } from 'react';
import { Group, Modal, Stack, Text, Title } from '@mantine/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { OperationForm } from './OperationForm';
import { displayFor } from './renderers';
import { PluginSlot } from './PluginSlot';

/**
 * An open record: the detail view's row plus its IDENTITY kept separate from its data — the
 * record key (the list row's id) and the acting node (the row's tenant, when a subtree grid
 * hands us a child company's row). This replaces the old habit of stuffing `__rowId` into the
 * row object and reading `row.tenantId` at the render sites: the (id, tenant) convention is
 * read ONCE, in openRecord, and everything downstream reads named fields.
 */
interface OpenRecord {
  row: Record<string, unknown>;
  key: string;
  actAs?: string;
}

/**
 * A framework-composed page (docs/32): ORDERED sections — grids and slots — plus an optional
 * RECORD surface (ordered form/slot sections) opened by rows of the page's FIRST grid.
 * Declaration order is layout order. The standard shapes with zero app React; registerPage()
 * remains the escape hatch for genuinely custom pages.
 */
export function ModelPage(props: { page: string }) {
  const tam = useTam();
  const { manifest, client, t, can, invalidate } = tam;
  const page = manifest.pages?.[props.page];
  const [record, setRecord] = useState<OpenRecord | null>(null);
  if (!page) throw new Error(`Unknown page '${props.page}'`);
  const rec = page.record;
  const primaryGrid = page.sections.find(s => s.kind === 'grid')?.id;

  const openRecord = async (row: Record<string, unknown>) => {
    if (!rec) return;
    // The (id, tenant) row convention, read once: subtree grids may hand us a child company's
    // row, so read + act in the ROW's node.
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    const detail = await client.view(rec.detailView, { [rec.key]: row.id },
      actAs ? { actAs } : undefined);
    setRecord(detail.rows[0] ? { row: detail.rows[0], key: String(row.id), actAs } : null);
  };

  const firstForm = rec?.sections.find(s => s.kind === 'form')?.id;
  const titleOperation = firstForm ? manifest.forms[firstForm]?.operation : undefined;
  const title = rec && record
    ? [
        titleOperation ? t(`operations.${titleOperation}.title`) : '',
        rec.titleField ? String(record.row[rec.titleField] ?? '') : '',
      ].filter(Boolean).join(' — ')
    : '';

  const recordSection = (section: { kind: string; id: string }) => {
    if (!record || !rec) return null;
    if (section.kind === 'form') {
      const form = manifest.forms[section.id];
      if (!form || !can(manifest.operations[form.operation]?.permission ?? '')) return null;
      return (
        <OperationForm
          key={section.id}
          form={section.id}
          actAs={record.actAs}
          initialValues={Object.fromEntries(form.fields
            .map(f => [f.name, f.name === rec.key ? record.key : record.row[f.name]])
            .filter(([, v]) => v !== undefined))}
          initialExtensions={(record.row.extensions as Record<string, unknown>) ?? {}}
          onSuccess={r => { setRecord(null); invalidate(r.effects); }}
        />
      );
    }
    return (
      <PluginSlot key={section.id} id={section.id}
        context={{ [rec.key]: record.key }} actAs={record.actAs} />
    );
  };

  return (
    <Stack gap="lg">
      {page.sections.map(section => (
        <Stack key={section.id} gap="xs">
          {/* Multi-section pages label their sections (docs/34 M6) — the key is
              L10N001-gated server-side, so t() always has text. */}
          {section.headingKey && <Title order={5}>{t(section.headingKey)}</Title>}
          {section.kind === 'grid'
            ? (
              <ViewGrid
                grid={section.id}
                onRowClick={rec && section.id === primaryGrid ? row => void openRecord(row) : undefined}
              />
            )
            : <PluginSlot id={section.id} context={{}} />}
        </Stack>
      ))}
      {rec && (
        <Modal opened={record !== null} onClose={() => setRecord(null)}
          title={<Title order={4}>{title}</Title>} size="lg">
          {/* A record with NO form (a plugin's read-model page, docs/32) shows the detail
              view's fields read-only through the SAME display cascade as the grid cells. */}
          {record && !rec.sections.some(s => s.kind === 'form') && (
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
          )}
          {record && rec.sections.map(recordSection)}
        </Modal>
      )}
    </Stack>
  );
}
