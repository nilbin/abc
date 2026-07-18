import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  ActionIcon, Button, Checkbox, Group, NumberInput, SegmentedControl, Select, Stack, Text, Textarea, TextInput,
} from '@mantine/core';
import { DateInput } from '@mantine/dates';
import dayjs from 'dayjs';
import {
  ClauseOp, ParsedCondition, Px, RuleActionModel, RuleClause, RuleRef, RuleSchemaRow,
  buildAction, buildCondition, conditionRefs, enumLabel, isUnary, operatorsFor,
  parseAction, parseCondition, setFieldTargets, toWireEnum,
} from '@tam/core';
import type { FieldRendererProps } from './renderers';

// The chosen trigger: an operation or a domain event. Exactly one is set (the pickers enforce it).
interface Trigger { id: string; kind: 'operation' | 'event'; }

function resolveTrigger(form: Record<string, unknown> | undefined): Trigger | null {
  const onEvent = form?.onEvent;
  if (typeof onEvent === 'string' && onEvent) return { id: onEvent, kind: 'event' };
  const onOperation = form?.onOperation;
  if (typeof onOperation === 'string' && onOperation) return { id: onOperation, kind: 'operation' };
  return null;
}

// One schema fetch per (manifest revision, trigger) shared by the condition AND action editors —
// the view is pure over the compiled model, so the answer cannot change within a revision.
const schemaCache = new Map<string, Promise<RuleSchemaRow[]>>();

function fetchSchema(p: FieldRendererProps, trigger: Trigger): Promise<RuleSchemaRow[]> {
  const key = `${p.tam.manifest.revision}|${trigger.kind}|${trigger.id}`;
  let hit = schemaCache.get(key);
  if (!hit) {
    if (schemaCache.size > 64) schemaCache.clear();
    hit = p.tam.client
      .view('rules.schema', { trigger: trigger.id, kind: trigger.kind, pageSize: 200 })
      .then(res => res.rows as unknown as RuleSchemaRow[]);
    schemaCache.set(key, hit);
    hit.catch(() => schemaCache.delete(key));
  }
  return hit;
}

/** The trigger's server schema (target-row compiled fields). Empty when the trigger has no
 *  single target row, or before a trigger is chosen. Races are rejected by a sequence guard. */
function useRuleSchema(p: FieldRendererProps, trigger: Trigger | null): RuleSchemaRow[] {
  const [rows, setRows] = useState<RuleSchemaRow[]>([]);
  const seq = useRef(0);
  useEffect(() => {
    if (!trigger) { setRows([]); return; }
    const sent = ++seq.current;
    fetchSchema(p, trigger)
      .then(fetched => { if (sent === seq.current) setRows(fetched); })
      .catch(() => { if (sent === seq.current) setRows([]); });
  }, [p.tam.client, p.tam.manifest.revision, trigger?.id, trigger?.kind]);
  return rows;
}

function safeParsePx(value: unknown): Px | null {
  if (typeof value !== 'string' || !value.trim()) return null;
  try { return JSON.parse(value) as Px; } catch { return null; }
}

function refLabel(p: FieldRendererProps, ref: RuleRef): string {
  const label = p.tam.t(ref.labelKey);
  return label === ref.labelKey ? ref.path : label;   // t() echoes the key when unresolved
}

// Symbols are universal; the two word-shaped operators come from the catalog (the Swedish
// error text references them by their localized names).
const OP_SYMBOL: Partial<Record<ClauseOp, string>> = {
  eq: '=', ne: '≠', gt: '>', ge: '≥', lt: '<', le: '≤',
};
const opLabel = (t: (key: string) => string, op: ClauseOp): string =>
  OP_SYMBOL[op] ?? (op === 'isNull' ? t('rules.op-is-empty') : t('rules.op-is-set'));

// ---- trigger pickers ----------------------------------------------------------------------------
// Pure presentation: searchable selects over the manifest's operation/event catalogs. All the
// coordination (exactly one trigger; dependent condition/action reset) is DECLARED on the form
// with ResetOn — the pickers know nothing about their siblings.

export function RuleTriggerOperation(p: FieldRendererProps): React.ReactNode {
  // Context-free titles collide across aggregates ("Inaktivera" x2) — the picker prefers
  // the disambiguating `operations.{id}.name`, then the title, then the raw id.
  const data = Object.keys(p.tam.manifest.operations).sort().map(id => {
    const label = p.tam.tOr([`operations.${id}.name`, `operations.${id}.title`]);
    return { value: id, label: label === `operations.${id}.title` ? id : label };
  });
  return (
    <Select
      label={p.label} description={p.warning} error={p.error} searchable clearable
      data={data}
      value={p.value === null || p.value === undefined ? null : String(p.value)}
      onChange={v => p.onChange(v || null)}
    />
  );
}

