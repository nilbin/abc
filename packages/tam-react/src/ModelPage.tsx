import React, { useState } from 'react';
import { Group, Modal, Stack, Text, Title } from '@mantine/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { OperationForm } from './OperationForm';
import { PluginSlot } from './PluginSlot';

/**
 * A framework-composed page (docs/32): ORDERED sections — grids and slots — plus an optional
 * RECORD surface (ordered form/slot sections) opened by rows of the page's FIRST grid.
 * Declaration order is layout order. The standard shapes with zero app React; registerPage()
 * remains the escape hatch for genuinely custom pages.
 */
export function ModelPage(props: { page: string }) {
  const { manifest, client, t, can, refreshManifest } = useTam();
  const page = manifest.pages?.[props.page];
  const [record, setRecord] = useState<Record<string, unknown> | null>(null);
  const [rowTenant, setRowTenant] = useState<string | undefined>(undefined);
  const [refreshKey, setRefreshKey] = useState(0);
  if (!page) throw new Error(`Unknown page '${props.page}'`);
  const rec = page.record;
  const primaryGrid = page.sections.find(s => s.kind === 'grid')?.id;

  const openRecord = async (row: Record<string, unknown>) => {
    if (!rec) return;
    // Subtree grids may hand us a child company's row: read + act in the ROW's node.
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    const detail = await client.view(rec.detailView, { [rec.key]: row.id },
      actAs ? { actAs } : undefined);
    setRowTenant(actAs);
    setRecord(detail.rows[0] ? { ...detail.rows[0], __rowId: row.id } : null);
  };

  const firstForm = rec?.sections.find(s => s.kind === 'form')?.id;
  const titleOperation = firstForm ? manifest.forms[firstForm]?.operation : undefined;
  const title = rec && record
    ? [
        titleOperation ? t(`operations.${titleOperation}.title`) : '',
        rec.titleField ? String(record[rec.titleField] ?? '') : '',
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
          actAs={rowTenant}
          initialValues={Object.fromEntries(form.fields
            .map(f => [f.name, f.name === rec.key ? record.__rowId : record[f.name]])
            .filter(([, v]) => v !== undefined))}
          initialExtensions={(record.extensions as Record<string, unknown>) ?? {}}
          onSuccess={() => { setRecord(null); setRefreshKey(k => k + 1); }}
        />
      );
    }
    return (
      <PluginSlot key={section.id} id={section.id}
        context={{ [rec.key]: record.__rowId }} actAs={rowTenant} />
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
                refreshKey={refreshKey}
                onAction={() => void refreshManifest()}
              />
            )
            : <PluginSlot id={section.id} context={{}} />}
        </Stack>
      ))}
      {rec && (
        <Modal opened={record !== null} onClose={() => setRecord(null)}
          title={<Title order={4}>{title}</Title>} size="lg">
          {/* A record with NO form (a plugin's read-model page, docs/32) shows the detail
              view's fields read-only — otherwise the modal would be a bare title. */}
          {record && !rec.sections.some(s => s.kind === 'form') && (
            <Stack gap={6} mb="md">
              {(manifest.views[rec.detailView]?.resultFields ?? [])
                .filter(f => f.name !== 'id' && f.name !== 'version'
                  && f.wireKind !== 'object' && record[f.name] !== undefined
                  && record[f.name] !== null)
                .map(f => (
                  <Group key={f.name} gap="xs" wrap="nowrap">
                    <Text size="sm" c="dimmed" w={160}>{t(f.labelKey)}</Text>
                    <Text size="sm">{String(record[f.name])}</Text>
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
