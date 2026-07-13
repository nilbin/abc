# 11 — MCP and Agent Support

Operations should be exposable as MCP tools. Views should be exposable as resources or query tools. Derivations should support preflight resolution and elicitation.

## Example flow

```
Agent calls orders.create.resolve with partial input
Server returns:
  - missing required fields
  - available selections
  - warnings
  - server validation findings
  - suggested values
Agent gathers missing input
Agent calls orders.create
```

The same semantics should serve:

- Human forms
- Mobile workflows
- MCP elicitation
- External integrations
- Automated workflows

**Agents must invoke the same operations as humans.** There should be no separate "agent business logic" layer.

## Per-tenant schemas

MCP tool and resource schemas are generated from the **effective manifest** (compiled manifest + tenant overlay), so agents see tenant-defined custom fields with types, constraints, labels, and admin-authored descriptions — and can read and write them through the same operations as humans, with the same validation and findings. A tenant admin writing a good description for a custom field is simultaneously documenting it for every agent. See [15-extensibility.md](15-extensibility.md).
