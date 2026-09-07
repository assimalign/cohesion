# Assimalign.Cohesion.LogSpace Design

## Design intent

The area root owns only the contracts that feature packages compose against. `ILogSpaceApplicationBuilder` is the contract-only builder seam, while `ILogSpaceApplication` supplies the host lifecycle expected by an executable resource.

## Hosting isolation

The root references only the shared Hosting foundation. The concrete builder, host, context, and options remain internal to `Assimalign.Cohesion.LogSpace.Hosting`; feature libraries must not reference that runtime module.

## Filler lifecycle

The current implementation registers no hosted services and always uses the production host environment. It exists only to complete the SDK/framework path until log-storage behavior is implemented.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
