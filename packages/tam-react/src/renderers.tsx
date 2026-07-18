import React, { useEffect, useState } from 'react';
import { Badge, Button, Checkbox, FileInput, Group, NumberInput, SegmentedControl, Select, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import { ManifestField, enumLabel, toWireEnum } from '@tam/core';
import { LookupSelect } from './LookupSelect';
import { badgeColor } from './badges';
import { TamContextValue, useTam, useView } from './context';

export interface FieldRendererProps {
  field: ManifestField;
  label: string;
  value: unknown;
  onChange: (value: unknown) => void;
  required: boolean;
  error?: string;
  warning?: string;
  options?: { value: unknown; label: string }[];
  tam: TamContextValue;
  /** Current wire values of the SIBLING fields on the same form (own fields keyed by wire name).
   *  A renderer that reacts to another field reads it here — e.g. the rule builder resolving its
   *  referenceable fields from the chosen trigger. Undefined outside a form context. */
  form?: Record<string, unknown>;
  /** Set a SIBLING field's value — for a renderer that coordinates fields (e.g. the trigger
   *  picker clearing the other trigger so exactly one is set). Undefined outside a form. */
  setField?: (key: string, value: unknown) => void;
}

export type FieldRenderer = (props: FieldRendererProps) => React.ReactNode;

const registry = new Map<string, FieldRenderer>();

/** App-owned pixels: register by renderer key ("customer-picker") or semantic type ("type:email"). */
export function registerRenderer(key: string, renderer: FieldRenderer): void {
  registry.set(key, renderer);
}

export function rendererFor(field: ManifestField): FieldRenderer {
  return registry.get(field.renderer ?? '')
    ?? registry.get(`type:${field.type}`)
    ?? DefaultRenderer;
}

// ---- Display renderers: the READ-ONLY twin of the input registry. A grid cell and a record's
// read view are the same question — "show this field's value" — so they share ONE cascade
// (renderer key → semantic type → DefaultDisplay), the mirror of rendererFor. Displays are PURE
// (no hooks): they are called as functions from a cell, not mounted as components.

export interface FieldDisplayProps {
  field: ManifestField;
  value: unknown;
  tam: TamContextValue;
}

/** A read-only value display. PURE — no hooks: called as a function, never mounted. */
export type FieldDisplay = (props: FieldDisplayProps) => React.ReactNode;

const displays = new Map<string, FieldDisplay>();

/** App-owned display: register by renderer key ("status-pill") or semantic type ("type:money").
 *  A display must be a PURE function of its props — no hooks: it is called inline from grid
 *  cells, not mounted as a component. */
export function registerDisplay(key: string, display: FieldDisplay): void {
  displays.set(key, display);
}

export function displayFor(field: ManifestField): FieldDisplay {
  return displays.get(field.renderer ?? '')
    ?? displays.get(`type:${field.type}`)
    ?? DefaultDisplay;
}

/** The built-in value display: enum → badge, boolean → check, number → localized (money 2dp),
 *  everything else → text. Nulls render as a dimmed dash. Grid cells and record read views both
 *  land here unless an app registers a display for the field's renderer/type. */
export const DefaultDisplay: FieldDisplay = ({ field, value, tam }) => {
  if (value === null || value === undefined) return <Text c="dimmed" size="sm">—</Text>;
  if (field.options) {
    return (
      <Badge variant="light" color={badgeColor(String(value))}>
        {enumLabel(tam.manifest, tam.culture, value)}
      </Badge>
    );
  }
  switch (field.wireKind) {
    case 'boolean': return value === true ? '✓' : '—';
    case 'number': return (
      <Text size="sm" ta="right">
        {new Intl.NumberFormat(tam.culture, (field.format === 'money' || field.renderer === 'money')
          ? { minimumFractionDigits: 2, maximumFractionDigits: 2 } : {}).format(Number(value))}
      </Text>
    );
    default: return <Text size="sm">{String(value)}</Text>;
  }
};

/** Built-in: forms opt fields into a textarea with .Renderer("multiline"). */
registerRenderer('multiline', (p) => (
  <Textarea
    label={p.label}
    required={p.required}
    error={p.error}
    description={p.warning}
    autosize
    minRows={4}
    styles={{ input: { fontFamily: 'monospace' } }}
    value={p.value === null || p.value === undefined ? '' : String(p.value)}
    onChange={e => p.onChange(e.currentTarget.value || null)}
  />
));

/** Built-in: money fields — two decimals, matching the grid's money formatting. */
registerRenderer('money', (p) => (
  <NumberInput
    label={p.label}
    required={p.required}
    error={p.error}
    description={p.warning}
    decimalScale={2}
    value={p.value === null || p.value === undefined ? '' : Number(p.value)}
    onChange={v => p.onChange(v === '' ? null : v)}
  />
));

/**
 * Built-in: per-culture text map ({culture: text}) — used by framework packages (extension
 * field labels, rule messages). The default registration covers the framework's shipped
 * cultures; re-register with your own list to change the columns.
 */
export const cultureText = (cultures: string[]) => function CultureText(p: FieldRendererProps) {
  const value = (p.value ?? {}) as Record<string, string>;
  const set = (culture: string, text: string) =>
    p.onChange({ ...value, [culture]: text || undefined });
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{p.label}{p.required ? ' *' : ''}</Text>
      <Group grow>
        {cultures.map((culture, i) => (
          <TextInput key={culture} placeholder={culture} value={value[culture] ?? ''}
            onChange={e => set(culture, e.currentTarget.value)} error={i === 0 ? p.error : undefined} />
        ))}
      </Group>
    </Stack>
  );
};
registerRenderer('culture-text', cultureText(['sv', 'en']));

