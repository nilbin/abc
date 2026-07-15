import React, { useState } from 'react';
import { Button, Checkbox, Group, NumberInput, SegmentedControl, Select, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import { ManifestField, enumLabel, toWireEnum } from '@tam/core';
import { TamContextValue } from './context';

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
        <TextInput placeholder="orders" value={draft} style={{ flex: 1 }} error={p.error}
          onChange={e => setDraft(e.currentTarget.value)} />
        <Button size="compact-sm" variant="light" disabled={!draftKey || draftTaken}
          onClick={() => { update(draftKey, choices[choices.length - 1]); setDraft(''); }}>+</Button>
      </Group>
    </Stack>
  );
};
// The framework's access levels (docs/27 D-A1) — the framework's client library may know them.
registerRenderer('level-map', keyValueMap(['view', 'edit', 'manage']));

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

export const DefaultRenderer: FieldRenderer = (p) => {
  const common = {
    label: p.label,
    required: p.required,
    error: p.error,
    description: p.warning,
  };
  const str = (v: unknown) => (v === null || v === undefined ? '' : String(v));

  if (p.field.renderer === 'hidden') return null;

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
