import React, { useState } from 'react';
import { Modal, Title } from '@mantine/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { OperationForm } from './OperationForm';
import { PluginSlot } from './PluginSlot';

/**
 * A framework-composed page (docs/32): the declared grid, whose rows open the declared RECORD
 * surface — detail fetch by key, edit form prefilled from same-named detail fields, and the
 * declared slots' plugin panels. The standard list-and-detail shape with zero app React;
 * registerPage() remains the escape hatch for genuinely custom pages.
 */
export function ModelPage(props: { page: string }) {
  const { manifest, client, t, can, refreshManifest } = useTam();
  const page = manifest.pages?.[props.page];
  const [record, setRecord] = useState<Record<string, unknown> | null>(null);
  const [rowTenant, setRowTenant] = useState<string | undefined>(undefined);
  const [refreshKey, setRefreshKey] = useState(0);
  if (!page) throw new Error(`Unknown page '${props.page}'`);
  const rec = page.record;

  const openRecord = async (row: Record<string, unknown>) => {
    if (!rec) return;
    // Subtree grids may hand us a child company's row: read + act in the ROW's node.
    const actAs = typeof row.tenantId === 'string' ? row.tenantId : undefined;
    const detail = await client.view(rec.detailView, { [rec.key]: row.id },
      actAs ? { actAs } : undefined);
    setRowTenant(actAs);
    setRecord(detail.rows[0] ? { ...detail.rows[0], __rowId: row.id } : null);
  };

  const form = rec?.form ? manifest.forms[rec.form] : undefined;
  const formAllowed = form !== undefined
    && can(manifest.operations[form.operation]?.permission ?? '');
  const title = rec && record
    ? [
        form ? t(`operations.${form.operation}.title`) : '',
        rec.titleField ? String(record[rec.titleField] ?? '') : '',
      ].filter(Boolean).join(' — ')
    : '';

  return (
    <>
      <ViewGrid
        grid={page.grid}
        onRowClick={rec ? row => void openRecord(row) : undefined}
        refreshKey={refreshKey}
        onAction={() => void refreshManifest()}
      />
      {rec && (
        <Modal opened={record !== null} onClose={() => setRecord(null)}
          title={<Title order={4}>{title}</Title>} size="lg">
          {record && formAllowed && form && (
            <OperationForm
              form={rec.form!}
              actAs={rowTenant}
              initialValues={Object.fromEntries(form.fields
                .map(f => [f.name, f.name === rec.key ? record.__rowId : record[f.name]])
                .filter(([, v]) => v !== undefined))}
              initialExtensions={(record.extensions as Record<string, unknown>) ?? {}}
              onSuccess={() => { setRecord(null); setRefreshKey(k => k + 1); }}
            />
          )}
          {record && rec.slots.map(slotId => (
            <PluginSlot key={slotId} id={slotId}
              context={{ [rec.key]: record.__rowId }} actAs={rowTenant} />
          ))}
        </Modal>
      )}
    </>
  );
}
