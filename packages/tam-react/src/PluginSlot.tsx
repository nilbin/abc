import React from 'react';
import { Stack, Text } from '@mantine/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';

export interface PluginSlotProps {
  /** The host-declared slot id (docs/31 D-X4), e.g. "web.orders.detail". */
  id: string;
  /** The record context the slot provides — keys match the slot's declared context keys. */
  context: Record<string, unknown>;
  /** Execute panel reads in another standable node (a subtree grid's cross-company row). */
  actAs?: string;
}

/**
 * A host contribution point (docs/31 D-X4): the host drops this ONE line into a surface and
 * every active plugin's contributed panel renders here — its own grid, query-bound to the
 * slot's record context, behind the same permission gate as any grid. Empty slots render
 * nothing at all.
 */
export function PluginSlot(props: PluginSlotProps) {
  const { manifest, t, can } = useTam();
  const panels = (manifest.slots?.[props.id] ?? []).filter(panel => {
    const gridDef = manifest.grids[panel.grid];
    const view = gridDef ? manifest.views[gridDef.view] : undefined;
    return view !== undefined && can(view.permission);
  });
  if (panels.length === 0) return null;

  return (
    <Stack gap="md" mt="md">
      {panels.map(panel => {
        const query: Record<string, unknown> = {};
        for (const [queryField, contextKey] of Object.entries(panel.bind))
          query[queryField] = props.context[contextKey];
        return (
          <Stack key={panel.grid} gap="xs">
            <Text size="sm" fw={600}>{t(panel.headingKey ?? `plugins.${panel.plugin}.title`)}</Text>
            <ViewGrid grid={panel.grid} query={query} actAs={props.actAs} pageSize={5} />
          </Stack>
        );
      })}
    </Stack>
  );
}
