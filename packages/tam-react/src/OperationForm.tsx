import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, Group, Stack, Text } from '@mantine/core';
import {
  FieldConflict, Finding, ManifestField, OperationResponse, ResolveResponse,
  evalPx, findingMessage,
} from '@tam/core';
import { useTam } from './context';
import { rendererFor } from './renderers';
import type { FieldRendererProps } from './renderers';
import { buildFormInput, FieldRuntime } from './formInput';

export interface OperationFormProps {
  form: string;
  /** Execute in another standable node (docs/26 D-H4) — a subtree grid editing a child row. */
  actAs?: string;
  initialValues?: Record<string, unknown>;
  initialExtensions?: Record<string, unknown>;
  /** The RECORD this form edits (Sol re-review round 4, Finding 5). Edit state resets and re-resolves
   *  when this changes — NOT when the initialValues OBJECT identity changes (a parent re-render mints a
   *  new object for the same record, which would otherwise discard the user's edits). The corollary
   *  (round 6, F1): NEW prefill values for the SAME instanceKey are ignored until instanceKey changes —
   *  the frozen baseline and the user's edits are kept. To adopt fresh server data as the baseline,
   *  change the identity, e.g. instanceKey={`${id}:${version}`}. Pass the row id for an edit form; omit
   *  for a create form (or remount with a React `key` per record instead). */
  instanceKey?: string | number;
  onSuccess?: (response: OperationResponse) => void;
  submitLabel?: string;
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
  // Per-field concurrency resolution (Sol re-review round 10, F1; round 11, F1). Each conflict is decided
  // on its OWN row, and BOTH choices record an override here rebasing the field's `original` to the
  // server's current value: "use mine" keeps the user's value (a genuine apply against reality), "keep
  // current" also adopts the current value so the retry is a true no-op (Original == Value). The
  // decisions apply together in ONE retry when the last conflict is resolved — never a global "use mine"
  // (or a global dismiss) fired from a button visually attached to a single field.
  const [pendingConflicts, setPendingConflicts] = useState<FieldConflict[]>([]);
  const conflictOverrides = useRef<Record<string, FieldConflict>>({});
  const resubmitAfterResolve = useRef(false);
  const seq = useRef(0);                       // local request sequence: stale-response rejection
  // Submit identity (Sol re-review round 11, F3): a submit in flight when the form/instanceKey changes
  // must not land its response, conflicts or onSuccess on the NEXT record. Bumped by the reset effect and
  // by each submit; a stale attempt (attempt !== current) drops its result and leaves `submitting` alone.
  const submitSeq = useRef(0);
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
  // Read `initial` at effect-fire time WITHOUT depending on its object identity (Finding 5): a parent
  // re-render mints a new initialValues object for the same record, which must not re-fire anything.
  const initialRef = useRef(initial);
  initialRef.current = initial;
  // The FROZEN concurrency baseline for this form instance (Sol re-review round 5, Finding 1): the
  // `original` of every Change<T> is read from here, NOT from the latest initialValues prop. A
  // background refresh that hands the SAME record (instanceKey) a newer server value must not silently
  // rebase `original` under the user's in-flight edit — that would let a stale edit overwrite the
  // concurrent update without the intended field conflict. Re-frozen only when the reset effect runs
  // (form / instanceKey change); to adopt fresh server data as the baseline, the caller changes the
  // identity, e.g. instanceKey={`${id}:${version}`}.
  const baselineRef = useRef(initial);

  const getWire = useCallback(
    (name: string) => values[name] ?? values[`ext:${name}`] ?? null,
    [values]);

  // The ONE operation-input builder for BOTH resolve and submit (docs/40, Sol re-review round 8):
  // there is no sparse/complete divergence to drift. Every INITIALIZED change-set field (own and
  // extension) carries its complete {original, value} object — `original` from the frozen baseline,
  // `value` the current edit — so a derivation sees the complete proposed state and TamMerge derives
  // the actual patch from `original != value` (an untouched field, original == value, is a no-op that
  // takes no concurrency check). Non-change-set (create) fields send their raw value. A conflict
  // override (the user resolving a detected conflict) supplies the fresh `original`. Reads live refs
  // so an in-flight async caller never sees a stale snapshot.
  const buildOperationInput = useCallback(
    (overrides?: Record<string, FieldConflict>) =>
      buildFormInput(fields, baselineRef.current, valuesRef.current, overrides),
    [fields]);

