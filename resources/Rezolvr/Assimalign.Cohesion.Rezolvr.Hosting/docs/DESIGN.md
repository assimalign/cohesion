# Assimalign.Cohesion.Rezolvr.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `RezolvrApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

## Composition execution model

Each build creates a new context, invokes every registered service factory exactly once against that context, and exposes the materialized services as an ordered, read-only `HostedServices` snapshot. The shared host starts services in registration order and stops them in reverse; an unregistered resource with an unconfigured builder still produces an empty collection and a production `HostEnvironment`.

`ResolverEndpointService` remains as a dormant future service stub and is not registered by default.

## Boundaries

The module references the area root and Hosting, Hosting.Health, and Hosting.Resources publicly. Its Web, Web.Hosting, Web.ControlPlane, HTTP, and transport implementation dependencies are private, with their resolved closure supplied by the area runtime framework. Hosting never references its own ApplicationModel package. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

## Enabled resource lifecycle

The builder discovers the entry assembly's registered default control plane through ResourceRuntime.TryCreateControlPlane. The host environment carries the ambient environment name and content root. Build adds the host health contributor and a private admin listener when its ambient endpoint exists, then calls ResourceRuntime.HostBuilt. RunAsync delegates to the base host runner seam introduced by 3a62edab (design R6). Without registration, the ordinary explicit-service host remains unchanged and opens no listener.

Web.ControlPlane is installed first on the private listener. It serves the exact v1 resource routes and post-23b command envelopes. Readiness observes the owning area's HostState.Started; managed namespaced routes verify ES256 bootstrap tokens against ApplicationTrustKey. Standalone resources work without a gateway identity. A-record and CNAME handlers are registered for the advertised kinds; unsupported commands return 501 with a Rejected body. The DNS service stub remains dormant.

## Declarative commands (item 31c)

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `rezolvr.add-a-record` | `AddARecord` | record name |
| `rezolvr.add-cname-record` | `AddCnameRecord` | record name |

Kinds use verb-noun kebab under the area prefix. The examples `rezolvr.record` and
`identityhub.audience` in developer-experience design section 7 are illustrative; item 27's design
rewrite should reflect the landed convention. Manifest commands remain bare JSON strings.
Typed verbs validate argument shape and use source-generated JSON metadata. Build validates the
advertised kind, canonical payload, deterministic id, and uniqueness of the target ownership key.
The default control plane handles id replay and owner isolation; each area handler also accepts
an identical reapplication with a different id. Conflicts return named Rejected details.

A-record declarations use a BCL IPv4 IPAddress serialized as a string. CNAME declarations
carry a DNS target string; TTL is a positive integer in seconds, defaulting to 300. COHAM001 keeps
Dns assemblies outside the ApplicationModel dependency closure.

Hosting stores records atomically in `records.json` under `ResourceContext.GetMount("data",
Path.GetFullPath(Path.Combine(ContentRootPath, "data")))`. Without a data mount, storage therefore
lives in the content-root-derived data directory. No CohesionMount or CohesionWorkloadKind change
is made: GenericPlanner requires StatefulSet for a Volume while RezolvrPlanner requires Deployment.
The command registry survives restart and restores ownership before the listener starts.

Records are stored, not served as DNS answers. ResolverEndpointService remains parked. DNS serving
and reconciling a durable Volume with the Deployment contract are deferred area work.

## HTTPS endpoint certificate contract (31t)

The enabled resource's `admin` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged.
