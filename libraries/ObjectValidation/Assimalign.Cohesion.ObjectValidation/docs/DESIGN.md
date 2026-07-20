# Assimalign.Cohesion.ObjectValidation Design

## Design Intent

The library breaks validation into explicit layers so callers can author reusable profiles and then execute them through a single validator. That keeps rule definition and runtime validation separate.

## Architecture

- Validator coordinates profile execution and produces ValidationResult objects.
- ValidationProfile and descriptor types capture the fluent rule configuration model.
- ValidationOptions control execution behavior such as stop-on-first-failure and throw-on-failure.

## NativeAOT Posture

The library is NativeAOT-clean: no `System.Linq.Expressions.Expression.Compile` and no other
dynamic-code or reflection-emit path remains, so `IsAotCompatible=true` holds with no `IL2026`/
`IL3050` findings. Two seams were hardened without changing the authoring model:

- **Member access is compile-free.** `RuleFor`/`RuleForEach` still accept an
  `Expression<Func<T, TMember>>` member selector (the expression body doubles as the member name for
  the error `Source`), but the value is read by walking the expression's already-resolved
  `PropertyInfo`/`FieldInfo` metadata in `ValidationItemBase.GetValue` rather than by compiling a
  delegate. Walking resolved member metadata is reflection-only and AOT-safe, and the historical
  null-in-chain behavior (a null owner yields the member's default) is preserved by the surrounding
  try/catch.
- **Conditional predicates are delegate-first.** `When(...)` on `IValidationRuleDescriptor<T>` and
  `IValidationCondition<T>` now takes a `Func<T, bool>` rather than an `Expression<Func<T, bool>>`, so
  no predicate is compiled at run time. Inline lambda call sites (`When(p => p.Age >= 18, ...)`) bind
  to the delegate parameter unchanged; only a call site that first materialized an
  `Expression<Func<T, bool>>` variable is a (rare) source break.

## Layout Example

```text
Assimalign.Cohesion.ObjectValidation/
  src/
    Assimalign.Cohesion.ObjectValidation.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Internal/
    Properties/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Create a validator with a profile

```csharp
IValidator validator = Validator.Create(builder =>
{
    builder.AddProfile(new PersonValidationProfile());
});
```

## Example 2: Validate a model instance

```csharp
var person = new Person();
ValidationResult result = validator.Validate(person);
```
