# OpenApi attribute authoring guidance

This guide covers how to annotate application code with the OpenApi attribute set and how those
attributes map into generation metadata. The mapping rules here are the contract the source generator
(feature .06) implements; authoring to them keeps runtime mapping and compile-time generation in
agreement.

## Describing an operation

Annotate a method with `[OpenApiOperation(method, path)]` and attach the operation's parameters,
request body, responses, and security:

```csharp
[OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet", Summary = "Get a pet", Tags = new[] { "pets" })]
[OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
[OpenApiResponse(200, Description = "The pet", ContentType = "application/json", ModelType = typeof(Pet))]
[OpenApiResponse(404, Description = "Not found")]
public static void GetPet() { }
```

- **Path parameters** should set `Required = true`. If you omit it, the mapper corrects it and emits a
  warning (`OPENAPIATTR0003`) — the correction is a convenience, not a substitute for authoring intent.
- **Response and request-body schemas** are named either by `ModelType = typeof(T)` (resolved to
  `#/components/schemas/T`) **or** by an explicit `SchemaReference`. Setting both is an error
  (`OPENAPIATTR0002`); prefer `ModelType` when a CLR model exists.
- The scalar `SchemaType` on a parameter or property uses `OpenApiSchemaKind` (not the model's
  `SchemaType`, because a nullable enum cannot be an attribute argument). Leave it `Unspecified` for a
  referenced or complex schema.

## Describing a schema

Annotate the model type with `[OpenApiSchema]` and its members with `[OpenApiSchemaProperty]`:

```csharp
[OpenApiSchema(Description = "A pet in the store.")]
public sealed class Pet
{
    [OpenApiSchemaProperty(Required = true, SchemaType = OpenApiSchemaKind.Integer, Format = "int64")]
    public long Id { get; set; }

    [OpenApiSchemaProperty(Name = "tag", SchemaType = OpenApiSchemaKind.String, Nullable = true)]
    public string? Category { get; set; }
}
```

The component name defaults to the type name (override with `Name`); property names default to the
member name (override with `Name`).

## Tags, security, and examples

- `[OpenApiTag("pets", Description = "…")]` on an assembly or class registers a document tag. The
  `Summary`, `Parent`, and `Kind` fields describe 3.2 tag metadata; generation ignores them when
  targeting an earlier line.
- `[OpenApiSecurityScheme("api_key", SecuritySchemeType.ApiKey) { ParameterName = "X-Key", In = ParameterLocation.Header }]`
  registers a reusable scheme. An API key scheme must set both `ParameterName` and `In`
  (`OPENAPIATTR0006`). `[OpenApiSecurityRequirement("api_key")]` on a class or method references it.
- `[OpenApiExample("sample", Value = "{ \"name\": \"Rex\" }")]` attaches a named example. Set exactly
  one of `Value` or `ExternalValue` — both is an error (`OPENAPIATTR0004`), neither is a warning
  (`OPENAPIATTR0005`).

## Version-targeted behavior

The attributes themselves are version-neutral; they describe intent. **Version targeting happens at
generation time** — the generation pipeline (feature .06) is told which OpenAPI line to emit and drops
or adapts version-gated metadata (3.2 tag fields, 3.1 schema keywords, …) using the same capability
matrix the serializer and validator use. Author the full intent; the generator emits what the target
line supports. This keeps a single annotated codebase able to produce 3.0, 3.1, and 3.2 descriptions.

## Known restrictions

- Example values are serialized strings (a compile-time constant), not live objects.
- A body schema is named by type *name*, not by structural analysis of the type — pair a `ModelType`
  response with an `[OpenApiSchema]`-annotated model of the same name to produce the component.
- Generic model types resolve by their raw `Type.Name` (including the arity marker); prefer an explicit
  `SchemaReference` for generic payloads.
