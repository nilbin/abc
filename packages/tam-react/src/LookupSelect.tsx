import React, { useEffect, useRef, useState } from 'react';
import { Loader, Select } from '@mantine/core';
import { useTam, useView } from './context';

export interface LookupSelectProps {
  /** Lookup view id (e.g. "customers.lookup"). The view's Search query member drives the search. */
  view: string;
  value: unknown;
  onChange: (value: string | null) => void;
  label?: string;
  required?: boolean;
  error?: string;
  description?: string;
  /** Row member used as the option value. */
  idField?: string;
  /** Row member used as the option label. */
  labelField?: string;
  /** Query member the typed text binds to. */
  searchParam?: string;
  pageSize?: number;
}

/**
 * Reference picker backed by a lookup view: the typed text becomes a debounced server-side
 * search through the same cached view reads as the grids (TanStack Query), so repeating a search
 * is instant and the same lookup shared across pickers dedupes. The current selection stays
 * visible even when the latest search response doesn't contain it.
 */
export function LookupSelect(p: LookupSelectProps) {
  const { t } = useTam();
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  const selected = useRef<{ value: string; label: string } | null>(null);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(search), search ? 200 : 0);
    return () => clearTimeout(timer);
  }, [search]);

  const result = useView(p.view, {
    [p.searchParam ?? 'search']: debounced || undefined,
    pageSize: p.pageSize ?? 20,
  });
  const loading = result.isFetching;
  const options = (result.data?.rows ?? []).map(row => ({
    value: String(row[p.idField ?? 'id']),
    label: String(row[p.labelField ?? 'name']),
  }));

  const value = p.value === null || p.value === undefined || p.value === ''
    ? null : String(p.value);
  const data = value !== null
      && selected.current?.value === value
      && !options.some(o => o.value === value)
    ? [selected.current, ...options]
    : options;

  return (
    <Select
      label={p.label}
      required={p.required}
      error={p.error}
      description={p.description}
      data={data}
      value={value}
      onChange={v => {
        selected.current = data.find(o => o.value === v) ?? selected.current;
        p.onChange(v);
      }}
      searchable
      clearable={!p.required}
      onSearchChange={setSearch}
      filter={({ options: opts }) => opts /* the server already filtered */}
      nothingFoundMessage={loading ? <Loader size="xs" /> : t('common.no-matches')}
      rightSection={loading ? <Loader size="xs" /> : undefined}
    />
  );
}
