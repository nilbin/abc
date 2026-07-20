import { FieldConflict, ManifestField } from '@tam/core';

/** One own or extension field's runtime binding: the manifest field + its values-map key
 *  (own fields use `f.name`, extension fields use `ext:{name}`). */
export interface FieldRuntime {
  field: ManifestField;
  key: string;
}

/**
 * The ONE operation-input builder for both resolve and submit (docs/40, Sol re-review round 8).
 * Pure and dependency-free so it is directly testable: given the frozen baseline and the current
 * values, it produces the complete wire input. Every INITIALIZED change-set field (own and
 * extension) carries its complete `{original, value}` object — `original` from the frozen baseline
 * (a conflict override supplies a fresh one) — so a derivation sees the complete proposed state and
 * TamMerge derives the actual patch from `original != value`. An untouched field (original == value)
 * is a no-op that takes no concurrency check. Non-change (create) fields send their raw value; a
 * null/undefined create field is omitted (it deserializes to null server-side).
 */
export function buildFormInput(
  fields: FieldRuntime[],
  baseline: Record<string, unknown>,
  values: Record<string, unknown>,
  overrides?: Record<string, FieldConflict>,
): Record<string, unknown> {
  const input: Record<string, unknown> = {};
  const extensions: Record<string, unknown> = {};
  for (const { field, key } of fields) {
    const value = values[key];
    if (field.extension) {
      if (baseline[key] === undefined && value === undefined) continue;
      const original = overrides?.[`extensions.${field.name}`]?.currentValue ?? baseline[key] ?? null;
      extensions[field.name] = { original, value: value ?? null };
    } else if (field.changeSet) {
      if (baseline[key] === undefined && value === undefined) continue;
      const original = overrides?.[field.name]?.currentValue ?? baseline[key] ?? null;
      input[field.name] = { original, value: value ?? null };
    } else if (value !== undefined && value !== null) {
      input[field.name] = value;
    }
  }
  if (Object.keys(extensions).length > 0) input.extensions = extensions;
  return input;
}