export function RuleTriggerEvent(p: FieldRendererProps): React.ReactNode {
  const events = p.tam.manifest.events ?? {};
  const data = Object.keys(events).sort().map(id => ({ value: id, label: id }));
  return (
    <Select
      label={p.label} description={p.warning} error={p.error} searchable clearable
      data={data}
      value={p.value === null || p.value === undefined ? null : String(p.value)}
      onChange={v => p.onChange(v || null)}
    />
  );
}

// ---- value control ------------------------------------------------------------------------------

function ClauseValue({ p, reference, clause, onClause }: {
  p: FieldRendererProps; reference: RuleRef | undefined; clause: RuleClause;
  onClause: (next: RuleClause) => void;
}): React.ReactNode {
  if (isUnary(clause.op)) return null;
  const kind = reference?.wireKind ?? 'string';

  if (reference && reference.options.length > 0) {
    return (
      <Select
        placeholder={p.tam.t('rules.placeholder-value')} searchable
        // Options arrive as declared names; the stored const must be the WIRE value (camel
        // enum) — the same toWireEnum mapping the default renderer applies, or the server's
        // ordinal comparison would never match.
        data={reference.options.map(o => ({
          value: toWireEnum(o), label: enumLabel(p.tam.manifest, p.tam.culture, o),
        }))}
        value={clause.value === null || clause.value === undefined ? null : String(clause.value)}
        onChange={v => onClause({ ...clause, value: v, relativeDays: null })}
        style={{ flex: 1 }}
      />
    );
  }
  if (kind === 'boolean') {
    return (
      <Select
        placeholder={p.tam.t('rules.placeholder-value')}
        data={[{ value: 'true', label: 'true' }, { value: 'false', label: 'false' }]}
        value={clause.value === true ? 'true' : clause.value === false ? 'false' : null}
        onChange={v => onClause({ ...clause, value: v === null ? null : v === 'true', relativeDays: null })}
        style={{ flex: 1 }}
      />
    );
  }
  if (kind === 'number' || kind === 'integer') {
    return (
      <NumberInput
        placeholder={p.tam.t('rules.placeholder-value')} style={{ flex: 1 }}
        value={typeof clause.value === 'number' ? clause.value : ''}
        onChange={v => onClause({ ...clause, value: typeof v === 'number' ? v : null, relativeDays: null })}
      />
    );
  }
  if (kind === 'date' || kind === 'datetime') {
    const relative = clause.relativeDays !== undefined && clause.relativeDays !== null;
    return (
      <Group gap={4} style={{ flex: 1 }} wrap="nowrap">
        <SegmentedControl
          size="xs"
          data={[{ value: 'on', label: 'date' }, { value: 'rel', label: 'today ±' }]}
          value={relative ? 'rel' : 'on'}
          onChange={mode => onClause(mode === 'rel'
            ? { ...clause, relativeDays: clause.relativeDays ?? 0, value: undefined }
            : { ...clause, relativeDays: null })}
        />
        {relative ? (
          <NumberInput
            placeholder={p.tam.t('rules.placeholder-days')} style={{ flex: 1 }} allowDecimal={false}
            value={typeof clause.relativeDays === 'number' ? clause.relativeDays : 0}
            onChange={v => onClause({ ...clause, relativeDays: typeof v === 'number' ? v : 0 })}
          />
        ) : (
          <DateInput
            placeholder={p.tam.t('rules.placeholder-date')} style={{ flex: 1 }} valueFormat="YYYY-MM-DD" clearable
            value={typeof clause.value === 'string' && clause.value ? dayjs(clause.value).toDate() : null}
            onChange={v => onClause({ ...clause, value: v ? dayjs(v).format('YYYY-MM-DD') : null })}
          />
        )}
      </Group>
    );
  }
  return (
    <TextInput
      placeholder={p.tam.t('rules.placeholder-value')} style={{ flex: 1 }}
      value={clause.value === null || clause.value === undefined ? '' : String(clause.value)}
      onChange={e => onClause({ ...clause, value: e.currentTarget.value, relativeDays: null })}
    />
  );
}

// ---- condition builder --------------------------------------------------------------------------

