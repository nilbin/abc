// Runs the full wire-verification matrix against an ALREADY RUNNING sample host on :5100.
// The server must be on a TRULY FRESH database — the suites create orders, folders, shares
// and rules, and are NOT idempotent: a second run against the same database fails with
// misleading errors (duplicate drafts, already-shared folders). See the verify-loop skill
// for the fresh-SQLite and fresh-Postgres one-liners.
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const suites = ['field-service.mjs', 'rule-builder.mjs', 'rules-gating.mjs', 'documents.mjs'];

const ping = await fetch('http://localhost:5100/api/manifest').catch(() => null);
if (!ping?.ok) {
  console.error('no sample host on http://localhost:5100 — start it on a FRESH database first');
  process.exit(1);
}

let failed = false;
for (const suite of suites) {
  console.log(`\n== ${suite} ==`);
  const result = spawnSync('node', [join(here, suite)], { stdio: 'inherit' });
  if (result.status !== 0) failed = true;
}
process.exit(failed ? 1 : 0);
