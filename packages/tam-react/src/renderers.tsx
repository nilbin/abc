import React from 'react';
import { Checkbox, NumberInput, Select, TextInput, Textarea } from '@mantine/core';
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
