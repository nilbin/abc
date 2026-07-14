import React, { useEffect, useRef, useState } from 'react';
import { Loader, Select } from '@mantine/core';
import { useTam } from './context';

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
 * search, so the option list never needs the full table client-side. The current selection
 * stays visible even when the latest search response doesn't contain it.
 */
export function LookupSelect(p: LookupSelectProps) {
  const { client, t } = useTam();
  const [search, setSearch] = useState('');
  const [options, setOptions] = useState<{ value: string; label: string }[]>([]);
  const [loading, setLoading] = useState(true);
  const selected = useRef<{ value: string; label: string } | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    const timer = setTimeout(() => {
      client.view(p.view, {
        [p.searchParam ?? 'search']: search || undefined,
        pageSize: p.pageSize ?? 20,
      }).then(result => {
        if (cancelled) return;
        setOptions(result.rows.map(row => ({
          value: String(row[p.idField ?? 'id']),
          label: String(row[p.labelField ?? 'name']),
        })));
      }).finally(() => { if (!cancelled) setLoading(false); });
    }, search ? 200 : 0);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [client, p.view, p.searchParam, p.pageSize, p.idField, p.labelField, search]);

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
