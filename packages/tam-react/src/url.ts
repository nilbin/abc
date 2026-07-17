// The app's ONE URL grammar (arc 3c nav + arc 4 records): ?mode=&page=&record=. Navigation
// state is bookmarkable and Back/forward-able; every writer goes through writeQuery so the
// params compose instead of clobbering each other. `record` is subordinate to `page` — any
// navigation that changes the page clears it (a record id is meaningless on another page).

export interface UrlQuery {
  mode: string | null;
  page: string | null;
  record: string | null;
}

export const readQuery = (): UrlQuery => {
  const p = new URLSearchParams(typeof window === 'undefined' ? '' : window.location.search);
  return { mode: p.get('mode'), page: p.get('page'), record: p.get('record') };
};

/** Applies a partial update to the URL query via pushState. Keys absent from the patch keep
 *  their current value; a null value removes the param. */
export const writeQuery = (patch: Partial<UrlQuery>): void => {
  if (typeof window === 'undefined') return;
  const p = new URLSearchParams(window.location.search);
  for (const key of ['mode', 'page', 'record'] as const) {
    if (!(key in patch)) continue;
    const value = patch[key];
    value === null || value === undefined ? p.delete(key) : p.set(key, value);
  }
  const qs = p.toString();
  window.history.pushState(null, '', qs ? `?${qs}` : window.location.pathname);
};
