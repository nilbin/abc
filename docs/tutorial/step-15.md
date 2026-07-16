# Step 15 — Norrservice becomes a group *(implemented — [26](../26-tenancy-hierarchy-and-identity.md) + [27](../27-authorization-model.md))*

A year in, Norrservice buys a company in Kiruna. Nothing about Orders changes — what changes is *who stands where*.

**The tree is data.** An admin opens the Companies page (or calls `tenants.create`) and adds `nord` under `demo`; the registry stores a materialized path (`demo.nord`), and every data row keeps carrying exactly one `TenantId` — nothing is denormalized, so re-parenting a company later (`tenants.move`) rewrites paths in the tiny tenants table and touches no data row. Renames are `tenants.rename`; the whole lifecycle is operations, so it is authorized, audited and localized like everything else.

**Grants fan out; writes fan in.** Alva's one membership at `demo` carries `admin` with `cascade: true`, so she can *stand at* any descendant — the login picker and the header switcher offer the whole standable set, labeled by path ("Demo AB ▸ Norrservice Nord AB"). Standing at `nord` (an `X-Tam-Tenant` act-as header the client sets), everything she does — creates, audit rows, events, idempotency — lands in `nord`, because the whole request is re-bound to the target node. Reads widen only where a view asks for it: the orders list itself declares `SubtreeRead` (the standard view IS the group roll-up — the dedicated overview it once had duplicated the real list and is retired; Step 18 shows the grid it drives), the customer lookup declares `inherited` (group-owned reference data readable from every leaf), and everything else stays strictly node-local. The compiler enforces the sharp edge here: composing a widened source into a query without explicitly scoping the other side is a build error (TAM005), because EF's filter opt-out is query-wide.

**Creates fan in to one explicit node — without switching.** A create targets a row that does
not exist yet, so its tenant must be *bound*, never inferred from a rolled-up view (D-H4).
Alva, holding a cascading `orders.create` at `demo`, gets a **target-node field** on the create
form — a lookup over the nodes where her cascaded capability grants create — so she books an
order into `nord` without leaving `demo`, the Azure resource-group pattern. A leaf worker whose
create capability covers only the active node never sees the chooser. The validated target
becomes the request's **execution tenant**, not just a column value: audit, outbox events,
idempotency and the form's own lookups (picking the sub-company's customer) all land in the
target with the row. And the same rebind runs per row on the group's grids: acting on a child
company's row acts *in* that company (Step 18).

**Access is capability — including row reach.** A role says what you can *do*, authored as access levels (`{"orders": "manage"}`) expanding to permission atoms at load time, with `[Sensitive]` fields maskable down to reads *and* writes. Row reach rides the same atoms as the **paired-atom pattern** ([28](../28-assignment-and-grouping.md)): `orders.read` is own-scoped by default, `orders.read-all` widens — so the technician role carries the base atoms and sees only assigned orders on every surface, while the dispatcher role adds the `-all` atoms and works the whole board. No second registry, no policy admin: granting a role with or without the widening atom IS the runtime toggle, and TAM006 verifies at compile time that every view over a widened resource actually applies the scope — fail-closed by construction. The role registry is tenant data with an admin page, validates against the compiled catalogue at define time, and re-resolves per request.

**People arrive by invite.** `users.invite` creates the account and membership up front (the seat is consumed immediately, so the count the admin sees is the count that bills), mails a one-shot hashed link through the `ITamEmail` seam (the dev default logs it), and the invitee sets a password on a framework page. Inviting someone who already has an account elsewhere in the platform just adds the membership — one human, many tenants, one login.

The pattern of Steps 1–14 holds: the tree, the memberships, the roles, the policies, the invites are all *data behind operations* — no deploy moves a company, grants a scope, or seats a user.

**And under all of it, the database holds the line too** *(BUILT — [33-rls-backstop.md](../33-rls-backstop.md))*. The EF global filter is the tenant boundary, but on PostgreSQL it is mirrored in the database itself: `TamRls.ProvisionAsync(db)` at startup creates a row-level-security policy over every `ITenantScoped` table, and `options.AddTamRls()` beside `UseNpgsql` keeps the session's `app.tenant_id`/`app.tenant_read_set` settings true to the ambient scope — including the subtree read set above. Sanctioned cross-tenant reads go through `AcrossTenants()`, THE opt-out that carries both halves (EF's filter skip and the database's query tag) together. The probe that motivated it: delete the app-side filter and the forgotten-filter query returns *zero* foreign rows, because the policy fails closed — one bug no longer leaks a tenant; it now takes two independent failures.