/** Built-in: keyed-choice map editor (role levels: resource → view|edit|manage). The framework
 *  validates keys and values server-side; this renderer only shapes the map. */
export const keyValueMap = (choices: string[]) => function KeyValueMap(p: FieldRendererProps) {
  const value = (p.value ?? {}) as Record<string, string>;
  const entries = Object.entries(value);
  const update = (resource: string, scope: string) => p.onChange({ ...value, [resource]: scope });
  const remove = (resource: string) => {
    const next = { ...value };
    delete next[resource];
    p.onChange(Object.keys(next).length ? next : null);
  };
  const [draft, setDraft] = useState('');
  const draftKey = draft.trim();
  const draftTaken = draftKey in value;
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{p.label}{p.required ? ' *' : ''}</Text>
      {entries.map(([resource, scope]) => (
        <Group key={resource} gap="xs">
          <TextInput value={resource} readOnly style={{ flex: 1 }} />
          <SegmentedControl size="xs" data={choices} value={scope}
            onChange={v => update(resource, v)} />
          <Button size="compact-xs" variant="subtle" color="red"
            onClick={() => remove(resource)}>✕</Button>
        </Group>
      ))}
      <Group gap="xs">
        <TextInput placeholder={p.tam.t('roles.placeholder-resource')} value={draft} style={{ flex: 1 }} error={p.error}
          onChange={e => setDraft(e.currentTarget.value)} />
        <Button size="compact-sm" variant="light" disabled={!draftKey || draftTaken}
          onClick={() => { update(draftKey, choices[choices.length - 1]); setDraft(''); }}>+</Button>
      </Group>
    </Stack>
  );
};
// The framework's access levels (docs/27 D-A1) — the framework's client library may know them.
registerRenderer('level-map', keyValueMap(['view', 'edit', 'manage']));

