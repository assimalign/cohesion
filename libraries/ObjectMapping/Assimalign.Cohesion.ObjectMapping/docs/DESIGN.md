# Assimalign.Cohesion.ObjectMapping Design

## Design Intent

The mapper favors **explicit profile configuration** over convention-heavy magic.
A profile records an ordered list of mapping *actions* ahead of time, and at run
time the mapper simply replays the actions that match the source/target pair
against a mapping context. There is no global static state and no ambient
configuration — a mapper is an immutable, reusable object built from options.

Two layers make up the public surface:

- A **low-level, target-first primitive**: `IMapper.Map(target, source, targetType, sourceType)`
  copies values *into* an existing target *from* a source. Target-first is
  deliberate — it is what makes multi-source composition (several sources merged
  into one target) natural.
- **Ergonomic extension methods** (`MapperExtensions`) layered on top:
  `Map<TTarget, TSource>(source)` (create + map), `Map<TTarget>(params object[])`
  (compose), `Map<TTarget, TSource>(IEnumerable<TSource>)` (project a sequence),
  and `Map<TTarget, TSource>(target, source)` (map onto an existing instance).

## Architecture

| Type | Responsibility |
| --- | --- |
| `IMapper` / `Mapper` | Holds options + profiles; applies matching profiles on `Map`. |
| `MapperOptions` | Name, `IgnoreHandling`, `CollectionHandling`, and the profile list. |
| `IMapperBuilder` / `MapperBuilder` | Collects profiles and builds a single `Mapper` (one-shot `Build`). |
| `IMapperFactoryBuilder` / `MapperFactoryBuilder` | Registers named mappers and builds an `IMapperFactory`. |
| `IMapperFactory` | Resolves a registered `IMapper` by name. |
| `MapperProfile<TTarget, TSource>` | Base class for a source→target mapping; `Configure` declares actions. |
| `MapperProfileDescriptor<TTarget, TSource>` | Fluent surface (`MapMember`, `MapMemberTypes`, `MapMemberEnumerables`, `MapAction`, `MapAll*`). |
| `IMapperAction` | A single recorded mapping step (`Invoke(context)`). |
| `IMapperContext` / `MapperContext` | Per-invocation state: source, target, profiles, handling flags. |
| `MapperException` | Area-scoped exception root (e.g. unknown mapper name). |

### The mapping actions (`Internal/Actions`)

- `MapperActionMember` — scalar member-to-member. **Hot path**: reads through a
  compiled `Func<TSource, TSourceMember>` getter and writes through a compiled
  `Action<TTarget, TSourceMember>` setter (no per-call reflection).
- `MapperActionMemberType` — a complex/reference member. Resolves the profile(s)
  registered for the member's `(TTargetMember, TSourceMember)` pair and runs them
  against a **child context** scoped to the member instances, then assigns the
  result. Reuses an existing target member instance if present.
- `MapperActionMemberEnumerable` — an enumerable member. Maps each source element
  through the matching element profile, then materializes into the concrete
  collection type declared by the target member (array, `List`/`IList`/`IEnumerable`,
  `HashSet`/`ISet`, `Queue`, `Stack`).
- `MapperAction` / `MapperAction<TTarget, TSource>` — user-supplied callbacks.

## Lifecycle

1. **Configure** — profiles declare their actions. For class-based profiles this
   happens in the `MapperProfile` constructor; for inline profiles
   (`AddProfile<TTarget, TSource>(configure)`) the delegate is passed through the
   base constructor so it is assigned *before* the virtual `Configure` runs. (This
   ordering matters — calling a virtual that reads a not-yet-assigned derived field
   from a base constructor was a defect that has been corrected.)
2. **Build** — `MapperBuilder.Build()` snapshots options into an immutable `Mapper`.
   It is one-shot and throws `InvalidOperationException` if called twice.
3. **Map** — `Mapper.Map` validates arguments, builds a `MapperContext`, and replays
   every profile whose `(targetType, sourceType)` matches. Mappers are thread-safe
   to *use* once built (no mutable state on the map path).

## Null / default and collection handling

`MapperIgnoreHandling` controls whether source nulls/defaults are written:

- `Never` (default) — always write, including null and default values.
- `Always` — never write a null source value; value-type defaults *are* written
  (a value type is never null).
- `WhenMappingDefaults` — write only values that are neither null nor the type
  default. (A null source value is skipped without throwing.)

`MapperCollectionHandling` controls enumerable members:

