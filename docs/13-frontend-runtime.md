# 13 — Frontend Runtime

Generate a portable TypeScript manifest and clients.

Initial frontend packages:

```
@tam/core
@tam/react
```

Later:

```
@tam/react-native
@tam/vue
@tam/web-components
```

## React usage

```tsx
<OperationForm operation="orders.create" />
```

Grid usage:

```tsx
<ViewGrid view="orders.list" />
```

## Custom renderers

```ts
runtime.registerRenderer(
  "customer-id",
  CustomerPicker);

runtime.registerRenderer(
  "address",
  AddressEditor);

runtime.registerRenderer(
  "gps-assisted-address",
  GpsAddressEditor);
```

Overrides should be possible without abandoning the operation contract:

```tsx
<OperationForm operation="orders.create">
  <OperationForm.Override
    field="workAddress"
    component={MapAddressEditor}
  />
</OperationForm>
```

## Generated source stays minimal

Prefer:

- Generated types
- Generated clients
- Compiled manifests
- Generic runtime
- Handwritten exceptional components

Avoid large generated React component trees.

## Manifest-driven rendering and tenant fields

Because `OperationForm` and `ViewGrid` render from field descriptors (not from generated components), a field descriptor added at runtime by the tenant overlay renders identically to a compiled one. Renderers are keyed by semantic type, so a tenant-defined "email" field gets the same editor as a compiled `EmailAddress` field. Tenant fields therefore appear in forms, grids, and reports with **zero frontend deployments** ([15-extensibility.md](15-extensibility.md)).

Generated TypeScript types expose extension data as a typed container (`ExtensionData`) rather than per-field properties; per-field static typing is only possible for compiled fields. Application code that needs to touch a specific tenant field (rare — normally only the generic runtime does) accesses it by field key through the container API.
