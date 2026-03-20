# Assimalign.Cohesion.Configuration.Binder Design

## Design Intent

The binder package sits directly on top of the configuration core and converts hierarchical data into concrete object graphs without forcing consumers to parse values manually.

## Architecture

- Extension methods like Get<T>, Bind, and GetValue<T> live directly on IConfiguration.
- Binding can either create new instances or populate existing ones.
- Because the binder is reflection-driven, trimming and AOT concerns are part of the design conversation for callers.

## Layout Example

```text
Assimalign.Cohesion.Configuration.Binder/
  src/
    Assimalign.Cohesion.Configuration.Binder.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Create typed options from configuration

```csharp
MyOptions options = configuration.Get<MyOptions>();
string endpoint = configuration.GetValue<string>("Api:Endpoint");
```

## Example 2: Bind into an existing instance

```csharp
var options = new MyOptions();

configuration.Bind("Api", options);
```
