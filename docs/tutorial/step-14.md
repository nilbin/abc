# Step 14 — Who is asking, and what have they paid for *(implemented — [24-subscriptions.md](../24-subscriptions.md))*

Everything above assumed an actor with permissions. Two framework capabilities produce that actor and bound it, and neither is application code.

**Identity is the framework's own** (`Tam.Auth.OpenIddict`, behind the `IActorProvider` seam). The app calls `AddTamOpenIddict<ErpDbContext>()` and gets an embedded OpenIddict token server: humans sign in with Authorization Code + PKCE on a framework-rendered, localized login page (no password grant — OAuth 2.1), pick their organization when they have more than one, and the SPA renews a 10-minute access token silently with a rotating refresh token. Agents and integrations use client credentials (a machine client acts as a same-named account, so an agent has roles and an audited identity like any human). Accounts are platform-global with per-tenant memberships ([26](../26-tenancy-hierarchy-and-identity.md)); grants resolve *fresh from the membership's roles and policies on every request*, so revoking a role beats the token's lifetime. The token machinery is hardened for a public client holding its own tokens: one-time-use rotation with reuse detection (a replayed refresh token revokes its whole family), revocation on sign-out, entry validation so revocation bites immediately, and a pruned token store. Swap in any external IdP by replacing the provider; the rest of the framework never knows.

```csharp
builder.Services.AddTam<ErpDbContext>(model);
builder.Services.AddTamOpenIddict<ErpDbContext>();   // the whole auth story, one line
app.MapTamAuth();                                    // /connect/authorize + /connect/token + login/picker/invite pages
```

**Entitlements bound what that actor can reach** (docs/24). A tenant's subscription — plan, seats, plugin entitlements — is data a billing provider drives through `subscriptions.set-plan` (a service-actor operation, not the tenant admin: a Stripe webhook maps to one call). Two mechanical gates, both already the right place: `plugins.activate` refuses a plugin the plan doesn't entitle (a localized upsell finding, not a crash), and `users.define` refuses a new user past the seat ceiling. A tenant tree with no anchor on its chain falls to the host-configured `unconfigured` default — bootstrap-sized, anchored at the root so child nodes never mint fresh seat pools — and self-hosted deployments set `SubscriptionDefaults.Unlimited` once, so the framework runs fully without any billing wired up. This is how the inspect and fortnox plugins of the last two steps become things a tenant *buys*: the marketplace adds the plugin id to `PluginEntitlements`, and activation starts succeeding — the framework never touches money, it reads one boolean.

When Norrservice becomes a group (next step), the subscription does what capability does:
**it cascades**. A subscription row is an *anchor* — it covers its own node and every
descendant until a nearer anchor shadows it — so the Kiruna company Norrservice acquires is
entitled to everything the group plan pays for the moment it is created, with no row of its
own. Seats pool at the anchor: the count spans the covered subtree and the seat lease lands on
the anchor's row, so two invites racing at different child companies conflict at commit instead
of both slipping under the ceiling — and one region admin with a cascading membership is one
seat, not fifty. A subsidiary on genuinely separate billing gets a **sub-anchor**
(`subscriptions.set-plan` run while acting at that node — billing-provider-only by
construction, since `subscriptions.manage` is a reserved atom no role can grant), and the
boundary is absolute: no entitlement unions, no seat borrowing. `tenants.move` across an anchor
boundary succeeds with `subscriptions.entitlement-lost` / `seat-overflow` WARNINGS — a re-org
is never held hostage to billing, and enforcement rides the ordinary downgrade semantics.

The whole request now reads top to bottom as data: *the OpenIddict token names a user → the user's roles resolve to grants → the grants pass the operation's `[Authorize]` → the plan entitles the plugin the operation belongs to → the seat/entitlement gates hold → the pipeline runs → the audit row records the human's name.* Every arrow is a row in a table a tenant admin or a billing webhook can change without a deploy.
