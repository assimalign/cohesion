# MapperProfileGenerator

`MapperProfileGenerator` implements Roslyn's `IIncrementalGenerator` and is discovered by
the C# compiler through its `Generator` attribute. `Initialize` registers the syntax pipelines
for class-based profiles and inline profile registrations.

The generator is a build-time component. Applications use the ObjectMapping runtime API;
they do not instantiate this type. Direct Roslyn hosts can construct the generator and adapt it
with `AsSourceGenerator()` for a generator driver.

See [the design](../../../DESIGN.md) for supported input shapes and generated output.
