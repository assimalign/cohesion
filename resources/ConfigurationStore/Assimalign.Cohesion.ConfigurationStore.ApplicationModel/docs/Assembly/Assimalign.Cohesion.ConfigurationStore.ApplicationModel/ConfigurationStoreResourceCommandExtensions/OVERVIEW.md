# ConfigurationStoreResourceCommandExtensions

Extension members on `IConfigurationStoreResourceDescriptor` attach typed commands and return the same
descriptor for fluent chaining. Each method accepts `optional = false`; optional controls gateway
reconciliation behavior, without relaxing payload validation or ownership.

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `configurationstore.add-namespace` | `AddNamespace` | namespace name |

AddNamespace creates a namespace if absent and atomically stores its owner and original seed
alongside values. An identical declaration succeeds even after separate value commands change its
contents. A different seed or foreign owner is rejected with a named detail. Resource-seeded namespaces
are not implicitly adopted. Deletion removes the owned namespace; callers should remove its value
commands first. Existing SetValue and RemoveValue behavior remains unchanged, including 404 for
unknown namespaces.

Blank required strings, invalid single-segment keys and malformed argument values throw argument
exceptions naming the offending parameter. Build rejects unadvertised kinds or duplicate target
keys through ResourceCommandValidator. Serialization uses the internal generated JSON context.