// The FILE renderer (docs/35): a real file input carried as base64 in the wire field. It also
// fills the upload contract's metadata SIBLINGS (fileName, contentType) through setField —
// extra keys are ignored by forms that lack them, the same tolerance the wire binder has.
registerRenderer('file', function FilePick(p: FieldRendererProps) {
  return (
    <FileInput
      label={p.label}
      required={p.required}
      error={p.error}
      onChange={file => {
        if (!file) { p.onChange(null); return; }
        const reader = new FileReader();
        reader.onload = () => {
          p.onChange(String(reader.result).split(',')[1] ?? '');
          p.setField?.('fileName', file.name);
          p.setField?.('contentType', file.type || 'application/octet-stream');
        };
        reader.readAsDataURL(file);
      }}
    />
  );
});

// The REACH picker (docs/35 D-R5): people-set selection over the reach.search view —
// options grouped by kind (locale key reach.kinds.{kind}, shipped by the kind's owner;
// unknown kinds fall back to the raw token), value = the canonical ReachRef string. The
// server filters (activation-gated, fail-closed), so the picker shows exactly the sets a
// stored ACL could resolve.
registerRenderer('reach', function ReachPick(p: FieldRendererProps) {
  const { t } = useTam();
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(search), search ? 200 : 0);
    return () => clearTimeout(timer);
  }, [search]);
  const result = useView('reach.search', { search: debounced || undefined, pageSize: 50 });
  const rows = (result.data?.rows ?? []) as { id: string; label: string; kind: string }[];
  const groups = new Map<string, { value: string; label: string }[]>();
  for (const row of rows) {
    const localized = t(`reach.kinds.${row.kind}`);
    const heading = localized === `reach.kinds.${row.kind}` ? row.kind : localized;
    if (!groups.has(heading)) groups.set(heading, []);
    groups.get(heading)!.push({ value: row.id, label: row.label });
  }
  const data = [...groups.entries()].map(([group, items]) => ({ group, items }));
  return (
    <Select
      label={p.label}
      required={p.required}
      error={p.error}
      data={data}
      value={p.value === null || p.value === undefined || p.value === '' ? null : String(p.value)}
      onChange={v => p.onChange(v)}
      searchable
      clearable={!p.required}
      onSearchChange={setSearch}
      filter={({ options }) => options /* the server already filtered */}
      nothingFoundMessage={t('common.no-matches')}
    />
  );
});

/** Built-in: repeated-value editor (role names, permission atoms). Server validates every name. */
registerRenderer('string-list', function StringList(p: FieldRendererProps) {
  const value = (p.value ?? []) as string[];
  const [draft, setDraft] = useState('');
  const draftKey = draft.trim();
  const remove = (i: number) => {
    const next = value.filter((_, j) => j !== i);
    p.onChange(next.length ? next : null);
  };
  return (
    <Stack gap={4}>
      <Text size="sm" fw={500}>{p.label}{p.required ? ' *' : ''}</Text>
      {value.map((item, i) => (
        <Group key={`${item}-${i}`} gap="xs">
          <TextInput value={item} readOnly style={{ flex: 1 }} />
          <Button size="compact-xs" variant="subtle" color="red" onClick={() => remove(i)}>✕</Button>
        </Group>
      ))}
      <Group gap="xs">
        <TextInput value={draft} style={{ flex: 1 }} error={p.error}
          onChange={e => setDraft(e.currentTarget.value)} />
        <Button size="compact-sm" variant="light" disabled={!draftKey || value.includes(draftKey)}
          onClick={() => { p.onChange([...value, draftKey]); setDraft(''); }}>+</Button>
      </Group>
    </Stack>
  );
});

// The rule-builder renderers (docs/22): visual condition/action editors + trigger pickers that
// resolve their referenceable fields and value options from the server (manifest + rules.schema).
// Registered here so they load with the form, exactly like the other built-ins; the components
// live in RuleBuilder.tsx (which imports only types from this module — no runtime cycle).
import {
  RuleTriggerOperation, RuleTriggerEvent, RuleConditionField, RuleActionField,
} from './RuleBuilder';
registerRenderer('rule-trigger-operation', RuleTriggerOperation);
registerRenderer('rule-trigger-event', RuleTriggerEvent);
registerRenderer('rule-condition', RuleConditionField);
registerRenderer('rule-action', RuleActionField);

