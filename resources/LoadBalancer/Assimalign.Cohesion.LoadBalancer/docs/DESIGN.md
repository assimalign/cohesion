# Assimalign.Cohesion.LoadBalancer Design

## Design intent

The area root owns only the contracts that feature packages compose against. `ILoadBalancerApplicationBuilder` is the contract-only builder seam, while `ILoadBalancerApplication` supplies the host lifecycle expected by an executable resource.

## Hosting isolation

The root references the shared Hosting foundation while preserving its existing HTTP foundation dependency; it does not reference `Assimalign.Cohesion.LoadBalancer.Hosting`. The concrete builder, host, context, and options remain internal to that runtime module, and feature libraries must not reference it.

## Filler lifecycle

The current implementation registers no hosted services and always uses the production host environment. It exists only to complete the SDK/framework path until load-balancing behavior is implemented.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
