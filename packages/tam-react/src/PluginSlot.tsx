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
  /** Render only ONE plugin's panels — the panel-tabs expansion (docs/32) mounts one
   *  PluginSlot per plugin tab. The tab already carries the plugin's title, so panels
   *  without their own headingKey render unheaded here. */
  plugin?: string;
}

/** The slot's panels the acting user may see — the permission gate every consumer applies. */
export function visiblePanels(
  manifest: ReturnType<typeof useTam>['manifest'],
  can: (permission: string) => boolean,
  slotId: string,
) {
  return (manifest.slots?.[slotId] ?? []).filter(panel => {
    const gridDef = manifest.grids[panel.grid];
    const view = gridDef ? manifest.views[gridDef.view] : undefined;
    return view !== undefined && can(view.permission);
  });
}

/**
 * A host contribution point (docs/31 D-X4): the host drops this ONE line into a surface and
 * every active plugin's contributed panel renders here — its own grid, query-bound to the
 * slot's record context, behind the same permission gate as any grid. Empty slots render
 * nothing at all.
 */
export function PluginSlot(props: PluginSlotProps) {
  const { manifest, t, can } = useTam();
  const panels = visiblePanels(manifest, can, props.id)
    .filter(panel => props.plugin === undefined || panel.plugin === props.plugin);
  if (panels.length === 0) return null;

  return (
    <Stack gap="md" mt="md">
      {panels.map(panel => {
        const query: Record<string, unknown> = {};
        for (const [queryField, contextKey] of Object.entries(panel.bind))
          query[queryField] = props.context[contextKey];
        const heading = props.plugin !== undefined
          ? panel.headingKey   // inside a plugin tab the tab title covers the fallback
          : panel.headingKey ?? `plugins.${panel.plugin}.title`;
        return (
          <Stack key={panel.grid} gap="xs">
            {heading && <Text size="sm" fw={600}>{t(heading)}</Text>}
            <ViewGrid grid={panel.grid} query={query} actAs={props.actAs} pageSize={5} />
          </Stack>
        );
      })}
    </Stack>
  );
}
