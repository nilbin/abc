# Step 8 — What the machine callers see *(BUILT)*

The same feature, no extra code:

**HTTP** — `POST /api/operations/orders.create`, `orders.edit-details`, `orders.complete`; `GET /api/views/orders.list?status=open&sort=number&dir=desc&page=2` — filter and sort parameters are the camelCase wire names, enum values camelCase (`status=open`), direction a separate `dir`. OpenAPI documents all of it at `/openapi.json`.

**TypeScript** —

```ts
const result = await client.ordersCreate({
  customerId, orderType: "service",
  workAddress: "Industrigatan 4, Västerås",
  description: "Byt packning på huvudpump",
});
// result.output?.number — TypedOperationResponse<OrdersCreateOutput>, typed end to end
```

The generated client (`samples/web/src/generated/tam.ts`) is flat camelCase methods over operation ids; an `{ idempotencyKey }` option becomes the `X-Idempotency-Key` header.

**MCP** — tool names replace the id's dots and dashes with underscores (`orders_create`, `views_orders_list`), and the preflight tool rides the FORM id — interaction state is a form concern — so an agent asked to "create a project order for Acme's pump replacement" does:

```
→ tool: web_orders_create_resolve   { "customerId": "…", "orderType": "project" }
← { "fields": {
      "projectId": { "visible": true, "enabled": true, "required": true,
        "options": [ { "value": "…", "label": "Pumprenovering 2026" },
                     { "value": "…", "label": "Serviceavtal årligt" } ],
        "findings": [] },
      "workAddress": { "visible": true, "enabled": true, "required": true,
        "suggestedValue": "Industrigatan 4, Västerås", "findings": [] } },
    "findings": [], "revision": 3 }

→ tool: orders_create   { …full input… }
← { "output": { "orderId": "…", "number": "2026-01418" }, … }   ← the Step-2 envelope, verbatim
```

Every field's resolved state carries `visible`/`enabled`/`required`/`suggestedValue`/`options`/`findings`; the suggested address is a single string because `Address` is a single-string value type. Had the agent picked the credit-blocked customer instead, a `customers.credit-blocked` warning finding would arrive with its `message` resolved in the connection's culture ("Kunden är kreditspärrad.") from the same catalogs the web form uses — the `code` is what the agent branches on. Idempotency is the same `X-Idempotency-Key` HTTP header on the MCP request, not a body property. The agent hit the same derivations, the same validation, the same audit trail as the web form — `resolve` is the form runtime's endpoint wearing a tool schema. There is no agent-specific business logic anywhere in the feature.

---