export const DefaultRenderer: FieldRenderer = (p) => {
  const common = {
    label: p.label,
    required: p.required,
    error: p.error,
    description: p.warning,
  };
  const str = (v: unknown) => (v === null || v === undefined ? '' : String(v));

  if (p.field.renderer === 'hidden') return null;

  // docs/34 M5 — the type carries the defaults. A readOnly COMPILED field is a computed
  // display seat: disabled, fed by suggestions, the server's value authoritative.
  if (p.field.readOnly && !p.field.extension) {
    const shown = p.value === null || p.value === undefined ? ''
      : p.field.format === 'money'
        ? new Intl.NumberFormat(p.tam.culture, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(Number(p.value))
        : String(p.value);
    return <TextInput label={p.label} value={shown} disabled description={p.warning} />;
  }

  // A [Lookup]-carrying field renders a searchable picker over its view — no per-form
  // renderer, no options derivation. Server-sent options (a derivation) still win: they
  // carry request context a global lookup cannot (e.g. "projects OF THIS customer").
  if (p.field.lookup && !(p.options && p.options.length > 0)) {
    const view = p.tam.manifest.views[p.field.lookup];
    const labelField = view?.resultFields.find(f => f.name !== 'id' && f.wireKind === 'string')?.name ?? 'name';
    return (
      <LookupSelect
        view={p.field.lookup}
        value={p.value}
        onChange={v => p.onChange(v)}
        label={p.label}
        required={p.required}
        error={p.error}
        description={p.warning}
        labelField={labelField}
      />
    );
  }

  if (p.options && (p.field.type === 'reference' || p.options.length > 0)) {
    return (
      <Select
        {...common}
        data={p.options.map(o => ({ value: String(o.value), label: o.label }))}
        value={p.value === null || p.value === undefined ? null : String(p.value)}
        onChange={v => p.onChange(v)}
        searchable
        clearable={!p.required}
      />
    );
  }

  switch (p.field.wireKind) {
    case 'boolean':
      return (
        <Checkbox
          label={p.label}
          checked={p.value === true}
          onChange={e => p.onChange(e.currentTarget.checked)}
          error={p.error}
        />
      );
    case 'number':
    case 'integer':
      return (
        <NumberInput
          {...common}
          value={typeof p.value === 'number' ? p.value : ''}
          onChange={v => p.onChange(typeof v === 'number' ? v : null)}
          thousandSeparator=" "
          decimalScale={p.field.format === 'money' ? 2 : undefined}
        />
      );
    case 'date':
      return (
        <DateInput
          {...common}
          value={p.value ? dayjs(String(p.value)).toDate() : null}
          onChange={v => p.onChange(v ? dayjs(v).format('YYYY-MM-DD') : null)}
          valueFormat="YYYY-MM-DD"
          clearable={!p.required}
        />
      );
    default:
      if (p.field.options?.length) {
        return (
          <Select
            {...common}
            data={p.field.options.map(o => ({
              value: toWireEnum(o),
              label: enumLabel(p.tam.manifest, p.tam.culture, o),
            }))}
            value={str(p.value) || null}
            onChange={v => p.onChange(v)}
            allowDeselect={!p.required}
          />
        );
      }
      if (p.field.format === 'multiline' || p.field.type === 'multiline-text') {
        return (
          <Textarea {...common} autosize minRows={2}
            value={str(p.value)} onChange={e => p.onChange(e.currentTarget.value || null)} />
        );
      }
      return (
        <TextInput {...common}
          type={p.field.format === 'email' ? 'email' : 'text'}
          maxLength={p.field.maxLength}
          value={str(p.value)} onChange={e => p.onChange(e.currentTarget.value || null)} />
      );
  }
};
