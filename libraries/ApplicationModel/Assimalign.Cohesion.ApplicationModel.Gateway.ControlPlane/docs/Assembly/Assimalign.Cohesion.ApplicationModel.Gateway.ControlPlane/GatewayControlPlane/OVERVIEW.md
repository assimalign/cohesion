# GatewayControlPlane

Static composition entry point. `CreateFactory` produces application-scoped servers,
`CreateClient` produces either gateway-context or fixed-credential resolver clients, and
`Configure` installs non-destructive SDK defaults for the selected run mode.