- `Override` (default) — replace the target collection with the mapped elements.
- `Merge` — keep the existing target elements, then append the mapped elements.

## Error model

- `ArgumentNullException` — null arguments to public APIs.
- `ArgumentException` — a target expression that is not a writable member of the
  declaring type (e.g. a method call or a chained member).
- `InvalidCastException` — a source member type that is not assignable to the
  target member type.
- `MapperException` — area-scoped root; thrown when a factory cannot resolve a
  mapper by name, and surfaced by the ergonomic extensions if a result cannot be
  produced.

## Performance posture

The scalar hot path (`MapMember`) avoids reflection entirely by compiling both the
getter and the setter once at profile-configuration time. Complex- and
enumerable-member writes (configured once per object/collection, not per scalar)
use `PropertyInfo`/`FieldInfo` reflection for simplicity; these are not the hot
path. Profiles are configured once and replayed many times.

## AOT posture

Mapping actions are built from two paths:

1. **Generated (AOT-safe) path.** Each scalar mapping is driven by a compiled
   getter `Func<TSource, TMember>` and setter `Action<TTarget, TMember>` supplied
   directly to `MapperActionMember`. No `Expression.Compile()` and no reflection
   run at map time. Profiles expose `TryConfigureGenerated`, which the base
   constructor prefers over the reflection-based `Configure`; the
   `Assimalign.Cohesion.SourceGeneration` incremental generator emits an override
   of it for eligible profiles (see below).
2. **Reflection fallback path.** The expression-based `MapMember(t => t.X, s => s.Y)`,
   `MapMemberTypes`, and `MapMemberEnumerables` overloads compile expression trees,
   and the convention helpers (`MapMember(string, string)`, `MapAll*`) use
   reflection. These are annotated `[RequiresDynamicCode]` /
   `[RequiresUnreferencedCode]` so callers are warned under trim/AOT analysis. They
   exist so any profile keeps working even when the generator does not cover it.

### Source generator (`analyzers/Core/Assimalign.Cohesion.SourceGeneration`)

The incremental generator recognizes top-level, non-generic, `partial`,
class-based `MapperProfile<TTarget, TSource>` definitions whose `Configure` body is
a single fluent chain of `MapMember`, `MapMemberTypes`, and `MapMemberEnumerables`
calls. For each, it emits a partial `TryConfigureGenerated` override that re-registers
the mappings through the delegate-based overloads:

- `MapMember(t => t.X, s => …)` → `MapMember(<source lambda verbatim>, static (target, value) => target.X = value)`.
- `MapMemberTypes(t => t.X, s => …)` → `MapMemberTypes(<source>, <target>, static (target, value) => target.X = value)`.
- `MapMemberEnumerables(t => t.X, s => …)` → `MapMemberEnumerables(<source>, <target>, static (target, items) => target.X = <conversion>)`,
  where `<conversion>` is chosen at compile time from the target member's declared
  collection type (`ToList`, `ToArray`, `new HashSet<…>`, `new Queue<…>`, `new Stack<…>`).

The source lambda is reused verbatim (so any expression — e.g. `s => s.Age.GetValueOrDefault()` —
works), a trailing null-forgiving `!` on the target member is unwrapped, and the setter is
synthesized. The result is a profile whose runtime mapping never touches `Expression.Compile()`.

A scalar `MapMember` is only generated when the source value is assignable to the target member by
identity / reference / boxing (matching the runtime `IsAssignableTo` rule). This both guarantees the
emitted `target.X = value` compiles and keeps generated behavior identical to the fallback. A
two-parameter lambda in the second argument is recognized as the delegate-based overload
(`MapMember(getter, setter)`), which is already AOT-safe and left untouched.

To keep the generated type clean under trim/AOT analysis, the generator stamps each covered
class-based profile's partial with type-level `[UnconditionalSuppressMessage]` for `IL2026`/`IL3050`.
The expression-based `Configure` is statically present but never executed for a generated profile, so
suppressing it on the generated type is sound.

### Inline `AddProfile<TTarget, TSource>(lambda)` (interceptors)

The same fluent chain written inline — `builder.AddProfile<T, S>(d => d.MapMember(…)…)` — is also
made AOT-safe, via C# **interceptors**. For each eligible call site the generator (resolving the
location through `SemanticModel.GetInterceptableLocation`) emits:

- a generated `file`-scoped delegate-based profile class,
- an `[InterceptsLocation]` interceptor that redirects the call to
  `builder.AddProfile(new <generated profile>())` and never invokes the user lambda (so its
  `Expression.Compile()` never runs), and
