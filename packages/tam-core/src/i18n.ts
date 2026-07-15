// Localization (docs/21): resolve locally from catalogs; server message is the fallback.

import type { Manifest, Finding } from './manifest';


export function translate(
  manifest: Manifest, culture: string, key: string,
  args?: Record<string, unknown>): string {
  const template = manifest.catalogs[culture]?.[key]
    ?? manifest.catalogs[manifest.defaultCulture]?.[key]
    ?? key;
  if (!args) return template;
  return template.replace(/\{(\w+)\}/g, (_, k) =>
    args[k] !== undefined ? String(args[k]) : `{${k}}`);
}

export function findingMessage(
  manifest: Manifest, culture: string, finding: Finding): string {
  const template = manifest.catalogs[culture]?.[finding.code];
  if (!template) return finding.message ?? finding.code;
  return template.replace(/\{(\w+)\}/g, (_, k) =>
    finding.args?.[k] !== undefined ? String(finding.args[k]) : `{${k}}`);
}

/** PascalCase enum name → camelCase wire value (single shared conversion). */
export function toWireEnum(name: string): string {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

export function enumLabel(manifest: Manifest, culture: string, value: unknown): string {
  if (value === null || value === undefined) return '';
  const s = String(value);
  const kebab = toWireEnum(s).replace(/[A-Z]/g, m => '-' + m.toLowerCase());
  const key = `enums.${kebab}`;
  const hit = manifest.catalogs[culture]?.[key] ?? manifest.catalogs[manifest.defaultCulture]?.[key];
  return hit ?? s;
}
