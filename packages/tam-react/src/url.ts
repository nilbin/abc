// The app's ONE URL grammar (arc 3c nav + arc 4 records + deep links):
// ?mode=&page=&record=&tenant=&tab=. Navigation state is bookmarkable and Back/forward-able;
// every writer goes through writeQuery so the params compose instead of clobbering each
// other. Subordination: `record` is meaningless on another page (page changes clear it),
// `tab` is meaningless on another record (record changes clear it), and `tenant` names the
// ACTING node for what the URL shows — the global picker writes it, and a cross-company
// subtree row writes it alongside `record` so the deep link re-establishes the scope the
// record lives in (docs/26 D-H4: the server still validates on every request).

export interface UrlQuery {
  mode: string | null;
  page: string | null;
  record: string | null;
  tenant: string | null;
  tab: string | null;
}

export const readQuery = (): UrlQuery => {
  const p = new URLSearchParams(typeof window === 'undefined' ? '' : window.location.search);
  return {
    mode: p.get('mode'), page: p.get('page'), record: p.get('record'),
    tenant: p.get('tenant'), tab: p.get('tab'),
  };
};

/** Applies a partial update to the URL query via pushState. Keys absent from the patch keep
 *  their current value; a null value removes the param. */
export const writeQuery = (patch: Partial<UrlQuery>): void => {
  if (typeof window === 'undefined') return;
  const p = new URLSearchParams(window.location.search);
  for (const key of ['mode', 'page', 'record', 'tenant', 'tab'] as const) {
    if (!(key in patch)) continue;
    const value = patch[key];
    value === null || value === undefined ? p.delete(key) : p.set(key, value);
  }
  const qs = p.toString();
  window.history.pushState(null, '', qs ? `?${qs}` : window.location.pathname);
};
