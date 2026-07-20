// @vitest-environment jsdom
import React from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { OperationForm } from './OperationForm';
import { TamContext, TamContextValue } from './context';

// Mount the REAL OperationForm and assert on the resolve requests its effect lifecycle produces —
// the coverage the pure buildFormInput tests can't reach (Sol re-review round 9, F5). Focus: the
// identity-switch invariant (F1) — an identity change always resolves the freshly frozen baseline,
// never a mix of the previous record's values under the new baseline.

beforeAll(() => {
  // Mantine needs these in jsdom.
  window.matchMedia ??= ((q: string) => ({
    matches: false, media: q, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {} unobserve() {} disconnect() {}
  };
});

afterEach(cleanup);

const descField = { name: 'description', changeSet: true, extension: false, labelKey: 'description', renderer: 'hidden' };

// Two forms sharing the change field `description`: one WITHOUT server derivations, one WITH. The
// shared field name is what would expose a mixed-record request — the old record's `description`
// value under the new record's baseline.
const manifest = {
  revision: 1,
  operations: { 'op.noderiv': {}, 'op.deriv': {}, 'op.twofield': {} },
  extensions: {},
  forms: {
    'form.noderiv': {
      operation: 'op.noderiv', fields: [descField], includeExtensions: false,
      serverDependencies: [], hasServerDerivations: false,
    },
    'form.deriv': {
      operation: 'op.deriv', fields: [descField], includeExtensions: false,
      serverDependencies: [], hasServerDerivations: true,
    },
    'form.twofield': {
      operation: 'op.twofield',
      fields: [descField, { name: 'budget', changeSet: true, extension: false, labelKey: 'budget', renderer: 'hidden' }],
      includeExtensions: false, serverDependencies: [], hasServerDerivations: false,
    },
  },
} as unknown as TamContextValue['manifest'];

function makeClient() {
  return {
    resolve: vi.fn().mockResolvedValue({ fields: {}, findings: [], revision: 1 }),
    operation: vi.fn().mockResolvedValue({ findings: [], effects: [], conflicts: [] }),
  };
}

function ctxFor(client: ReturnType<typeof makeClient>): TamContextValue {
  return {
    client: client as unknown as TamContextValue['client'],
    manifest,
    culture: 'en',
    setCulture: () => {},
    refreshManifest: async () => {},
    t: (k: string) => k,
    tOr: (keys: string[]) => keys[0],
    can: () => true,
    invalidate: () => {},
  };
}

function renderForm(client: ReturnType<typeof makeClient>, props: Record<string, unknown>) {
  return render(
    <MantineProvider>
      <TamContext.Provider value={ctxFor(client)}>
        <OperationForm {...(props as { form: string })} />
      </TamContext.Provider>
    </MantineProvider>,
  );
}

const changeOf = (client: ReturnType<typeof makeClient>, callIndex: number) =>
  (client.resolve.mock.calls[callIndex][1] as { description?: unknown }).description;

describe('OperationForm resolve lifecycle', () => {
  it('resolves the baseline on mount of a derivation-backed form', async () => {
    const client = makeClient();
    renderForm(client, { form: 'form.deriv', instanceKey: 'b1', initialValues: { description: 'B1' } });
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(1));
    expect(changeOf(client, 0)).toEqual({ original: 'B1', value: 'B1' });
  });

  it('a record switch resolves ONLY the new baseline', async () => {
    const client = makeClient();
    const view = renderForm(client, { form: 'form.deriv', instanceKey: 'b1', initialValues: { description: 'B1' } });
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(1));

    view.rerender(
      <MantineProvider>
        <TamContext.Provider value={ctxFor(client)}>
          <OperationForm form="form.deriv" instanceKey="b2" initialValues={{ description: 'B2' }} />
        </TamContext.Provider>
      </MantineProvider>,
    );
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(2));
    // The post-switch resolve is exactly the new baseline — no b1 value carried under the b2 baseline.
    expect(changeOf(client, 1)).toEqual({ original: 'B2', value: 'B2' });
  });

  it('gaining derivations during an identity switch sends the new baseline, never a mixed record (F1)', async () => {
    const client = makeClient();
    // Mount a form with NO derivations — no resolve happens.
    const view = renderForm(client, { form: 'form.noderiv', instanceKey: 'a', initialValues: { description: 'A' } });
    await waitFor(() => expect(client.resolve).not.toHaveBeenCalled());

    // Switch to a DIFFERENT form/record that HAS derivations — identity changed AND derivations gained.
    view.rerender(
      <MantineProvider>
        <TamContext.Provider value={ctxFor(client)}>
          <OperationForm form="form.deriv" instanceKey="b" initialValues={{ description: 'B' }} />
        </TamContext.Provider>
      </MantineProvider>,
    );
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(1));
    // The baseline effect owns it: the new record's baseline, NOT the previous record's 'A' value.
    expect(changeOf(client, 0)).toEqual({ original: 'B', value: 'B' });
    for (const call of client.resolve.mock.calls)
      expect((call[1] as { description: { value: unknown } }).description.value).not.toBe('A');
  });

  it('a context (actAs) change for the SAME record re-resolves without a record switch', async () => {
    const client = makeClient();
    const view = renderForm(client, {
      form: 'form.deriv', instanceKey: 'b1', initialValues: { description: 'B1' }, actAs: 'tenant-1',
    });
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(1));

    view.rerender(
      <MantineProvider>
        <TamContext.Provider value={ctxFor(client)}>
          <OperationForm form="form.deriv" instanceKey="b1" initialValues={{ description: 'B1' }} actAs="tenant-2" />
        </TamContext.Provider>
      </MantineProvider>,
    );
    // Same identity, changed context → the context-refresh effect resolves (not the baseline effect).
    await waitFor(() => expect(client.resolve).toHaveBeenCalledTimes(2));
    const opts = client.resolve.mock.calls[1][4] as { actAs?: string };
    expect(opts.actAs).toBe('tenant-2');
  });

  it('resolves each conflict on its OWN row and retries with only the chosen overrides (F1)', async () => {
    const client = makeClient();
    // First submit → two conflicts; the retry → success.
    client.operation
      .mockResolvedValueOnce({
        findings: [], effects: [], conflicts: [
          { field: 'description', currentValue: 'srvDesc', submittedValue: 'myDesc' },
          { field: 'budget', currentValue: 'srvBudget', submittedValue: 'myBudget' },
        ],
      })
      .mockResolvedValueOnce({ findings: [], effects: [], conflicts: [] });

    renderForm(client, {
      form: 'form.twofield', instanceKey: 'x',
      initialValues: { description: 'myDesc', budget: 'myBudget' },
    });
    const conflictButtons = () => Array.from(document.querySelectorAll('button'))
      .filter(b => b.textContent === 'concurrency.use-mine' || b.textContent === 'concurrency.keep-current');

    // Submit via the form's own save button (only button on screen before conflicts appear).
    fireEvent.click(document.querySelector('button')!);
    await waitFor(() => expect(client.operation).toHaveBeenCalledTimes(1));

    // Two rows → four buttons, ordered [desc keep, desc mine, budget keep, budget mine].
    await waitFor(() => expect(conflictButtons()).toHaveLength(4));
    fireEvent.click(conflictButtons()[1]);   // description → "use mine"

    // The description row disappears; only the budget row remains.
    await waitFor(() => expect(conflictButtons()).toHaveLength(2));
    fireEvent.click(conflictButtons()[0]);   // budget → "keep current"

    // Resolving the last conflict fires exactly one retry, carrying only description's override.
    await waitFor(() => expect(client.operation).toHaveBeenCalledTimes(2));
    const body = client.operation.mock.calls[1][1] as {
      description: { original: unknown; value: unknown }; budget: { original: unknown; value: unknown };
    };
    // "use mine" for description → original rebased to the server current; my value kept.
    expect(body.description).toEqual({ original: 'srvDesc', value: 'myDesc' });
    // "keep current" for budget → the field's value became the server current; NO override (original
    // stays the baseline), so the merge sees Value == Current and resolves it.
    expect(body.budget.value).toBe('srvBudget');
    expect(body.budget.original).toBe('myBudget');
  });
});
