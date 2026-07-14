import { useEffect, useMemo, useRef, useState } from 'react';
import { TamAuth, TamClient, type TamAuthOptions, type TamSession, type TamUser } from '@tam/core';

export type TamAuthStatus = 'loading' | 'anonymous' | 'authenticated';

export interface TamAuthState {
  status: TamAuthStatus;
  user: TamUser | null;
  /** Start the Authorization Code + PKCE redirect to the framework login. */
  signIn: () => void;
  /** Forget the token (a fresh signIn re-authenticates). */
  signOut: () => void;
}

/**
 * Auth for a Tam SPA in one line: on mount it rehydrates a stored token, and if the browser is on the
 * PKCE callback it redeems the code — wiring the bearer header onto the client throughout. The app
 * renders a sign-in affordance when anonymous and its shell when authenticated; the login page and
 * tenant picker themselves are the framework's (server-rendered). No PKCE code in the app (docs/26).
 */
export function useTamAuth(client: TamClient, options: TamAuthOptions): TamAuthState {
  // One TamAuth per client+clientId; options are treated as stable (as they are in practice).
  const auth = useMemo(() => new TamAuth(client, options), [client, options.clientId]);
  const [session, setSession] = useState<TamSession | null>(() => auth.restore());
  const [loading, setLoading] = useState(() => auth.isCallback());
  const started = useRef(false);

  useEffect(() => {
    if (!auth.isCallback() || started.current) return;
    started.current = true;   // guard React 18 StrictMode's double-invoke: redeem the code once
    auth.completeCallback()
      .then(next => setSession(next))
      .finally(() => setLoading(false));
  }, [auth]);

  return {
    status: loading ? 'loading' : session ? 'authenticated' : 'anonymous',
    user: session?.user ?? null,
    signIn: () => void auth.signIn(),
    signOut: () => { auth.signOut(); setSession(null); },
  };
}
