# 12 — Compiler and Manifest

Use a Roslyn incremental source generator and analyzer package.

The compiler should inspect:

- Operations
- Views
- Bindings
- Derivations
- Dependencies
- Nullability
- Attributes
- Value types
- EF Core model metadata
- Integration mappings
- Change-set operations

It should emit a **compiled application manifest**:

```json
{
  "operations": {},
  "views": {},
  "derivations": {},
  "bindings": {},
  "dependencies": {},
  "integrations": {},
  "permissions": {}
}
```

The manifest should be build-time output. Do not require a large runtime metadata server.

> The manifest schema is the framework's real public API: the TypeScript generator, the frontend runtime, the MCP adapter, and the tenant overlay system all consume it. It must be versioned and treated with the same rigor as a wire protocol (see [review-notes.md](review-notes.md)).

## Static and dynamic state

Static data should be compiled:

- Field types
- Labels
- Operation schemas
- View shapes
- Derivation dependencies
- Default forms
- Default grids
- MCP schemas
- Integration requirements

Dynamic state should be applied as small runtime overlays:

- Current permissions
- Tenant-specific labels
- Feature flags
- User preferences
- Dynamic custom fields ([15-extensibility.md](15-extensibility.md))
- Workflow state
- Runtime options

Effective model:

```
Compiled static model
+ tenant overlay
+ permissions
+ user preferences
+ runtime derivation state
```

## Compiler diagnostics

Diagnostics are a major feature.

Examples:

```
FORM001: ProjectId may be hidden while still required.
FORM002: A derivation cycle exists between CustomerId and ProjectId.
EDIT001: Status is exposed through a generic edit operation.
         Use an intent-specific operation.
EDIT002: A changed field has no semantic equality implementation.
DB001:   Operation permits 1,000 characters but persistence supports 500.
INT001:  Fortnox import does not map required field CustomerReference.
INT002:  Integration writes a field owned by another system.
MCP001:  Operation input contains a type unsupported by the MCP adapter.
VIEW001: Grid declares sorting for a field not sortable by its view.
```

The same rule set runs in a second host at runtime: the tenant field registry evaluates `EXT###` diagnostics when tenant admins define or change custom fields — the registry is, deliberately, *the compiler for runtime-authored facts* ([15-extensibility.md](15-extensibility.md)).

## Impact reports

The compiler should also produce impact reports.

Example:

```
Added CreateOrder.Input.CustomerReference
Affected:
✓ HTTP schema
✓ Web create form
✓ Admin create form
– Mobile binding excludes field
✓ MCP tool schema
✗ Fortnox import missing required mapping
– No database migration required
✓ TypeScript client regenerated
```