  // Apply a resolve response: complete field state + untouched-only suggestions (docs/05).
  const applyResolved = useCallback((resolved: ResolveResponse) => {
    setResolveState(resolved);
    for (const [name, state] of Object.entries(resolved.fields)) {
      // Auto-adopt a suggestion into the actual value ONLY for a CREATE (non-change-set) field (Sol
      // re-review round 7, F4). Writing a suggestion into an edit change-set field's `values` WITHOUT
      // marking it touched would leave Original == Value — a no-op the merge ignores — so the displayed
      // suggestion would silently never persist. For edit fields the suggestion stays in resolveState
      // (surfaced to the renderer as `suggestion`); adopting it goes through setField, which makes it a
      // real change (Original != Value), so display and submit agree. A create field is always written,
      // so auto-adopt is safe and keeps prefill behaviour.
      if (formDef.fields.find(f => f.name === name)?.changeSet) continue;
      if (state.suggestedValue !== undefined && state.suggestedValue !== null
          && !touchedRef.current.has(name)
          // Skip identical values: an unconditional set re-triggers the resolve effect and would
          // loop suggestion -> resolve -> suggestion forever.
          && valuesRef.current[name] !== state.suggestedValue) {
        // Adopting a suggestion is a field change: push it to lastChanged so the reactive resolve
        // reruns and any derivation depending on this field recomputes (Sol re-review round 8, P3).
        // Otherwise the form would display the adopted value but derive dependent state from the old
        // snapshot. Not marked touched — touched stays "user interacted" for UX (suggestions may still
        // overwrite this field), and the identical-value guard above prevents an infinite loop.
        lastChanged.current.push(name);
        setValues(prev => ({ ...prev, [name]: state.suggestedValue }));
      }
    }
  }, [formDef]);

  // Batched, debounced, stale-rejecting server resolution (docs/05). Sends the SAME complete input
  // submit sends (buildOperationInput), so resolve and submit derive requiredness/candidates/WasChanged
  // from identical state. A cleared non-change field goes out as null naturally (buildOperationInput
  // omits it, which deserializes to null server-side). The `changed` wire arg is advisory only now —
  // WasChanged is derived server-side from Original != Value — so we pass the triggering batch as a
  // hint, not authority. The resolve DECISION still gates on serverDependencies + debounce.
  const scheduleResolve = useCallback((changedFields: string[]) => {
    if (!changedFields.some(f => formDef.serverDependencies.includes(f))) return;
    clearTimeout(timer.current);
    timer.current = setTimeout(async () => {
      const sent = ++seq.current;
      try {
        const resolved = await client.resolve(
          props.form, buildOperationInput(), changedFields, manifest.revision, { actAs: props.actAs });
        if (sent === seq.current) applyResolved(resolved);
      } catch { /* resolve is advisory; submit re-validates authoritatively */ }
    }, 350);
  }, [client, formDef, props.form, props.actAs, manifest.revision, buildOperationInput, applyResolved]);

