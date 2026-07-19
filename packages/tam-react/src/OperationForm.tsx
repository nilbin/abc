import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, Group, Stack, Text } from '@mantine/core';
import {
  FieldConflict, Finding, ManifestField, OperationResponse, ResolveResponse,
  evalPx, findingMessage,
} from '@tam/core';
import { useTam } from './context';
import { rendererFor } from './renderers';
import type { FieldRendererProps } from './renderers';

export interface OperationFormProps {
  form: string;
  /** Execute in another standable node (docs/26 D-H4) — a subtree grid editing a child row. */
  actAs?: string;
  initialValues?: Record<string, unknown>;
  initialExtensions?: Record<string, unknown>;
  onSuccess?: (response: OperationResponse) => void;
  submitLabel?: string;
}

interface FieldRuntime {
  field: ManifestField;
  key: string;              // values map key; extensions use "ext:{name}"
}

export function OperationForm(props: OperationFormProps) {
  const tam = useTam();
  const { manifest, client, t, culture } = tam;
  const formDef = manifest.forms[props.form];
  if (!formDef) throw new Error(`Unknown form '${props.form}'`);
  const operation = manifest.operations[formDef.operation];

  const fields = useMemo<FieldRuntime[]>(() => {
    const own = formDef.fields.map(f => ({ field: f, key: f.name }));
    const extension = formDef.includeExtensions && operation.extensibleEntity
      ? (manifest.extensions[operation.extensibleEntity] ?? [])
          .filter(f => !f.readOnly)   // plugin-owned state (docs/31 D-X2): grids yes, forms no
          .map(f => ({ field: f, key: `ext:${f.name}` }))
      : [];
    return [...own, ...extension];
  }, [formDef, operation, manifest]);

  const initial = useMemo(() => {
    const values: Record<string, unknown> = { ...(props.initialValues ?? {}) };
    for (const [k, v] of Object.entries(props.initialExtensions ?? {})) values[`ext:${k}`] = v;
    return values;
  }, [props.initialValues, props.initialExtensions]);

  const [values, setValues] = useState<Record<string, unknown>>(initial);
  const [touched, setTouched] = useState<Set<string>>(new Set());
  const [resolveState, setResolveState] = useState<ResolveResponse | null>(null);
  const [response, setResponse] = useState<OperationResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const seq = useRef(0);                       // local request sequence: stale-response rejection
  // ALL keys set since the last effect run — a renderer may set several fields in one batch
  // (the rule-builder's trigger picker clears its dependents), and the resolve trigger must
  // see every one of them, not just the last.
  const lastChanged = useRef<string[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  // Read through a ref inside async continuations: the closure's `touched`/`values` may predate the
  // user touching a field while a resolve was in flight — the ref never lies.
  const touchedRef = useRef(touched);
  touchedRef.current = touched;
  const valuesRef = useRef(values);
  valuesRef.current = values;

  const getWire = useCallback(
    (name: string) => values[name] ?? values[`ext:${name}`] ?? null,
    [values]);

  // The complete own-field input as the server wants it (changeSet fields are resolved elsewhere).
  const currentInput = useCallback(() => {
    const input: Record<string, unknown> = {};
    for (const f of formDef.fields) {
      const v = valuesRef.current[f.name];
      if (v !== undefined && v !== null && !f.changeSet) input[f.name] = v;
    }
    return input;
  }, [formDef]);

  // Apply a resolve response: complete field state + untouched-only suggestions (docs/05).
  const applyResolved = useCallback((resolved: ResolveResponse) => {
    setResolveState(resolved);
    for (const [name, state] of Object.entries(resolved.fields)) {
      if (state.suggestedValue !== undefined && state.suggestedValue !== null
          && !touchedRef.current.has(name)) {
        // Skip identical values: an unconditional set re-triggers the resolve effect and would
        // loop suggestion -> resolve -> suggestion forever.
        setValues(prev => prev[name] === state.suggestedValue
          ? prev
          : { ...prev, [name]: state.suggestedValue });
      }
    }
  }, []);

  // Batched, debounced, stale-rejecting server resolution (docs/05).
  const scheduleResolve = useCallback((changedFields: string[]) => {
    if (!changedFields.some(f => formDef.serverDependencies.includes(f))) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(async () => {
      const sent = ++seq.current;
      const input = currentInput();
      for (const f of changedFields) input[f] = valuesRef.current[f] ?? null;
      try {
        const resolved = await client.resolve(
          props.form, input, changedFields, manifest.revision);
        if (sent === seq.current) applyResolved(resolved);
      } catch { /* resolve is advisory; submit re-validates authoritatively */ }
    }, 350);
  }, [client, formDef, props.form, manifest.revision, currentInput, applyResolved]);

  // FULL initial resolve on mount / form / acting-node change (Sol re-review, Finding 4): a prefilled
  // form must show operation-derived requiredness, lookup descriptors and findings BEFORE any field
  // is touched, and a context-only derivation (no field dependencies) is never reachable through the
  // change-triggered path. Gated on hasServerDerivations so a derivation-free form pays nothing.
  useEffect(() => {
    if (!formDef.hasServerDerivations) return;
    const sent = ++seq.current;
    (async () => {
      try {
        const resolved = await client.resolve(props.form, currentInput(), null, manifest.revision);
        if (sent === seq.current) applyResolved(resolved);
      } catch { /* advisory */ }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.form, props.actAs]);

  const setField = useCallback((key: string, value: unknown) => {
    lastChanged.current.push(key);
    // One-hop ResetOn (docs/05): a field declaring resetOn on the edited key had a value
    // authored against that sibling's OLD state — discard it in the same update. Mechanical
    // resets never trigger further resets, so mutual pairs ("exactly one of") are cycle-safe.
    const resets = fields
      .filter(f => f.key !== key && f.field.resetOn?.includes(key))
      .map(f => f.key);
    lastChanged.current.push(...resets);
    setValues(prev => {
      const next = { ...prev, [key]: value };
      for (const reset of resets) next[reset] = null;
      return next;
    });
    setTouched(prev => new Set(prev).add(key));
    setResponse(null);
  }, [fields]);

  useEffect(() => {
    const changed = lastChanged.current.filter(key => !key.startsWith('ext:'));
    lastChanged.current = [];
    if (changed.length > 0) scheduleResolve(changed);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [values]);

  useEffect(() => () => clearTimeout(timer.current), []);

  const submit = useCallback(async (overrides?: Record<string, FieldConflict>) => {
    setSubmitting(true);
    try {
      const body: Record<string, unknown> = {};
      const extensions: Record<string, unknown> = {};

      for (const { field, key } of fields) {
        const value = values[key] ?? null;
        if (field.extension) {
          if (!touched.has(key) && initial[key] === undefined) continue;
          const original = overrides?.[`extensions.${field.name}`]?.currentValue
            ?? initial[key] ?? null;
          if (touched.has(key)) extensions[field.name] = { original, value };
          continue;
        }
        if (field.changeSet) {
          if (!touched.has(key)) continue;
          const original = overrides?.[field.name]?.currentValue ?? initial[key] ?? null;
          body[field.name] = { original, value };
          continue;
        }
        if (value !== null && value !== undefined) body[field.name] = value;
      }
      if (Object.keys(extensions).length > 0) body.extensions = extensions;

      // Submit THROUGH this form binding (docs/40): the server applies the form's tightening on top
      // of the operation contract and folds it into idempotency identity. Omitting it would execute a
      // direct call and silently skip form-specific validation.
      const result = await client.operation(formDef.operation, body,
        { form: props.form, ...(props.actAs ? { actAs: props.actAs } : {}) });
      setResponse(result);
      if (!result.findings.some(f => f.severity === 'error') && !result.conflicts?.length) {
        props.onSuccess?.(result);
      }
    } finally {
      setSubmitting(false);
    }
  }, [client, fields, formDef.operation, initial, props, touched, values]);

  const fieldError = (name: string): Finding | undefined => {
    const fromResolve = resolveState?.fields[name]?.findings
      .find(f => f.severity === 'error');
    const fromResponse = response?.findings
      .find(f => f.severity === 'error' && f.targets.includes(name));
    return fromResponse ?? fromResolve;
  };

  const globalFindings: Finding[] = [
    ...(resolveState?.findings ?? []),
    ...(response?.findings.filter(f => f.targets.length === 0) ?? []),
  ];

  const conflicts = response?.conflicts ?? [];

  return (
    <Stack gap="sm">
      {fields.map(({ field, key }) => {
        const visible = field.visibleWhen ? evalPx(field.visibleWhen, getWire) === true : true;
        if (!visible || field.renderer === 'hidden') return null;
        const required = field.required
          || (field.requiredWhen ? evalPx(field.requiredWhen, getWire) === true : false)
          // Operation-owned requiredness (docs/40): a derivation's Require() rule surfaces here
          // through resolve, so the indicator matches what submit will enforce for every caller.
          || resolveState?.fields[field.name]?.required === true;
        const error = fieldError(field.extension ? `extensions.${field.name}` : field.name);
        const warning = resolveState?.fields[field.name]?.findings
          .find(f => f.severity === 'warning');
        const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);
        // A REAL component boundary per field (not a plain function call): each renderer owns
        // its hook list, so a renderer with state/effects composes with VisibleWhen — fields
        // mounting and unmounting can never corrupt the form's hook order.
        const Renderer = rendererFor(field) as React.ComponentType<FieldRendererProps>;
        return (
          <Box key={key}>
            <Renderer
              field={field}
              label={label}
              value={values[key] ?? null}
              onChange={v => setField(key, v)}
              required={required}
              error={error ? findingMessage(manifest, culture, error) : undefined}
              warning={warning ? findingMessage(manifest, culture, warning) : undefined}
              options={resolveState?.fields[field.name]?.options}
              lookup={resolveState?.fields[field.name]?.lookup}
              tam={tam}
              form={values}
              setField={setField}
            />
          </Box>
        );
      })}

      {globalFindings.map((f, i) => (
        <Alert key={i} color={f.severity === 'error' ? 'red' : f.severity === 'warning' ? 'yellow' : 'blue'}>
          {findingMessage(manifest, culture, f)}
        </Alert>
      ))}

      {conflicts.length > 0 && (
        <Alert color="orange" title={t('concurrency.field-conflict')}>
          <Stack gap="xs">
            {conflicts.map(conflict => (
              <Group key={conflict.field} justify="space-between">
                <Text size="sm">
                  <b>{conflict.field}</b>: «{String(conflict.currentValue ?? '—')}» ↔ «{String(conflict.submittedValue ?? '—')}»
                </Text>
                <Group gap="xs">
                  <Button size="compact-xs" variant="light" onClick={() => {
                    const key = conflict.field.startsWith('extensions.')
                      ? `ext:${conflict.field.slice('extensions.'.length)}`
                      : conflict.field;
                    setField(key, conflict.currentValue);
                    setResponse(null);
                  }}>
                    {tam.t('concurrency.keep-current')}
                  </Button>
                  <Button size="compact-xs" onClick={() =>
                    void submit(Object.fromEntries(conflicts.map(c => [c.field, c])))}>
                    {tam.t('concurrency.use-mine')}
                  </Button>
                </Group>
              </Group>
            ))}
          </Stack>
        </Alert>
      )}

      <Group justify="flex-end" mt="xs">
        <Button loading={submitting} onClick={() => void submit()}>
          {/* A button wants an imperative, a dialog title a noun phrase — one key can't be
              both. `.submit` overrides; an EDIT form (any changeSet field) defaults to the
              generic save verb; only then the operation title. */}
          {props.submitLabel ?? tam.tOr([
            `operations.${formDef.operation}.submit`,
            ...(formDef.fields.some(f => f.changeSet) ? ['actions.save'] : []),
            `operations.${formDef.operation}.title`,
          ])}
        </Button>
      </Group>
    </Stack>
  );
}
