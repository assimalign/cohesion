# Health

Cohesion's health-check area: a shared in-app model for liveness/readiness probes and the
in-process producer of the ApplicationModel `Degraded` signal.

## Purpose

A multi-service framework whose headline gateway is Kubernetes needs an in-app health surface:
kubelet liveness/readiness probes need an HTTP endpoint, and the control plane's
`ResourceLifecycle.Degraded` needs an in-process producer. Without a shared model each resource
host would grow its own ad-hoc health surface. This area provides that one model.

## Projects

| Project | Role |
|---------|------|
| [`Assimalign.Cohesion.Health`](Assimalign.Cohesion.Health/) | Contracts, report model, builder-time registry, check engine, publish seam. Zero dependencies, AOT-safe. |
| [`Assimalign.Cohesion.Health.Hosting`](Assimalign.Cohesion.Health.Hosting/) | The periodic publisher (rides the Hosting execution menu as a `BackgroundService`) plus the builder-time DI extensions. |

The HTTP endpoint (`/healthz`, `/livez`, `/readyz`) is a Web feature project:
`resources/Web/Assimalign.Cohesion.Web.Health`.

## Layering

L1 foundation. The core library is dependency-free; the `.Hosting` project is the only
DI/host-lifecycle seam. The Web endpoint (L3, `resources/Web`) and the ApplicationModel
orchestration plane are consumers.

See each project's `docs/DESIGN.md` for the design rationale.
