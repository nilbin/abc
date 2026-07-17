import React from 'react';
import { Group, NumberInput, Select, TextInput } from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import { ManifestField, enumLabel, toWireEnum } from '@tam/core';
import { useTam } from './context';

/** One declared-filterable field → the control set its wire kind supports (mirrors the
 *  server). Extension fields pass filterKey="ext.{key}" — same controls, same operators. */
export function FilterControl(props: {
  field: ManifestField;
  filters: Record<string, string>;
  setFilter: (key: string, value: string | null) => void;
  filterKey?: string;
}) {
  const { manifest, culture, t } = useTam();
  const { field, filters, setFilter } = props;
  const key = props.filterKey ?? field.name;
  const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);

  if (field.options) {
    return (
      <Select
        size="xs" w={150} placeholder={label} clearable
        data={field.options.map(o => ({
          value: toWireEnum(o),
          label: enumLabel(manifest, culture, o),
        }))}
        value={filters[key] ?? null}
        onChange={v => setFilter(key, v)}
      />
    );
  }

  switch (field.wireKind) {
    case 'boolean':
      return (
        <Select
          size="xs" w={120} placeholder={label} clearable
          data={[
            { value: 'true', label: t('common.yes') },
            { value: 'false', label: t('common.no') },
          ]}
          value={filters[key] ?? null}
          onChange={v => setFilter(key, v)}
        />
      );
    case 'number':
    case 'integer':
      return (
        <Group gap={4}>
          {(['from', 'to'] as const).map(bound => (
            <NumberInput
              key={bound} size="xs" w={110} hideControls
              placeholder={`${label} ${t(`filters.${bound}`)}`}
              value={filters[`${key}.${bound}`] ?? ''}
              onChange={v => setFilter(`${key}.${bound}`,
                v === '' || v === null ? null : String(v))}
            />
          ))}
        </Group>
      );
    case 'date':
      return (
        <Group gap={4}>
          {(['from', 'to'] as const).map(bound => (
            <DateInput
              key={bound} size="xs" w={130} clearable valueFormat="YYYY-MM-DD"
              placeholder={`${label} ${t(`filters.${bound}`)}`}
              value={filters[`${key}.${bound}`]
                ? dayjs(filters[`${key}.${bound}`]).toDate() : null}
              onChange={v => setFilter(`${key}.${bound}`,
                v ? dayjs(v).format('YYYY-MM-DD') : null)}
            />
          ))}
        </Group>
      );
    default:
      return (
        <TextInput
          size="xs" w={150} placeholder={label}
          value={filters[`${key}.contains`] ?? ''}
          onChange={e => setFilter(`${key}.contains`, e.currentTarget.value)}
        />
      );
  }
}
