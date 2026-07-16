# 33 — The RLS backstop: PostgreSQL row-level security under the EF filters

Status: **BUILT** (the docs/19 D2 defense-in-depth commitment, delivered). Decisions D-R1…D-R8.

The first Postgres run under the naive design (policies = current ∨ read-set, sentinel only for
null scopes) was the real design input: every SANCTIONED cross-tenant read — login enumerating
an account's memberships, the role cascade reading ancestor grants, the anchor walk, inherited
lookups — failed closed, because the database cannot see an app-level `IgnoreQueryFilters`.
The answer is not to weaken the policy but to make the sanction VISIBLE to the database, at the
same granularity the application grants it: per query (D-R7), per branch (D-R8), or per table
(D-R6).

## The problem

The tenant boundary is enforced in ONE application place — the EF global query filter over every
`ITenantScoped` entity (docs/19 D2, the DRY-isolation milestone) — plus the pipeline's write
stamp. That is the primary mechanism and it stays primary. But it has a known failure class:
a raw-SQL escape hatch, a future `IgnoreQueryFilters` misuse, or a bug in filter composition is
a SILENT cross-tenant leak. D2 committed to converting that class from "possible" to "requires
two independent failures" by mirroring the boundary in the database itself.

## The mechanism

Two session settings carry the request's scope to PostgreSQL, and one policy per tenant-scoped
table enforces them:

```
app.tenant_id        the ambient tenant — or '*' in an explicit cross-tenant scope
app.tenant_read_set  comma-joined extra READ tenants (docs/26 subtree views); usually empty
```

```sql
ALTER TABLE "orders" ENABLE ROW LEVEL SECURITY;
ALTER TABLE "orders" FORCE ROW LEVEL SECURITY;      -- the app role OWNS its tables
CREATE POLICY tam_tenant_isolation ON "orders" FOR ALL USING (
    current_setting('app.tenant_id', true) = '*'
    OR "TenantId" = current_setting('app.tenant_id', true)
    OR "TenantId" = ANY (string_to_array(
           nullif(current_setting('app.tenant_read_set', true), ''), ','))
);
```

`TamRls.ProvisionAsync(db)` emits this for every `ITenantScoped` entity in the EF model at
startup (names from EF metadata, idempotent drop/create) — a new tenant-scoped table is covered
by the same convention that gives it the EF filter, so the two layers cannot drift. A
`TamRlsInterceptor` (one class, three EF interceptor roles) keeps the settings true:

- **command role**: before every EF command, compare the context's
  `ITenantScopeContext.CurrentTenantId`/`TenantReadSet` fingerprint with what this connection
  last applied; on change, `set_config(...)` on the same connection/transaction first.
- **connection role**: a (re)opened connection starts unknown — Npgsql's pool reset
  (`DISCARD ALL`) wipes session settings, so the fingerprint clears on open.
- **transaction role**: `set_config` is TRANSACTIONAL — a rollback (or savepoint rollback)
  reverts the settings, so those events clear the fingerprint too.

An unknown fingerprint is never trusted: the next command re-applies. And a connection whose
settings were never applied fails CLOSED — `current_setting(..., true)` returns NULL and the
policy matches nothing.

## Decisions

- **D-R1 — RLS is a backstop, never the mechanism.** The EF filter and pipeline stamp remain
  the tenant boundary; the policies MIRROR them exactly (current ∨ read-set, and the same
  write semantics via `FOR ALL`). Nothing in the application may rely on RLS for correctness —
  it exists to turn one bug into two.
- **D-R2 — the null scope maps to a sentinel, not to nothing.** `CurrentTenantId == null` is
  the framework's EXPLICIT cross-tenant contract (background loops that `IgnoreQueryFilters`
  and filter by row). The database mirror of that contract is `app.tenant_id = '*'`, which the
  policy passes. Request paths always pin a tenant before touching the database (the scope
  middleware), so every REQUEST query runs hard-scoped in the database — which is where the
  forgotten-filter class lives. A leak now needs a forgotten filter AND a background scope.
- **D-R3 — provisioning refuses a bypassing role.** RLS is invisible to superusers and
  `BYPASSRLS` roles; a backstop that silently does not apply is worse than none, so
  `ProvisionAsync` throws if `current_user` bypasses. The app role must be an ordinary owner
  (`NOSUPERUSER NOBYPASSRLS`); `FORCE ROW LEVEL SECURITY` keeps the owner itself subject.
- **D-R4 — it lives in Tam.AspNetCore.Postgres.** RLS is a PostgreSQL capability; the SQLite
  dev path is untouched (docs/29: Npgsql stays out of the core). The host opts in with two
  lines: `options.AddTamRls()` beside `UseNpgsql`, `TamRls.ProvisionAsync(db)` after schema
  creation. The interceptor reads the scope OFF THE CONTEXT (`ITenantScopeContext`), so any
  host DbContext that carries the EF filter carries the settings too — no extra DI.
- **D-R6 — the tenants REGISTRY is exempt.** Topology metadata (ids, paths, display names) that
  every request must read to resolve scope at all — act-as validation, standable sets, ancestor
  paths — and that holds no business rows. Protecting it would deadlock bootstrap (you need the
  registry to decide what you may read, including the registry); exempting it leaks nothing a
  tenant picker doesn't already show its own users.
- **D-R7 — sanctioned cross-tenant READS carry a query tag.** `AcrossTenants()` (in
  Tam.EntityFrameworkCore) is now THE way framework code opts a query out:
  `IgnoreQueryFilters` (the EF half) + `TagWith("tam:cross-tenant")` (the database half). The
  interceptor recognizes the tag ONLY in the leading comment block EF emits — a tag-shaped
  string in a value can't escalate — and runs that one command under the sentinel; the next
  unmarked command flips back (the fingerprint records truth, so precision costs no extra
  round-trips). The two opt-outs travel together and the pairing is greppable; the analyzer
  accepts `AcrossTenants` everywhere it accepted `IgnoreQueryFilters`.
- **D-R8 — the auth branch is a sanctioned cross-tenant SCOPE.** Login enumerates memberships
  across arbitrary tenants and WRITES token/lease rows for the chosen tenant while the ambient
  scope still holds the fallback — reads a tag could cover, writes it cannot (they flush at
  SaveChanges, far from the call site). `TenantScope.EscalateCrossTenant()` marks the request
  sticky for its remainder; `MapTamAuth` applies it to `/connect/*`. It is an escalation
  framework code applies at mapped, auditable places — never reachable from operation input.
- **D-R5 — one policy, `FOR ALL`, read-set included.** Splitting read (current ∨ read-set)
  from write (current only) policies would make the database STRICTER than the application
  contract (the read set is data the request may legitimately see; write discipline is the
  stamp interceptor's job, docs/26 D-H4) and double the provisioning surface. The backstop
  mirrors; it does not editorialize.

## Non-goals

Database-per-tenant routing (D2's escape valve, unchanged); RLS on `[GlobalData]` tables
(they are global by declaration); per-permission or per-role database policies (authorization
is the application's, docs/27); protecting against a hostile DBA or a compromised app role —
the threat model is application bugs, not database operators.
