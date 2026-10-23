---
paths:
  - "libraries/**/*.csproj"
  - "resources/**/*.csproj"
  - "**/ComponentIntegrations.cs"
  - "libraries/Core/**/ComponentIntegration/**"
  - "analyzers/Assimalign.Cohesion.SourceGeneration/**"
  - "build/IntegrationCheck/**"
---

# Component integration

Owner request, 2026-10-22: integration declarations are internal, opt-in shared source, with a closed
seam/method catalog, an explicit template per declaration, and optional custom signatures and bodies.
The authoritative contract, examples, placeholders, lifecycle, and diagnostics are documented in
[the generator design](../../analyzers/Assimalign.Cohesion.SourceGeneration/docs/DESIGN.md).

## Adding an integration

1. The contributing csproj explicitly includes
   `<CohesionSharedSource Include="Assimalign.Cohesion.Core" Module="ComponentIntegration" />`.
   Core's shipped assembly does not expose this attribute. Do not add a DI reference to a feature
   library or create a glue project to obtain it.
2. Declare assembly attributes in `src/Properties/ComponentIntegrations.cs`. Use
   `ComponentIntegrationTarget`, `ComponentIntegrationMethod`, and `ComponentIntegrationTemplate`;
   arbitrary seam/member strings are not the contract.
3. Declare the `Builder` and `BuilderWithServiceProvider` overloads separately under the same feature
   verb. The first emits `Action<TBuilder>? configure = null`; the second emits
   `Action<IServiceProvider, TBuilder> configure`. No implicit overload or lifetime variants are added.
4. A builder's public instance build method is preferred over a public static factory facade created
   only for integration. `Factory` remains available where required arguments need a static factory.
5. A `Custom` declaration supplies `Signature` and a complete `Body` block. It owns the return,
   registration, validation, and disposal semantics. Use documented placeholders and qualified type
   names. The generator checks syntax; consuming compilation checks binding. Adding a custom body
   does not grant the contributor a runtime reference to any seam library.
6. Reference both sides in IntegrationCheck and call every generated overload. Preserve workflow
   triggers for contributing projects. Tests must cover behavior, not just emitted text.

## Lifetime and boundaries

The ordinary builder callback builds eagerly and reuses one component; target `AddSingleton`.
The provider callback builds inside the sink producer and follows its lifetime. Web features remain
explicitly singleton because Web.Hosting enforces that contract. Disposable static products use a
producer; instance registrations can bypass container disposal capture (COHCMP0007).

The supported catalog currently names IServiceProviderBuilder and AddSingleton/AddScoped/AddTransient.
A new target is a deliberate shared-catalog change with tests and a canary, not an unchecked string.
Feature names remain AddHttpClientFactory, AddRouting, and so on. Shared-source types keep their
owning namespace and remain internal in each opted-in assembly; no shipped InternalsVisibleTo grant
is required. The generator is build tooling and generated code must preserve NativeAOT compatibility.

Projected containers are internal to each consumer and cannot re-export verbs through a library.
Consumers require C# 14 and .NET 10. Schema version 2 intentionally replaces the former public
string-based declaration; update package versions coherently.
