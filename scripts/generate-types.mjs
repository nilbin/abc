#!/usr/bin/env node
// Generates typed TS contracts + a typed client from the exported manifest.
// The generic runtime stays manifest-driven; this gives handwritten app code
// compile-time types for operation inputs/outputs and view rows (docs/13).
//
// Usage: node scripts/generate-types.mjs [manifest.json] [out.ts]
import fs from 'node:fs';
import path from 'node:path';

const manifestPath = process.argv[2] ?? 'samples/erp/manifest.baseline.json';
const outPath = process.argv[3] ?? 'apps/web/src/generated/tam.ts';
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

const pascal = (id) => id.split(/[.\-_]/).map(w => w.charAt(0).toUpperCase() + w.slice(1)).join('');
const camel = (id) => { const p = pascal(id); return p.charAt(0).toLowerCase() + p.slice(1); };

function tsType(field) {
  const base = {
    string: 'string', number: 'number', integer: 'number',
    boolean: 'boolean', date: 'string', datetime: 'string', object: 'Record<string, unknown>',
  }[field.wireKind] ?? 'unknown';
  const withEnum = field.options
    ? field.options.map(o => JSON.stringify(o.charAt(0).toLowerCase() + o.slice(1))).join(' | ')
    : base;
  return field.changeSet ? `Change<${withEnum}>` : withEnum;
}

function emitInterface(name, fields, extensible) {
  const lines = fields.map(f => {
    const optional = f.changeSet || !f.required ? '?' : '';
    return `  ${f.name}${optional}: ${tsType(f)};`;
  });
  if (extensible) lines.push('  extensions?: Record<string, Change<unknown>>;');
  return `export interface ${name} {\n${lines.join('\n')}\n}`;
}

const parts = [`// GENERATED from ${path.basename(manifestPath)} — do not edit (scripts/generate-types.mjs).
/* eslint-disable */
import type { OperationResponse, ViewResponse, TamClient } from '@tam/core';

export interface Change<T> { original: T | null; value: T | null; }

export interface TypedOperationResponse<TOutput> extends OperationResponse {
  output?: TOutput & Record<string, unknown>;
}`];

const clientMethods = [];

for (const [id, op] of Object.entries(manifest.operations)) {
  const name = pascal(id);
  parts.push(emitInterface(`${name}Input`, op.fields, !!op.extensibleEntity));
  if (op.outputFields.length > 0) parts.push(emitInterface(`${name}Output`, op.outputFields, false));
  const output = op.outputFields.length > 0 ? `${name}Output` : 'Record<string, unknown>';
  clientMethods.push(
    `  /** ${id} (requires ${op.permission}) */\n` +
    `  ${camel(id)}(input: ${name}Input, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<${output}>> {\n` +
    `    return this.client.operation(${JSON.stringify(id)}, input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<${output}>>;\n  }`);
}

for (const [id, view] of Object.entries(manifest.views)) {
  const name = pascal(id);
  parts.push(emitInterface(`${name}Row`, view.resultFields, false));
  parts.push(emitInterface(`${name}Query`, view.queryFields.map(f => ({ ...f, required: false })), false));
  clientMethods.push(
    `  /** view ${id} (requires ${view.permission}) */\n` +
    `  ${camel(id)}(query?: ${name}Query & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<ViewResponse & { rows: ${name}Row[] }> {\n` +
    `    return this.client.view(${JSON.stringify(id)}, query as unknown as Record<string, unknown>) as Promise<ViewResponse & { rows: ${name}Row[] }>;\n  }`);
}

parts.push(`export class TypedTamClient {
  constructor(readonly client: TamClient) {}

${clientMethods.join('\n\n')}
}`);

fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, parts.join('\n\n') + '\n');
console.log(`generated ${outPath}: ${Object.keys(manifest.operations).length} operations, ${Object.keys(manifest.views).length} views`);
