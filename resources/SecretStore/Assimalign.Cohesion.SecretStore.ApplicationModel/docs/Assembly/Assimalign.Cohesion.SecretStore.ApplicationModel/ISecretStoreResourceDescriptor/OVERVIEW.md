# ISecretStoreResourceDescriptor

`ISecretStoreResourceDescriptor` is the area-typed descriptor returned by
`AddSecretStore`. Its `Resource` property exposes `SecretStoreResource` directly while
the inherited application-model members retain ordinary dependency chaining.

The concrete descriptor is internal. Application code depends only on this public
contract and the shared `IApplicationResourceDescriptor` surface.

## See also

- [SecretStoreResource](../SecretStoreResource/OVERVIEW.md)
- [SecretStoreResourceExtensions](../SecretStoreResourceExtensions/OVERVIEW.md)
