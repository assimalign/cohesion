# GatewayControlPlane

Static composition entry point. `CreateFactory` produces application-scoped servers,
`CreateClient` produces either gateway-context or fixed-credential resolver clients, and
`Configure` installs non-destructive SDK defaults for the selected run mode. For `Run` and
`Apply`, `Configure` adapts the first gateway command client of each kind into a server
dispatcher. The gateway selects clients the same way: exact kind first, then `AnyKind`.