export function RuleConditionField(p: FieldRendererProps): React.ReactNode {
  const trigger = resolveTrigger(p.form);
  const schema = useRuleSchema(p, trigger);
  const refs = useMemo(
    () => trigger ? conditionRefs(p.tam.manifest, schema, trigger.id, trigger.kind) : [],
    [p.tam.manifest, schema, trigger?.id, trigger?.kind]);

  // The stored value is the source of truth; parse it into the clause model. null → raw-JSON
  // editing, both for a non-clause-shaped expression (nested groups, computed sides) AND for
  // text that does not parse at all — otherwise a broken Advanced draft would round-trip
  // through an empty visual model and be silently overwritten.
  const parsed = useMemo<ParsedCondition | null>(() => {
    const px = safeParsePx(p.value);
    if (px === null && typeof p.value === 'string' && p.value.trim() !== '') return null;
    return parseCondition(px);
  }, [p.value]);
  const [rawMode, setRawMode] = useState(false);
  const advanced = rawMode || parsed === null;

  const emit = (next: ParsedCondition) => p.onChange(JSON.stringify(buildCondition(next)));

  const header = (
    <Text size="sm" fw={500}>{p.label}{p.required ? ' *' : ''}</Text>
  );

  if (!trigger) {
    return <Stack gap={4}>{header}<Text size="sm" c="dimmed">{p.tam.t('rules.pick-trigger')}</Text></Stack>;
  }

  if (advanced) {
    return (
      <Stack gap={4}>
        <Group justify="space-between">
          {header}
          {parsed !== null && (
            <Button size="compact-xs" variant="subtle" onClick={() => setRawMode(false)}>
              {p.tam.t('rules.visual')}
            </Button>
          )}
        </Group>
        <Textarea
          autosize minRows={3} error={p.error} description={p.tam.t('rules.advanced-hint')}
          styles={{ input: { fontFamily: 'monospace' } }}
          value={typeof p.value === 'string' ? p.value : ''}
          onChange={e => p.onChange(e.currentTarget.value || null)}
        />
      </Stack>
    );
  }

  const model = parsed!;
  const setClause = (i: number, next: RuleClause) =>
    emit({ ...model, clauses: model.clauses.map((c, j) => j === i ? next : c) });
  const removeClause = (i: number) =>
    emit({ ...model, clauses: model.clauses.filter((_, j) => j !== i) });
  const addClause = () =>
    emit({ ...model, clauses: [...model.clauses, { path: refs[0]?.path ?? '', op: 'eq' }] });

  return (
    <Stack gap={6}>
      <Group justify="space-between">
        {header}
        <Group gap="xs">
          {model.clauses.length > 1 && (
            <SegmentedControl
              size="xs"
              data={[{ value: 'all', label: p.tam.t('rules.match-all') },
                     { value: 'any', label: p.tam.t('rules.match-any') }]}
              value={model.match}
              onChange={v => emit({ ...model, match: v as 'all' | 'any' })}
            />
          )}
          <Button size="compact-xs" variant="subtle" onClick={() => setRawMode(true)}>
            {p.tam.t('rules.advanced')}
          </Button>
        </Group>
      </Group>

      {model.clauses.map((clause, i) => {
        const ref = refs.find(r => r.path === clause.path);
        const ops = operatorsFor(ref?.wireKind ?? 'string');
        // A comparison clause without a value would serialize as compare-against-null — the
        // server refuses that at define (invalid-condition), so flag it here as the user types.
        const incomplete = !isUnary(clause.op)
          && (clause.value === null || clause.value === undefined)
          && (clause.relativeDays === null || clause.relativeDays === undefined);
        return (
          <Stack key={i} gap={2}>
            <Group gap={4} align="flex-start" wrap="nowrap">
              <Select
                placeholder={p.tam.t('rules.placeholder-field')} searchable style={{ flex: 1.4 }}
                data={refs.map(r => ({ value: r.path, label: refLabel(p, r) }))}
                value={clause.path || null}
                onChange={v => setClause(i, { path: v ?? '', op: clause.op })}
              />
              <Select
                style={{ width: 96 }}
                data={ops.map(o => ({ value: o, label: opLabel(p.tam.t, o) }))}
                value={ops.includes(clause.op) ? clause.op : 'eq'}
                onChange={v => setClause(i, { ...clause, op: (v as ClauseOp) ?? 'eq' })}
              />
              <ClauseValue p={p} reference={ref} clause={clause} onClause={next => setClause(i, next)} />
              <ActionIcon variant="subtle" color="red" onClick={() => removeClause(i)} aria-label={p.tam.t('rules.remove-condition')}>✕</ActionIcon>
            </Group>
            {incomplete && <Text size="xs" c="red">{p.tam.t('rules.value-required')}</Text>}
          </Stack>
        );
      })}

      {p.error && <Text size="xs" c="red">{p.error}</Text>}
      <Group>
        <Button size="compact-sm" variant="light" onClick={addClause} disabled={refs.length === 0}>
          {p.tam.t('rules.add-condition')}
        </Button>
      </Group>
    </Stack>
  );
}

