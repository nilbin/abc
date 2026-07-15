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
  /** Subscribe to committed-operation effects over SSE (decision D5); returns unsubscribe.
   *  Identity is stable across renders — safe to use directly in effect dependencies. */
  subscribeEffects: (callback: () => void) => () => void;
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
  const effectListeners = useRef(new Set<() => void>());

  const refreshManifest = useCallback(async () => {
    setManifest(await props.client.manifest());
  }, [props.client]);

  useEffect(() => { void refreshManifest(); }, [refreshManifest]);

  useEffect(() => {
    // EventSource cannot set headers, so the acting-company header (docs/26 D-H4) rides a query
    // param instead — the server applies the same standable validation either way.
    const actAs = props.client.actingAs;
    const query = actAs ? `?actAs=${encodeURIComponent(actAs)}` : '';
    const source = new EventSource(`${props.client.baseUrl}/api/events${query}`);
    source.onmessage = () => effectListeners.current.forEach(listener => listener());
    return () => source.close();
  }, [props.client.baseUrl, props.client.actingAs]);

  const subscribeEffects = useCallback((callback: () => void) => {
    effectListeners.current.add(callback);
    return () => { effectListeners.current.delete(callback); };
  }, []);

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
    subscribeEffects,
  }), [manifest, culture, props.client, setCulture, refreshManifest, subscribeEffects]);

  if (!value) {
    return <Group justify="center" p="xl"><Loader /></Group>;
  }
  return <TamContext.Provider value={value}>{props.children}</TamContext.Provider>;
}
