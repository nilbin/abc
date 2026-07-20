import { describe, expect, it } from 'vitest';
import { buildFormInput, FieldRuntime } from './formInput';
import { ManifestField } from '@tam/core';

// Minimal ManifestField stubs — buildFormInput only reads name/changeSet/extension.
const field = (name: string, opts: Partial<ManifestField> = {}): ManifestField =>
  ({ name, changeSet: false, extension: false, ...opts } as ManifestField);

const change = (name: string) => ({ field: field(name, { changeSet: true }), key: name });
const plain = (name: string) => ({ field: field(name), key: name });
const ext = (name: string) => ({ field: field(name, { extension: true }), key: `ext:${name}` });

describe('buildFormInput — the one resolve/submit input builder (round 8)', () => {
  it('sends every initialized change field complete, even untouched ones', () => {
    const fields: FieldRuntime[] = [change('orderType'), change('description')];
    const baseline = { orderType: 'project', description: 'Old' };
    const values = { orderType: 'project', description: 'New' };   // only description edited

    const input = buildFormInput(fields, baseline, values);

    // Untouched field: complete, with original == value (a TamMerge no-op).
    expect(input.orderType).toEqual({ original: 'project', value: 'project' });
    // Edited field: original from baseline, value the edit.
    expect(input.description).toEqual({ original: 'Old', value: 'New' });
  });

  it('produces identical shape for resolve and submit (same function, same inputs)', () => {
    const fields: FieldRuntime[] = [change('description')];
    const baseline = { description: 'A' };
    const values = { description: 'A' };   // no change yet
    expect(buildFormInput(fields, baseline, values))
      .toEqual(buildFormInput(fields, baseline, values));
    // An untouched edit field is present with original == value on BOTH paths.
    expect(buildFormInput(fields, baseline, values).description).toEqual({ original: 'A', value: 'A' });
  });

  it('shapes an explicit clear as {original, value: null}', () => {
    const input = buildFormInput([change('projectId')], { projectId: 'p1' }, { projectId: null });
    expect(input.projectId).toEqual({ original: 'p1', value: null });
  });

  it('a conflict override supplies the fresh original', () => {
    const input = buildFormInput(
      [change('description')],
      { description: 'A' },
      { description: 'Mine' },
      { description: { field: 'description', currentValue: 'Theirs', submittedValue: 'Mine' } as any });
    expect(input.description).toEqual({ original: 'Theirs', value: 'Mine' });
  });

  it('a conflict override with a null current value sends original: null, not the stale baseline', () => {
    // Round 9, F4: the persisted Current is legitimately null. The override entry EXISTS, so its null
    // must win over the baseline — a `?? baseline` fallback would resend the old base and re-conflict.
    const input = buildFormInput(
      [change('description')],
      { description: 'A' },
      { description: 'Mine' },
      { description: { field: 'description', currentValue: null, submittedValue: 'Mine' } as any });
    expect(input.description).toEqual({ original: null, value: 'Mine' });
  });

  it('bundles initialized extension changes under extensions, complete', () => {
    const fields: FieldRuntime[] = [change('description'), ext('serial')];
    const baseline = { description: 'A', 'ext:serial': 'S1' };
    const values = { description: 'A', 'ext:serial': 'S1' };   // untouched
    const input = buildFormInput(fields, baseline, values) as any;
    expect(input.extensions.serial).toEqual({ original: 'S1', value: 'S1' });
  });

  it('sends a non-change (create) field as its raw value and omits it when null', () => {
    expect(buildFormInput([plain('customerId')], {}, { customerId: 'c1' }))
      .toEqual({ customerId: 'c1' });
    expect(buildFormInput([plain('customerId')], {}, { customerId: null })).toEqual({});
  });
});
