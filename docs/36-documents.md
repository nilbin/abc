# 36 — Documents: folders, files, and the first reach consumer

Status: **BUILT** (the `tam.documents` framework package, the reach-backed folder ACLs, magic
folders with `DOC001`, the record documents tab, the documents browser page; 22-check wire
suite on SQLite and PostgreSQL). Deferred, tracked in STATUS: streaming/multipart uploads
(today's upload is base64 with a 5 MB cap), the reach picker UI for shares (D-R5), per-kind
document contracts for plugins.

`tam.documents` is a framework package by docs/22's own test: domain-agnostic (every business
app grows a "put the PDF somewhere" need), no meaningful per-tenant activation toggle, and it
exercises the newest seams daily — it is the first CONSUMER of the reach seam (docs/35), a
wildcard effect subscriber, and the first `Task<IQueryable<…>>` view (docs/04).

## The folder tree is a path, not a parent pointer

A folder row stores its **materialized path** (`/avtal/2026`), unique per tenant. That one
choice shapes everything downstream:

- **`documents.folders.define` is `mkdir -p`**: defining `/a/b/c` ensures every ancestor
  exists; redefining a retired path un-retires it. There is no "move" — paths are identity
  (retire-don't-drop, as everywhere).
- **Ancestry is a string prefix**, so effective-ACL resolution and subtree queries are index
  walks, not recursive CTEs.

## ACLs are stored ReachRefs — inheritance with OVERRIDE

A share is a stored row: folder → `ReachRef` (`role:dispatcher`, `user:0d3f…`, `tenant`,
`approvals.group:7a41…`). The **effective ACL** of a folder is *its own rows if it has any,
otherwise the nearest ancestor's* — own rows REPLACE inheritance rather than union with it, so
a child can be locked TIGHTER than its parent (`/avtal` shared to dispatchers,
`/avtal/löner` shared to one user). `documents.folders.unshare` removes rows; removing the
last own row restores inheritance.

Enforcement is ONE predicate — `DocumentAccess.VisibleFolderIdsAsync` — used by every read
(folder list, document list, download) AND every write (upload re-checks visibility of the
target folder): the docs/28 rule that share edges are domain data enforced by the domain on
both sides, made concrete. Actors holding the manage atom bypass reach (someone must see the
whole tree to administer it). Containment goes through `ReachResolver` and is therefore
**fail-closed** (docs/35): a malformed ref, an unknown kind, or a kind whose plugin is
deactivated makes the row INERT — never an error, never wider access. Deactivating the
approvals plugin silently narrows every folder shared to an approver group; reactivating
restores it.

## Documents: attached by EntityRef, stored by content

- **`AttachedTo`** is an `EntityRef` string (`order:3f2a…`) — the docs/35 record-reference
  vocabulary, validated against the model's extensible entities at upload. A record's
  documents are one mechanical filter (`documents.list?attachedTo=order:…`), which is exactly
  what the record page's documents tab binds (docs/32, `QueryEntityRef`).
- **Content is content-addressed**: blobs are keyed by SHA-256 per tenant, so ten uploads of
  the same PDF store one blob. Storage hides behind `IDocumentStore` (registered with
  `TryAddScoped` — the DB-backed default swaps for S3/disk without touching the package).
- **Download** (`GET /api/documents/{id}/content`) requires the read atom AND folder
  visibility, and answers **404 — not 403 — when out of reach**: an existence leak is a leak.
- `documents.retire` retires; nothing deletes.

## Magic folders: the tree grows itself

The host binds events to path templates in its model:

```csharp
model.DocumentFolder("order-created", "/order/{number}");
```

`DOC001` verifies at Build() that the event is declared (`PublishesEvent`) and that every
`{placeholder}` is a field the event carries. At runtime a package subscriber listens with the
wildcard subscription (`OnEffect("*")` — the outbox dispatches `"*"` subscribers for every
event; PLG009 exempts the wildcard), renders the template from the payload, and `mkdir -p`s
the result — idempotent under redelivery, and a template whose placeholder resolves empty is
skipped rather than materialized as a broken path. Completing the loop: the order-created
event that drives inspection checklists also, with one model line, gives every order its
document folder.

## The UI that falls out

- **Record tabs**: the orders detail page declares a documents tab bound by
  `bind.QueryEntityRef("attachedTo", "order")` — the manifest carries `$ref:order`, the client
  composes `order:{recordKey}` (docs/32). Upload from the tab pre-fills the attachment.
- **The browser page** (`samples/web` `documents-browser`, registered page): folder tree from
  `documents.folders.list`, files from `documents.list`, download through the typed client's
  `blob()` (bearer + retry like every other call), define/upload as ordinary operation forms.
  The file input is a standard renderer (`file`): base64 payload plus `fileName`/`contentType`
  side fields — no bespoke upload endpoint.
- **Sharing lives in the browser too**: the share dialog is ONE multi-select whose pills ARE
  the selected folder's own grants (`documents.folders.shares`, admin-only like the intents,
  described labels via docs/35 D-R6) — picking adds a grant, removing a pill revokes it, each
  an immediate share/unshare intent. Inherited/open access shows as the empty-state hint —
  the effective-ACL question stays server-side (D-DOC2).
- **And that is ALL the nav there is.** The package deliberately suggests no pages
  (`plugin.Nav(nav => nav.None())` — declaring nav, even empty, graduates it past the
  mechanical More-page, docs/30 D-N1): documents surface on the records they attach to and in
  the host's browser, not as flat admin lists. The `web.documents.*` grids stay declared —
  wire names are permanent (D4) — for hosts that do want flat pages.

## Decisions

- **D-DOC1 — ACL rows override, never union.** Own rows replace inherited ones so subtrees can
  narrow. The alternative (union) can only ever widen down the tree, which is the wrong
  default for payroll folders.
- **D-DOC2 — one visibility predicate.** Reads and writes share
  `DocumentAccess.VisibleFolderIdsAsync`; there is no second, subtly different filter to
  drift.
- **D-DOC3 — attachment is an EntityRef string, not a foreign key.** Documents attach to ANY
  extensible entity — host or plugin — without the package referencing anyone's tables;
  validation is against the model, not the schema.
- **D-DOC4 — content addressing behind `IDocumentStore`.** Dedupe is a property of the store
  contract (hash key), not of any particular backend.
- **D-DOC5 — out-of-reach downloads are 404.** Authorization failures on addressable blobs
  must not confirm existence.
- **D-DOC6 — magic folders are model bindings, verified by `DOC001`.** The event→folder rule
  lives in the model like every other binding, checked at build against the declared event
  contract — not a runtime convention that fails silently on a renamed field.
