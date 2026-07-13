# 10 — External Integrations

Integrations are **bindings into operations or out of views and events**.

External systems must not mutate internal tables or repositories directly.

The flow should be:

```
External payload
→ external mapping
→ typed operation input
→ normal operation execution
```

## Example

```csharp
[IntegrationBinding("fortnox.orders.import")]
public static partial class ImportFortnoxOrder
{
    public static void Configure(
        IntegrationBuilder<FortnoxOrder, CreateOrder.Input> integration)
    {
        integration.Into(CreateOrder.Definition);

        integration.Map(
            target => target.CustomerId,
            source => ResolveCustomer(source.CustomerNumber));

        integration.Map(
            target => target.WorkAddress,
            source => source.DeliveryAddress);

        integration.Map(
            target => target.Description,
            source => source.Description);

        integration.IdempotencyKey(
            source => source.DocumentNumber);
    }
}
```

## Compile-time validation

The compiler should validate:

- Required fields are mapped
- Types are compatible
- Idempotency exists
- External identity resolution exists
- Integration has permission to invoke the operation
- Ownership policies do not conflict

Mappings that target tenant-defined extension fields cannot be verified at compile time; they are verified by the same rule set at configuration time and continuously against the tenant field registry ([15-extensibility.md](15-extensibility.md)).

## Integration runtime

Provide shared infrastructure for:

- Inbox
- Outbox
- Retries
- Rate limiting
- Dead-letter handling
- Authentication refresh
- Replay
- Checkpoints
- Correlation
- Observability
- Reconciliation

Vendor-specific adapters should contain only:

- External contracts
- Authentication details
- API transport
- Mapping
- Vendor edge cases
- Ownership and conflict policies

## Reconciliation

Integrations should support reconciliation, not only events.

Reconciliation should compare:

```
External identity
Local value
External value
Field ownership
Conflict policy
Recommended action
```

Example:

```
Customer name
Local:    Acme AB
External: ACME Aktiebolag
Owner:    Local
Action:   Update external system
```
