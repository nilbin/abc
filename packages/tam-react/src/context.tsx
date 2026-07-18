import React, {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState,
} from 'react';
import { Group, Loader } from '@mantine/core';
import {
  QueryClient, QueryClientProvider, useQuery, useQueryClient, type UseQueryResult,
} from '@tanstack/react-query';
import { Manifest, TamClient, ViewResponse, translate } from '@tam/core';

export interface TamContextValue {
  client: TamClient;
  manifest: Manifest;
  culture: string;
  setCulture: (culture: string) => void;
  refreshManifest: () => Promise<void>;
  t: (key: string, args?: Record<string, unknown>) => string;
  /**
   * First key that RESOLVES wins (an unresolved key echoes itself). The convention-key
   * fallback: a surface-specific key (`operations.x.submit`, `operations.x.action`) overlays
   * a general one without every catalog having to ship every variant.
   */
  tOr: (keys: string[], args?: Record<string, unknown>) => string;
  /** Effective-permission check from the manifest's actor overlay (decision D1). */
  can: (permission: string) => boolean;
  /**
   * Invalidate cached server state after a committed write (decision D5). Pass the operation
   * response's `effects` (or the SSE payload's) and invalidation is TARGETED: each
   * `entity-modified` effect invalidates only the views over that entity (mapped through the
   * manifest's `extensibleEntity`), and a system/config write additionally refetches the
   * manifest. TanStack Query owns the cache, dedup and stale-while-revalidate underneath.
   */
  invalidate: (effects?: ReadonlyArray<Record<string, unknown>>) => void;
}

const TamContext = createContext<TamContextValue | null>(null);

export function useTam(): TamContextValue {
  const context = useContext(TamContext);
  if (!context) throw new Error('useTam must be used inside <TamProvider>');
  return context;
}

/** The query key for a view read: prefix `['view', id]` so a targeted invalidation of one
 *  view catches every page/filter/act-as/culture variant of it. */
export const viewKey = (
  viewId: string, params: Record<string, unknown>, actAs: string | undefined, culture: string,
) => ['view', viewId, params, actAs ?? null, culture] as const;

/**
 * A cached view read (TanStack Query). Every grid, picker and panel reads through this, so two
 * surfaces on the same view share one request and a committed write reloads exactly them.
 */
export function useView(
  viewId: string, params: Record<string, unknown>, options?: { actAs?: string; enabled?: boolean },
): UseQueryResult<ViewResponse> {
  const { client, culture } = useTam();
  return useQuery({
    queryKey: viewKey(viewId, params, options?.actAs, culture),
    queryFn: () => client.view(viewId, params, options?.actAs ? { actAs: options.actAs } : undefined),
    enabled: options?.enabled ?? true,
    placeholderData: prev => prev,   // keep the old page visible while the next loads
  });
}

/**
 * Turns a set of committed effects into precise cache invalidations. Domain writes name an
 * EXTENSIBLE entity (`order`, `project`, …) → only that entity's views reload. A system/config
 * write (roles, activations, fields, nav) names a non-extensible entity, or the effect set is
 * unknown → the effective manifest may have changed, so refetch it plus every view. The domain
 * vs system split derives from the manifest's own `extensibleEntity` set — no hardcoded list.
 */
function invalidateForEffects(
  queryClient: QueryClient, manifest: Manifest, effects: ReadonlyArray<Record<string, unknown>>,
): void {
  const viewsByEntity = new Map<string, string[]>();
  const extensible = new Set<string>();
  for (const [id, view] of Object.entries(manifest.views)) {
    const entity = view.extensibleEntity;
    if (!entity) continue;
    extensible.add(entity);
    (viewsByEntity.get(entity) ?? viewsByEntity.set(entity, []).get(entity)!).push(id);
  }

  const entities = effects
    .map(e => (typeof e.entity === 'string' ? e.entity : null))
    .filter((e): e is string => e !== null);
  const systemWrite = entities.length === 0 || entities.some(e => !extensible.has(e));

  for (const entity of entities) {
    for (const id of viewsByEntity.get(entity) ?? [])
      void queryClient.invalidateQueries({ queryKey: ['view', id] });
  }
  if (systemWrite) {
    // config/permissions/nav may have changed — the manifest and every view are suspect.
    void queryClient.invalidateQueries({ queryKey: ['manifest'] });
    void queryClient.invalidateQueries({ queryKey: ['view'] });
  }
}

