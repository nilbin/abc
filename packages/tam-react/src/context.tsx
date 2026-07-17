import React, {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState,
} from 'react';
import { Group, Loader } from '@mantine/core';
import { Manifest, TamClient, translate } from '@tam/core';

export interface TamContextValue {
  client: TamClient;
  manifest: Manifest;
  culture: string;
  setCulture: (culture: string) => void;
  refreshManifest: () => Promise<void>;
  t: (key: string, args?: Record<string, unknown>) => string;
  /** Effective-permission check from the manifest's actor overlay (decision D1). */
  can: (permission: string) => boolean;
  /**
   * The data-invalidation bus (decision D5). A single monotonic counter that bumps whenever
   * server data changed — a committed write (form success, row action) OR a committed effect
   * arriving over SSE (debounced, so bursts collapse into one bump). Anything reading server
   * data depends on it and reloads; this ONE signal replaces the old refreshKey prop / internal
   * localRefresh state / per-grid SSE subscription / onAction callback tangle.
   */
  dataVersion: number;
  /** Publish to the bus: a write committed, reload every subscribed view now (no SSE wait). */
  invalidate: () => void;
}

const TamContext = createContext<TamContextValue | null>(null);

export function useTam(): TamContextValue {
  const context = useContext(TamContext);
  if (!context) throw new Error('useTam must be used inside <TamProvider>');
  return context;
}

export function TamProvider(props: {
  client: TamClient;
  initialCulture?: string;
  children: React.ReactNode;
}) {
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [culture, setCultureState] = useState(props.initialCulture ?? props.client.culture);
  const [dataVersion, setDataVersion] = useState(0);

  const invalidate = useCallback(() => setDataVersion(v => v + 1), []);

  const refreshManifest = useCallback(async () => {
    setManifest(await props.client.manifest());
  }, [props.client]);

  useEffect(() => { void refreshManifest(); }, [refreshManifest]);

  useEffect(() => {
    // Committed effects (D5) publish to the ONE bus, debounced so a burst collapses into a
    // single reload across every subscribed view. EventSource cannot set headers, so the
    // acting-company header (docs/26 D-H4) rides a query param instead — the server applies
    // the same standable validation either way.
    const actAs = props.client.actingAs;
    const query = actAs ? `?actAs=${encodeURIComponent(actAs)}` : '';
    const source = new EventSource(`${props.client.baseUrl}/api/events${query}`);
    let timer: ReturnType<typeof setTimeout>;
    source.onmessage = () => { clearTimeout(timer); timer = setTimeout(invalidate, 400); };
    return () => { clearTimeout(timer); source.close(); };
  }, [props.client.baseUrl, props.client.actingAs, invalidate]);

  const setCulture = useCallback((next: string) => {
    props.client.culture = next;
    setCultureState(next);
  }, [props.client]);

  const value = useMemo<TamContextValue | null>(() => manifest && ({
    client: props.client,
    manifest,
    culture,
    setCulture,
    refreshManifest,
    t: (key: string, args?: Record<string, unknown>) => translate(manifest, culture, key, args),
    can: (permission: string) => {
      const granted = manifest.actorPermissions ?? ['*'];
      return granted.includes('*')
        || granted.includes(permission)
        || granted.includes(`${permission}:own`);
    },
    dataVersion,
    invalidate,
  }), [manifest, culture, props.client, setCulture, refreshManifest, dataVersion, invalidate]);

  if (!value) {
    return <Group justify="center" p="xl"><Loader /></Group>;
  }
  return <TamContext.Provider value={value}>{props.children}</TamContext.Provider>;
}

/**
 * Runs `callback` whenever data is invalidated — NOT on mount. For side effects a bare view
 * reload doesn't cover: an admin surface refetching the effective manifest after a write that
 * changed permissions, nav, or fields (grids already reload off `dataVersion` themselves).
 */
export function useInvalidation(callback: () => void): void {
  const { dataVersion } = useTam();
  const mounted = useRef(false);
  useEffect(() => {
    if (!mounted.current) { mounted.current = true; return; }
    callback();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dataVersion]);
}