  // Reset edit state when the form or the RECORD (instanceKey) changes (Sol re-review round 4,
  // Finding 5) — NOT on initialValues object identity, which a parent re-render changes for the same
  // record and would wrongly discard edits. Also cancels any in-flight debounce and stales any
  // pending resolve, so a previous record's response can't land after the switch.
  useEffect(() => {
    // Freeze the concurrency baseline for THIS instance (Finding 1) — the only place it changes.
    baselineRef.current = initialRef.current;
    clearTimeout(timer.current);
    seq.current++;
    lastChanged.current = [];
    setValues(baselineRef.current);
    setTouched(new Set());
    setResponse(null);
    setResolveState(null);
    // Conflict + submit state is per-record (round 11, F3): a pending conflict round, its accumulated
    // overrides, a queued auto-resubmit and the submitting flag all belong to the OLD instanceKey. Bump
    // submitSeq so any in-flight submit drops its response, and clear the UI so the new record starts
    // clean instead of inheriting the previous record's conflict banner or spinner.
    submitSeq.current++;
    conflictOverrides.current = {};
    resubmitAfterResolve.current = false;
    setPendingConflicts([]);
    setSubmitting(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.form, props.instanceKey]);

  // Submit + conflict state is EXECUTION-CONTEXT specific too (Sol re-review round 12, F1). An `actAs`
  // switch deliberately PRESERVES the edit values and frozen baseline (the context-refresh effect below
  // re-resolves them against the new acting node) — but a pending conflict round and an in-flight submit
  // were computed against the OLD node. A conflict override carries that node's persisted current values
  // as fresh merge bases, and an in-flight submit executed against it; neither may resolve or land
  // against the new node. So on a same-record `actAs` change, invalidate ONLY request/result state —
  // never the values, touched set or baseline. An identity change is owned by the reset effect above
  // (which already clears this), so skip it here to avoid a redundant double-invalidation.
  const submitContext = useRef<{ form: string; instanceKey?: string | number; actAs?: string } | null>(null);
  useEffect(() => {
    const prev = submitContext.current;
    const recordChanged = prev === null
      || prev.form !== props.form || prev.instanceKey !== props.instanceKey;
    const actAsChanged = prev !== null && prev.actAs !== props.actAs;
    submitContext.current = { form: props.form, instanceKey: props.instanceKey, actAs: props.actAs };
    if (recordChanged || !actAsChanged) return;   // reset effect owns record changes; nothing to do on mount
    submitSeq.current++;
    conflictOverrides.current = {};
    resubmitAfterResolve.current = false;
    setPendingConflicts([]);
    setResponse(null);
    setSubmitting(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.form, props.instanceKey, props.actAs]);

  // FULL baseline resolve on mount / form / record switch (Sol re-review, Findings 4 + 2 + 5): a
  // prefilled form must show operation-derived requiredness, lookup descriptors and findings BEFORE
  // any field is touched, and a context-only derivation (no field dependencies) is unreachable through
  // the change path. Built from `initialRef` directly (the just-frozen baseline, not the async-updating
  // values, not `initial`'s object identity), so a change-set field carries its {original, value}
  // shape. Deps match the reset effect — the SAME record the reset just installed — and deliberately
  // EXCLUDE actAs/revision: refreshing those must preserve edits (the effect below), not rebuild from
  // the pristine baseline and discard them (Sol re-review round 6, F1). Gated on hasServerDerivations.
  useEffect(() => {
    if (!formDef.hasServerDerivations) return;
    const sent = ++seq.current;
    // The SAME shared builder resolve/submit use (Sol re-review round 9, F2): the frozen baseline is
    // both `original` and `value` (nothing edited yet), so the baseline request has exactly the shape
    // every later resolve/submit does — initialized null change fields included as {null, null},
    // extensions bundled — not a second, subtly-different payload. baselineRef is frozen synchronously
    // by the reset effect (declared above, so it runs first on this same [form, instanceKey] change).
    const snapshot = baselineRef.current;
    const input = buildFormInput(fields, snapshot, snapshot);
    (async () => {
      try {
        const resolved = await client.resolve(props.form, input, null, manifest.revision, { actAs: props.actAs });
        if (sent === seq.current) applyResolved(resolved);
      } catch { /* advisory */ }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.form, props.instanceKey]);

  // RE-RESOLVE the current edit state on a CONTEXT change (acting node / manifest revision) or when the
  // form gains its first derivation for the SAME record — WITHOUT discarding edits (Sol re-review rounds
  // 6-9). One ref tracks the (form, instanceKey) IDENTITY, updated on EVERY run (even with no
  // derivations) so a later derivation-activation still sees the right prior identity. The invariant
  // (round 9, F1): an IDENTITY change is ALWAYS owned by the reset + baseline effects — never resolve
  // current edits here on one, or buildOperationInput() would read the previous record's not-yet-
  // rendered values under the freshly frozen baseline (a mixed-record request that also wins the seq
  // race). So this resolves ONLY when the identity is unchanged: a same-record context change, or a
  // same-record derivation activation (whose [form, instanceKey]-keyed baseline effect did not re-run).
  // Cancelling the pending debounce first stops a stale old-context timer from landing after us.
  const resolvedIdentity = useRef<{ form: string; instanceKey?: string | number } | null>(null);
  useEffect(() => {
    const prev = resolvedIdentity.current;
    const identityChanged = prev === null
      || prev.form !== props.form || prev.instanceKey !== props.instanceKey;
    resolvedIdentity.current = { form: props.form, instanceKey: props.instanceKey };
    if (!formDef.hasServerDerivations) return;
    if (identityChanged) return;
    clearTimeout(timer.current);
    const sent = ++seq.current;
    const input = buildOperationInput();
    (async () => {
      try {
        const resolved = await client.resolve(props.form, input, null, manifest.revision, { actAs: props.actAs });
        if (sent === seq.current) applyResolved(resolved);
      } catch { /* advisory */ }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.form, props.instanceKey, props.actAs, manifest.revision, formDef.hasServerDerivations]);

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
    // A mechanical ResetOn clear is part of the pending patch, not a passive display change (Sol
    // re-review round 7, F1): the reset fields must be TOUCHED too, or resolve keeps sending their
    // stale baseline and — worse — submit omits the untouched change-set field entirely, so the
    // cleared value the user saw never persists (the parent change lands while the dependent keeps its
    // old value). Marking them touched makes currentInput send {value: null} and submit include the
    // clear, for ordinary and `ext:` change-set fields alike.
    setTouched(prev => {
      const next = new Set(prev).add(key);
      for (const reset of resets) next.add(reset);
      return next;
    });
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
    // Claim this submit's identity (round 11, F3). If the form/instanceKey changes while the request is
    // in flight — the reset effect bumps submitSeq — this attempt is stale: it drops its result below and
    // leaves the new record's `submitting` flag untouched.
    const attempt = ++submitSeq.current;
    setSubmitting(true);
    try {
      // The SAME complete input builder resolve uses (docs/40, Sol re-review round 8): every
      // initialized Change<T> (own and extension) is sent with its {original, value}, `original` from
      // the FROZEN per-instance baseline (never the latest prop, so a background refresh can't rebase
      // the concurrency baseline under an in-flight edit). Untouched fields arrive as original == value
      // and TamMerge treats them as no-ops — partial persistence and field-level concurrency fall out
      // of the merge, not out of a sparse payload. A conflict override supplies the fresh `original`.
      const body = buildOperationInput(overrides);

      // Submit THROUGH this form binding (docs/40): the server applies the form's tightening on top
      // of the operation contract and folds it into idempotency identity. Omitting it would execute a
      // direct call and silently skip form-specific validation.
      const result = await client.operation(formDef.operation, body,
        { form: props.form, ...(props.actAs ? { actAs: props.actAs } : {}) });
      // The record switched under us — this response belongs to a form instance that no longer exists.
      // Drop it rather than paint a stale conflict / fire onSuccess for the previous record (round 11, F3).
      if (attempt !== submitSeq.current) return;
      setResponse(result);
      if (result.conflicts?.length) {
        // Open a fresh per-field resolution round: the retry (below) carries only the overrides the
        // user explicitly chose, so a later conflict from a third writer starts clean.
        conflictOverrides.current = {};
        resubmitAfterResolve.current = false;
        setPendingConflicts(result.conflicts);
      } else {
        setPendingConflicts([]);
        if (!result.findings.some(f => f.severity === 'error')) props.onSuccess?.(result);
      }
    } finally {
      // Only the current attempt owns the spinner — a stale submit returning after a record switch must
      // not clear the new record's `submitting` (round 11, F3).
      if (attempt === submitSeq.current) setSubmitting(false);
    }
  }, [client, formDef.operation, props, buildOperationInput]);

  // When the LAST conflict has been decided, retry once with the accumulated per-field overrides (F1).
  // Runs from an effect — not inline in the click handler — so a "keep current" setField has flushed to
  // valuesRef before buildOperationInput reads it.
  useEffect(() => {
    if (resubmitAfterResolve.current && pendingConflicts.length === 0) {
      resubmitAfterResolve.current = false;
      void submit(conflictOverrides.current);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingConflicts]);

  const resolveConflict = useCallback((conflict: FieldConflict, choice: 'mine' | 'current') => {
    // The override is the fresh merge base for EITHER choice (Sol re-review round 11, F1): it rebases
    // this field's `original` to the server's CURRENT value, so the retry is a true three-way decision
    // against reality — never against the stale form baseline.
    conflictOverrides.current[conflict.field] = conflict;
    if (choice === 'current') {
      // Adopt the server value too → Original == Value == current. A genuine no-op: WasChanged is false,
      // no field-local validation, no derivation guarded on "did this change" fires for a change the
      // user explicitly rejected. ("use mine" leaves the user's value → Original != Value, applied.)
      const key = conflict.field.startsWith('extensions.')
        ? `ext:${conflict.field.slice('extensions.'.length)}`
        : conflict.field;
      setField(key, conflict.currentValue);
    }
    resubmitAfterResolve.current = true;
    setPendingConflicts(prev => prev.filter(c => c.field !== conflict.field));
  }, [setField]);

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
        // An edit (change-set) field's suggestion is surfaced, not auto-written (round 7, F4): shown
        // as an accept affordance when it differs from the current value. Accepting goes through
        // setField, which marks the field touched, so display and submit agree (round 8, P3). Built-in
        // renderers get it via the `suggestion` prop too; the generic affordance guarantees a generated
        // edit form always shows it, even for renderers that ignore the prop.
        const suggestion = field.changeSet ? resolveState?.fields[field.name]?.suggestedValue : undefined;
        const showSuggestion = suggestion !== undefined && suggestion !== null
          && String(suggestion) !== String(values[key] ?? '');
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
              suggestion={suggestion}
              lookup={resolveState?.fields[field.name]?.lookup}
              actAs={props.actAs}
              tam={tam}
              form={values}
              setField={setField}
            />
            {showSuggestion && (
              <Group gap="xs" mt={4}>
                <Text size="xs" c="dimmed">{t('forms.suggested-value')}: {String(suggestion)}</Text>
                <Button size="compact-xs" variant="subtle" onClick={() => setField(key, suggestion)}>
                  {t('forms.use-suggestion')}
                </Button>
              </Group>
            )}
          </Box>
        );
      })}

      {globalFindings.map((f, i) => (
        <Alert key={i} color={f.severity === 'error' ? 'red' : f.severity === 'warning' ? 'yellow' : 'blue'}>
          {findingMessage(manifest, culture, f)}
        </Alert>
      ))}

      {pendingConflicts.length > 0 && (
        <Alert color="orange" title={t('concurrency.field-conflict')}>
          <Stack gap="xs">
            {pendingConflicts.map(conflict => (
              <Group key={conflict.field} justify="space-between">
                <Text size="sm">
                  <b>{conflict.field}</b>: «{String(conflict.currentValue ?? '—')}» ↔ «{String(conflict.submittedValue ?? '—')}»
                </Text>
                <Group gap="xs">
                  {/* The retry is in flight (round 11, F4): freeze the choices so a second click can't
                      queue a duplicate resubmit or mutate overrides under the running request. */}
                  <Button size="compact-xs" variant="light" disabled={submitting}
                    onClick={() => resolveConflict(conflict, 'current')}>
                    {tam.t('concurrency.keep-current')}
                  </Button>
                  <Button size="compact-xs" disabled={submitting}
                    onClick={() => resolveConflict(conflict, 'mine')}>
                    {tam.t('concurrency.use-mine')}
                  </Button>
                </Group>
              </Group>
            ))}
          </Stack>
        </Alert>
      )}

      <Group justify="flex-end" mt="xs">
        {/* An unresolved conflict round owns the form (round 11, F4): the ordinary Save is disabled so
            the only way forward is to decide each field. Re-enabled once every conflict is resolved (the
            auto-resubmit fires) or the round is cleared by a record switch. */}
        <Button loading={submitting} disabled={pendingConflicts.length > 0}
          onClick={() => void submit()}>
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