export function TamProvider(props: {
  client: TamClient;
  initialCulture?: string;
  children: React.ReactNode;
}) {
  // One client per provider instance: App.tsx remounts the provider on identity/act-as change,
  // so a tenant switch starts with a clean cache by construction.
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: {
      queries: { staleTime: 15_000, refetchOnWindowFocus: false, retry: 1 },
    },
  }));
  return (
    <QueryClientProvider client={queryClient}>
      <TamInner client={props.client} initialCulture={props.initialCulture}>
        {props.children}
      </TamInner>
    </QueryClientProvider>
  );
}

function TamInner(props: {
  client: TamClient;
  initialCulture?: string;
  children: React.ReactNode;
}) {
  const queryClient = useQueryClient();
  const [culture, setCultureState] = useState(props.initialCulture ?? props.client.culture);

  // The manifest is just another cached resource: refreshManifest() invalidates it; a
  // system/config write invalidates it through invalidateForEffects.
  const { data: manifest } = useQuery({
    queryKey: ['manifest'],
    queryFn: () => props.client.manifest(),
    staleTime: 60_000,
  });

  const refreshManifest = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ['manifest'] });
  }, [queryClient]);

  const invalidate = useCallback((effects?: ReadonlyArray<Record<string, unknown>>) => {
    if (!manifest) return;
    invalidateForEffects(queryClient, manifest, effects ?? []);
  }, [queryClient, manifest]);

  // The SSE effect reads invalidate through a ref: its identity changes with the manifest, and
  // re-running the effect would tear down the EventSource mid-debounce (dropping a batch) for
  // what is only a callback swap.
  const invalidateRef = useRef(invalidate);
  invalidateRef.current = invalidate;

  useEffect(() => {
    // Committed effects (D5) invalidate the same way a local write does — the SSE payload carries
    // the operation's effects, so live refresh is as targeted as a mutation. Debounced so a burst
    // collapses. EventSource cannot set headers, so the acting-company header (docs/26 D-H4) rides
    // a query param; the server applies the same standable validation either way.
    const actAs = props.client.actingAs;
    const query = actAs ? `?actAs=${encodeURIComponent(actAs)}` : '';
    const source = new EventSource(`${props.client.baseUrl}/api/events${query}`);
    let timer: ReturnType<typeof setTimeout>;
    let pending: Record<string, unknown>[] = [];
    source.onmessage = event => {
      try {
        const parsed = JSON.parse(event.data) as { effects?: Record<string, unknown>[] };
        if (parsed.effects) pending.push(...parsed.effects);
      } catch { /* a keep-alive comment or malformed frame — ignore */ }
      clearTimeout(timer);
      timer = setTimeout(() => { const batch = pending; pending = []; invalidateRef.current(batch); }, 400);
    };
    return () => { clearTimeout(timer); source.close(); };
  }, [props.client.baseUrl, props.client.actingAs]);

  const setCulture = useCallback((next: string) => {
    props.client.culture = next;
    setCultureState(next);
  }, [props.client]);

  const value = useMemo<TamContextValue | null>(() => manifest ? ({
    client: props.client,
    manifest,
    culture,
    setCulture,
    refreshManifest,
    t: (key: string, args?: Record<string, unknown>) => translate(manifest, culture, key, args),
    tOr: (keys: string[], args?: Record<string, unknown>) => {
      for (const key of keys) {
        const value = translate(manifest, culture, key, args);
        if (value !== key) return value;
      }
      return keys[keys.length - 1];
    },
    can: (permission: string) => {
      const granted = manifest.actorPermissions ?? ['*'];
      return granted.includes('*')
        || granted.includes(permission)
        || granted.includes(`${permission}:own`);
    },
    invalidate,
  }) : null, [manifest, culture, props.client, setCulture, refreshManifest, invalidate]);

  if (!value) {
    return <Group justify="center" p="xl"><Loader /></Group>;
  }
  return <TamContext.Provider value={value}>{props.children}</TamContext.Provider>;
}