// ---- action builder -----------------------------------------------------------------------------

function ActionValue({ p, reference, value, onValue }: {
  p: FieldRendererProps; reference: RuleRef | undefined; value: unknown; onValue: (v: unknown) => void;
}): React.ReactNode {
  const kind = reference?.wireKind ?? 'string';
  if (reference && reference.options.length > 0) {
    return (
      <Select
        label={p.tam.t('labels.value')} searchable
        // Wire values, exactly like the condition's value control (and the default renderer).
        data={reference.options.map(o => ({
          value: toWireEnum(o), label: enumLabel(p.tam.manifest, p.tam.culture, o),
        }))}
        value={value === null || value === undefined ? null : String(value)}
        onChange={v => onValue(v)}
      />
    );
  }
  if (kind === 'boolean') {
    return (
      <Checkbox
        label={p.tam.t('labels.value')} mt="lg"
        checked={value === true} onChange={e => onValue(e.currentTarget.checked)}
      />
    );
  }
  if (kind === 'number' || kind === 'integer') {
    return (
      <NumberInput
        label={p.tam.t('labels.value')}
        value={typeof value === 'number' ? value : ''}
        onChange={v => onValue(typeof v === 'number' ? v : null)}
      />
    );
  }
  if (kind === 'date' || kind === 'datetime') {
    return (
      <DateInput
        label={p.tam.t('labels.value')} valueFormat="YYYY-MM-DD" clearable
        value={typeof value === 'string' && value ? dayjs(value).toDate() : null}
        onChange={v => onValue(v ? dayjs(v).format('YYYY-MM-DD') : null)}
      />
    );
  }
  return (
    <TextInput
      label={p.tam.t('labels.value')}
      value={value === null || value === undefined ? '' : String(value)}
      onChange={e => onValue(e.currentTarget.value || null)}
    />
  );
}

export function RuleActionField(p: FieldRendererProps): React.ReactNode {
  const trigger = resolveTrigger(p.form);
  const schema = useRuleSchema(p, trigger);
  const entityKey = schema[0]?.entityKey;
  const targets = useMemo(() => setFieldTargets(p.tam.manifest, entityKey), [p.tam.manifest, entityKey]);

  const stored = parseAction(typeof p.value === 'string' ? p.value : null);
  // An event-triggered rule may only set-field (RUL007) — never a finding or publish-event.
  const kind = trigger?.kind ?? 'operation';
  const type = kind === 'event' && stored.type === 'finding' ? 'set-field' : stored.type;
  const model: RuleActionModel = { ...stored, type };

  const emit = (next: RuleActionModel) => p.onChange(buildAction(next));

  const typeOptions: { value: string; label: string }[] = kind === 'event'
    ? [{ value: 'set-field', label: p.tam.t('rules.action-set-field') }]
    : [
        { value: 'finding', label: p.tam.t('rules.action-finding') },
        ...(entityKey ? [{ value: 'set-field', label: p.tam.t('rules.action-set-field') }] : []),
        { value: 'publish-event', label: p.tam.t('rules.action-publish-event') },
      ];

  const header = <Text size="sm" fw={500}>{p.label}</Text>;
  if (!trigger) {
    return <Stack gap={4}>{header}<Text size="sm" c="dimmed">{p.tam.t('rules.pick-trigger')}</Text></Stack>;
  }

  const target = targets.find(t => t.path === model.field);
  return (
    <Stack gap={6}>
      {header}
      <Select
        data={typeOptions} value={model.type} allowDeselect={false}
        onChange={v => emit({ type: (v as RuleActionModel['type']) ?? 'finding' })}
      />
      {model.type === 'set-field' && (
        <Group gap="xs" align="flex-start" grow>
          <Select
            label={p.tam.t('labels.target-field')} searchable
            data={targets.map(t => ({ value: t.path, label: refLabel(p, t) }))}
            value={model.field ?? null}
            onChange={v => emit({ ...model, field: v ?? undefined })}
            description={targets.length === 0 ? p.tam.t('rules.no-writable-fields') : undefined}
          />
          <ActionValue p={p} reference={target} value={model.value}
            onValue={v => emit({ ...model, value: v })} />
        </Group>
      )}
      {p.error && <Text size="xs" c="red">{p.error}</Text>}
    </Stack>
  );
}
