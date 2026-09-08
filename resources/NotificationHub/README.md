# NotificationHub

NotificationHub is the L3 messaging service platform intended to manage subscriptions and audiences, templates, channel routing, delivery policy, receipts, retries, and suppression.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.NotificationHub` defines the public area-root application and builder contracts alongside the existing notification-hub abstraction.
- `Assimalign.Cohesion.NotificationHub.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.
- `Assimalign.Cohesion.NotificationHub.Client` contains the existing client-side contract surface and remains an early implementation.

## Layering and dependencies

As an L3 service platform, NotificationHub composes the L2 `Assimalign.Cohesion.Hosting` runtime and also references the L1 `Assimalign.Cohesion.Core` foundation directly. The Hosting project preserves the one-way boundary by depending only on the area root and shared Hosting runtime; the current area has no other outside-area runtime dependency.

## Project documentation

- [Root overview](./Assimalign.Cohesion.NotificationHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.NotificationHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.NotificationHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.NotificationHub.Hosting/docs/DESIGN.md)
