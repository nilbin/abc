import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, Group, Stack, Text } from '@mantine/core';
import {
  FieldConflict, Finding, ManifestField, OperationResponse, ResolveResponse,
  evalPx, findingMessage,
} from '@tam/core';
import { useTam } from './context';
import { rendererFor } from './renderers';

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
  const lastChanged = useRef<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  // Read through a ref inside async continuations: the closure's `touched` may predate the
  // user touching a field while a resolve was in flight — the ref never lies.
  const touchedRef = useRef(touched);
  touchedRef.current = touched;

  const getWire = useCallback(
    (name: string) => values[name] ?? values[`ext:${name}`] ?? null,
    [values]);

  // Batched, debounced, stale-rejecting server resolution (docs/05).
  const scheduleResolve = useCallback((changedField: string) => {
    if (!formDef.serverDependencies.includes(changedField)) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(async () => {
      const sent = ++seq.current;
      const input: Record<string, unknown> = {};
      for (const f of formDef.fields) {
        const v = values[f.name];
        if (v !== undefined && v !== null && !f.changeSet) input[f.name] = v;
      }
      input[changedField] = values[changedField] ?? null;
      try {
        const resolved = await client.resolve(
          props.form, input, [changedField], manifest.revision);
        if (sent === seq.current) {
          setResolveState(resolved);
          // Suggestions apply to untouched fields only: RecomputeIfUntouched (docs/05).
          for (const [name, state] of Object.entries(resolved.fields)) {
            if (state.suggestedValue !== undefined && state.suggestedValue !== null
                && !touchedRef.current.has(name)) {
              // Skip identical values: an unconditional set re-triggers the resolve effect
              // and would loop suggestion -> resolve -> suggestion forever.
              setValues(prev => prev[name] === state.suggestedValue
                ? prev
                : { ...prev, [name]: state.suggestedValue });
            }
          }
        }
      } catch { /* resolve is advisory; submit re-validates authoritatively */ }
    }, 350);
  }, [client, formDef, props.form, values, manifest.revision]);

  const setField = useCallback((key: string, value: unknown) => {
    lastChanged.current = key;
    setValues(prev => ({ ...prev, [key]: value }));
    setTouched(prev => new Set(prev).add(key));
    setResponse(null);
  }, []);

  useEffect(() => {
    const changed = lastChanged.current;
    if (changed && !changed.startsWith('ext:')) scheduleResolve(changed);
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

      const result = await client.operation(formDef.operation, body,
        props.actAs ? { actAs: props.actAs } : undefined);
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
          || (field.requiredWhen ? evalPx(field.requiredWhen, getWire) === true : false);
        const error = fieldError(field.extension ? `extensions.${field.name}` : field.name);
        const warning = resolveState?.fields[field.name]?.findings
          .find(f => f.severity === 'warning');
        const label = field.extension ? t(`ext.${field.name}`) : t(field.labelKey);
        const Renderer = rendererFor(field);
        return (
          <Box key={key}>
            {Renderer({
              field,
              label,
              value: values[key] ?? null,
              onChange: v => setField(key, v),
              required,
              error: error ? findingMessage(manifest, culture, error) : undefined,
              warning: warning ? findingMessage(manifest, culture, warning) : undefined,
              options: resolveState?.fields[field.name]?.options,
              tam,
              form: values,
              setField,
            })}
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
          {props.submitLabel ?? t(`operations.${formDef.operation}.title`)}
        </Button>
      </Group>
    </Stack>
  );
}
