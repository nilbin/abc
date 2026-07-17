import React, { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { NavLink, SegmentedControl, Stack, Tabs } from '@mantine/core';
import type { Manifest, NavNode } from '@tam/core';
import { useTam } from './context';
import { ViewGrid } from './ViewGrid';
import { ModelPage } from './ModelPage';

// Navigation runtime (docs/30): the model carries pure depth + kind; THESE components map depth
// to UI slots (mode switcher → sidebar → tabs). Visibility derives from the bound surface's
// permission — nav is discoverability, never authorization (D-N6).

/** App-owned custom pages: register by the key a { page } nav target names. */
const pages = new Map<string, () => ReactNode>();
export function registerPage(key: string, render: () => ReactNode): void {
  pages.set(key, render);
}

function visible(node: NavNode, manifest: Manifest, can: (p: string) => boolean): boolean {
  if (node.target?.grid) {
    const view = manifest.grids[node.target.grid]?.view;
    return view ? can(manifest.views[view]?.permission ?? '') : false;
  }
  if (node.target?.page) {
    if (node.permission) return can(node.permission);
    // A DECLARED page (docs/32) derives visibility from its FIRST grid's view.
    const declared = manifest.pages?.[node.target.page];
    const grid = declared?.sections.find(s => s.kind === 'grid')?.id;
    const view = grid ? manifest.grids[grid]?.view : undefined;
    return view ? can(manifest.views[view]?.permission ?? '') : false;
  }
  if (node.target?.plugin) {
    return Object.values(manifest.grids).some(g =>
      g.plugin === node.target!.plugin && can(manifest.views[g.view]?.permission ?? ''));
  }
  return node.children.some(c => visible(c, manifest, can));
}

function prune(nodes: NavNode[], manifest: Manifest, can: (p: string) => boolean): NavNode[] {
  return nodes
    .filter(n => visible(n, manifest, can))
    .map(n => ({ ...n, children: prune(n.children, manifest, can) }));
}

export interface NavState {
  /** The permission-filtered tree for this surface (default "web"). */
  modes: NavNode[];
  activeMode: NavNode | null;
  setMode: (id: string) => void;
  /** The active page node (depth 1 within the mode) and sub-page (depth 2), if any. */
  active: NavNode | null;
  activeSub: NavNode | null;
  navigate: (id: string) => void;
}

const NavStateContext = createContext<NavState | null>(null);

/** Computes the effective nav off the manifest and carries mode/page selection. */
export function NavProvider(props: { surface?: string; children: ReactNode }) {
  const { manifest, can } = useTam();
  const surface = props.surface ?? 'web';
  const modes = useMemo(
    () => prune(manifest.nav?.[surface] ?? manifest.nav?.web ?? [], manifest, can),
    [manifest, can, surface]);
  const [modeId, setModeId] = useState<string | null>(null);
  const [pageId, setPageId] = useState<string | null>(null);

  const activeMode = modes.find(m => m.id === modeId) ?? modes[0] ?? null;

  const flat: NavNode[] = useMemo(() => {
    const acc: NavNode[] = [];
    const walk = (n: NavNode) => { acc.push(n); n.children.forEach(walk); };
    activeMode?.children.forEach(walk);
    return acc;
  }, [activeMode]);

  const firstPage = (nodes: NavNode[]): NavNode | null => {
    for (const n of nodes) {
      if (n.kind === 'page') return n;
      const inner = firstPage(n.children);
      if (inner) return inner;
    }
    return null;
  };
  const active = flat.find(n => n.id === pageId && n.kind === 'page')
    ?? (pageId ? flat.find(n => n.children.some(c => c.id === pageId)) : null)
    ?? firstPage(activeMode?.children ?? []);
  const activeSub = active?.children.find(c => c.id === pageId && c.id !== active.id)
    ?? (active && active.children.length > 0 ? firstPage(active.children) : null);

  const state: NavState = {
    modes,
    activeMode,
    setMode: id => { setModeId(id); setPageId(null); },
    active: active ?? null,
    activeSub,
    navigate: id => setPageId(id),
  };
  return <NavStateContext.Provider value={state}>{props.children}</NavStateContext.Provider>;
}

export function useNav(): NavState {
  const state = useContext(NavStateContext);
  if (!state) throw new Error('useNav requires <NavProvider>');
  return state;
}

/** Depth 0: the mode switcher. Renders nothing for a single-mode app. */
export function NavModeSwitcher() {
  const { manifest, t } = useTam();
  const nav = useNav();
  if (nav.modes.length < 2) return null;
  return (
    <SegmentedControl
      size="xs"
      value={nav.activeMode?.id}
      onChange={nav.setMode}
      data={nav.modes.map(m => ({ value: m.id, label: t(m.labelKey) }))}
    />
  );
}

/** Depth 1: sections as headers, pages as links, in the left sidebar. */
export function NavSidebar() {
  const { t } = useTam();
  const nav = useNav();
  const render = (node: NavNode): ReactNode => node.kind === 'section'
    ? (
      <NavLink key={node.id} label={t(node.labelKey)} defaultOpened childrenOffset={12}>
        {node.children.map(render)}
      </NavLink>
    )
    : (
      <NavLink
        key={node.id}
        label={t(node.labelKey)}
        active={nav.active?.id === node.id}
        onClick={() => nav.navigate(node.id)}
      />
    );
  return <>{nav.activeMode?.children.map(render)}</>;
}

/** Depth 2: the active page's sub-pages as tabs across the top of the page. */
export function NavTabs() {
  const { t } = useTam();
  const nav = useNav();
  const subs = nav.active?.children.filter(c => c.kind === 'page') ?? [];
  if (subs.length === 0) return null;
  return (
    <Tabs value={nav.activeSub?.id} onChange={id => id && nav.navigate(id)}>
      <Tabs.List>
        {subs.map(s => <Tabs.Tab key={s.id} value={s.id}>{t(s.labelKey)}</Tabs.Tab>)}
      </Tabs.List>
    </Tabs>
  );
}

/** Renders the ACTIVE page: registered custom page, single grid, or the generic per-plugin
 *  grid stack (the mechanical fallback — PluginPage, absorbed into the framework). A write on an
 *  admin grid that flips the effective manifest (activation, role edits, field defs) names a
 *  system entity, so invalidateForEffects refetches the manifest for us — no per-page wiring. */
export function NavPage() {
  const { manifest } = useTam();
  const nav = useNav();
  const node = nav.activeSub ?? nav.active;
  if (!node?.target) return null;
  if (node.target.page) {
    const registered = pages.get(node.target.page);
    if (registered) return <>{registered()}</>;
    // Framework-composed page (docs/32): grid + record surface, zero app React.
    if (manifest.pages?.[node.target.page]) return <ModelPage page={node.target.page} />;
    return null;
  }
  if (node.target.grid) return <ViewGrid grid={node.target.grid} />;
  if (node.target.plugin) {
    const grids = Object.entries(manifest.grids)
      .filter(([, g]) => g.plugin === node.target!.plugin)
      .map(([id]) => id);
    return (
      <Stack gap="lg">
        {grids.map(id => <ViewGrid key={id} grid={id} />)}
      </Stack>
    );
  }
  return null;
}