- an assembly-level `[UnconditionalSuppressMessage(Scope = "member", Target = "<containing member>")]`
  for `IL2026`/`IL3050`, because the inline lambda — which still compiles into the user's method — is
  statically reachable to the analyzer even though the interceptor never runs it. (A `file`-scoped
  `InterceptsLocationAttribute` is emitted to avoid collisions with other generators.)

The member-scoped suppression is necessarily coarser than the class-based, type-scoped one: it
silences `IL2026`/`IL3050` for the whole member that contains the intercepted call. A member that
mixes an intercepted inline profile with a genuinely dynamic call (`MapAll*`, etc.) would have the
latter's warning hidden too.

Consumers that use inline profiles must opt their project into the interceptor namespace:

```xml
<InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.ObjectMapping.Generated</InterceptorsNamespaces>
```

The `samples/Assimalign.Cohesion.ObjectMapping.AotSample` project is the AOT guard for both paths: it
builds with `IsAotCompatible=true` and `IL2026`/`IL3050` promoted to errors, exercises class-based and
inline profiles, and so fails the build if the generator or either suppression regresses.

### Known limitations / next steps

- Generation is gated to a straight fluent chain. Profiles with loops, conditionals, locals, the
  string-based `MapMember`, `MapAll*`, or custom `MapAction` fall back to the reflection path (with the
  honest `[RequiresDynamicCode]` warning under AOT analysis).
- Inline interception depends on the preview interceptors feature and the per-project
  `InterceptorsNamespaces` opt-in above. Shipping a `.props` in the library package that sets it
  automatically for consumers is a packaging follow-up (the in-repo test and AOT-guard projects set
  it explicitly today).
- The AOT guard is not yet wired into CI / the solution; building it is currently a manual
  (or to-be-added pipeline) step.

## Non-goals

- **No implicit numeric/`Nullable<T>` conversions.** Member assignability is
  checked with `Type.IsAssignableTo`, which does not consider implicit conversions
  (e.g. `int` → `int?` or `int` → `long`). Supply a conversion in the source
  expression instead: `source => (int?)source.Age`.
- **No flattening/unflattening conventions** (e.g. `Customer.Name` → `CustomerName`)
  beyond the explicit dotted-path form of `MapMember(string, string)`.
- **Targets must be reference types.** Mapping mutates the target in place; struct
  targets would be mutated on a copy.
- **No async mapping.** Mapping is synchronous and CPU-bound.

## Layout

```text
Assimalign.Cohesion.ObjectMapping/
  src/
    Abstractions/      IMapper, IMapperFactory, IMapperProfile, IMapperAction,
                       IMapperContext, IMapperBuilder, IMapperFactoryBuilder
    Exceptions/        MapperException
    Extensions/        MapperExtensions
    Internal/          Mapper actions, DefaultMapperProfile, MapperUtility
    ValueTypes/        MapperIgnoreHandling, MapperCollectionHandling
    Mapper, MapperBuilder, MapperFactoryBuilder, MapperContext,
    MapperOptions, MapperProfile, MapperProfileDescriptor
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example: explicit profile + nested members + collections

```csharp
IMapper mapper = new MapperBuilder()
    .AddProfile<OrderTarget, OrderSource>(profile => profile
        .MapMember(target => target.Id, source => source.Id)
        .MapMemberTypes(target => target.Customer!, source => source.Customer!)
        .MapMemberEnumerables(target => target.Items!, source => source.Items!))
    .AddProfile<CustomerTarget, CustomerSource>(profile => profile
        .MapMember(target => target.Name, source => source.Name))
    .AddProfile<LineItemTarget, LineItemSource>(profile => profile
        .MapMember(target => target.Sku, source => source.Sku)
        .MapMember(target => target.Quantity, source => source.Quantity))
    .Build();

OrderTarget order = mapper.Map<OrderTarget, OrderSource>(source);
```

## Example: compose multiple sources into one target

```csharp
IMapper mapper = new MapperBuilder()
    .AddProfile<PersonTarget, NameSource>(p => p
        .MapMember(t => t.FirstName, s => s.FirstName)
        .MapMember(t => t.LastName, s => s.LastName))
    .AddProfile<PersonTarget, AgeSource>(p => p
        .MapMember(t => t.Age, s => s.Age))
    .Build();

PersonTarget person = mapper.Map<PersonTarget>(nameSource, ageSource);
```
