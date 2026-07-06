using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// Enumerates every <see cref="OpenApiSchema"/> reachable in a document together with a JSON Pointer to
/// it. It visits every schema-bearing location — component maps (schemas, parameters, headers, request
/// bodies, responses, media types, callbacks, path items), path and webhook operations (including
/// path-item-level and content-map parameters and additional operations), response and encoding headers,
/// streaming <c>itemSchema</c>s, and callbacks — then recurses through composition, properties, items,
/// and the JSON Schema 2020-12 subschema keywords. Reference-equality visited sets guard against cycles.
/// </summary>
internal static class OpenApiSchemaWalker
{
    internal static IReadOnlyList<(OpenApiSchema Schema, string Pointer)> Walk(OpenApiDocument document)
    {
        var context = new WalkContext();

        if (document.Components is { } components)
        {
            foreach (var pair in components.Schemas)
            {
                context.Schema(pair.Value, Pointer.Of("components", "schemas", pair.Key));
            }

            foreach (var pair in components.Parameters)
            {
                context.Parameter(pair.Value, Pointer.Of("components", "parameters", pair.Key));
            }

            foreach (var pair in components.Headers)
            {
                context.Header(pair.Value, Pointer.Of("components", "headers", pair.Key));
            }

            foreach (var pair in components.RequestBodies)
            {
                context.RequestBody(pair.Value, Pointer.Of("components", "requestBodies", pair.Key));
            }

            foreach (var pair in components.Responses)
            {
                context.Response(pair.Value, Pointer.Of("components", "responses", pair.Key));
            }

            foreach (var pair in components.MediaTypes)
            {
                context.MediaType(pair.Value, Pointer.Of("components", "mediaTypes", pair.Key));
            }

            foreach (var pair in components.Callbacks)
            {
                context.Callback(pair.Value, Pointer.Of("components", "callbacks", pair.Key));
            }

            foreach (var pair in components.PathItems)
            {
                context.PathItem(pair.Value, Pointer.Of("components", "pathItems", pair.Key));
            }
        }

        if (document.Paths is { } paths)
        {
            foreach (var pair in paths.Items)
            {
                context.PathItem(pair.Value, Pointer.Of("paths", pair.Key));
            }
        }

        foreach (var pair in document.Webhooks)
        {
            context.PathItem(pair.Value, Pointer.Of("webhooks", pair.Key));
        }

        return context.Results;
    }

    private sealed class WalkContext
    {
        private readonly HashSet<OpenApiSchema> _visitedSchemas = new(ReferenceComparer<OpenApiSchema>.Instance);
        private readonly HashSet<OpenApiPathItem> _visitedPathItems = new(ReferenceComparer<OpenApiPathItem>.Instance);

        internal List<(OpenApiSchema Schema, string Pointer)> Results { get; } = [];

        internal void PathItem(OpenApiPathItem item, string pointer)
        {
            if (item.Reference is not null || !_visitedPathItems.Add(item))
            {
                return;
            }

            foreach (var parameter in item.Parameters)
            {
                Parameter(parameter, Pointer.Append(pointer, "parameters"));
            }

            foreach (var operation in item.Operations)
            {
                Operation(operation.Value, Pointer.Append(pointer, operation.Key.ToString()));
            }

            foreach (var operation in item.AdditionalOperations)
            {
                Operation(operation.Value, Pointer.Append(pointer, "additionalOperations", operation.Key));
            }
        }

        private void Operation(OpenApiOperation operation, string pointer)
        {
            for (var index = 0; index < operation.Parameters.Count; index++)
            {
                Parameter(operation.Parameters[index], Pointer.Append(pointer, "parameters", index.ToString()));
            }

            if (operation.RequestBody is not null)
            {
                RequestBody(operation.RequestBody, Pointer.Append(pointer, "requestBody"));
            }

            if (operation.Responses is not null)
            {
                foreach (var response in operation.Responses.Items)
                {
                    Response(response.Value, Pointer.Append(pointer, "responses", response.Key));
                }
            }

            foreach (var callback in operation.Callbacks)
            {
                Callback(callback.Value, Pointer.Append(pointer, "callbacks", callback.Key));
            }
        }

        internal void Callback(OpenApiCallback callback, string pointer)
        {
            foreach (var pair in callback.PathItems)
            {
                PathItem(pair.Value, Pointer.Append(pointer, pair.Key));
            }
        }

        internal void Parameter(OpenApiParameter parameter, string pointer)
        {
            if (parameter.Schema is not null)
            {
                Schema(parameter.Schema, Pointer.Append(pointer, "schema"));
            }

            Content(parameter.Content, pointer);
        }

        internal void Header(OpenApiHeader header, string pointer)
        {
            if (header.Schema is not null)
            {
                Schema(header.Schema, Pointer.Append(pointer, "schema"));
            }

            Content(header.Content, pointer);
        }

        internal void RequestBody(OpenApiRequestBody body, string pointer) => Content(body.Content, pointer);

        internal void Response(OpenApiResponse response, string pointer)
        {
            foreach (var header in response.Headers)
            {
                Header(header.Value, Pointer.Append(pointer, "headers", header.Key));
            }

            Content(response.Content, pointer);
        }

        private void Content(IDictionary<string, OpenApiMediaType> content, string pointer)
        {
            foreach (var pair in content)
            {
                MediaType(pair.Value, Pointer.Append(pointer, "content", pair.Key));
            }
        }

        internal void MediaType(OpenApiMediaType media, string pointer)
        {
            if (media.Schema is not null)
            {
                Schema(media.Schema, Pointer.Append(pointer, "schema"));
            }

            if (media.ItemSchema is not null)
            {
                Schema(media.ItemSchema, Pointer.Append(pointer, "itemSchema"));
            }

            foreach (var encoding in media.Encoding)
            {
                foreach (var header in encoding.Value.Headers)
                {
                    Header(header.Value, Pointer.Append(pointer, "encoding", encoding.Key, "headers", header.Key));
                }
            }
        }

        internal void Schema(OpenApiSchema schema, string pointer)
        {
            if (!_visitedSchemas.Add(schema))
            {
                return;
            }

            Results.Add((schema, pointer));

            foreach (var pair in schema.Properties)
            {
                Schema(pair.Value, Pointer.Append(pointer, "properties", pair.Key));
            }

            foreach (var pair in schema.PatternProperties)
            {
                Schema(pair.Value, Pointer.Append(pointer, "patternProperties", pair.Key));
            }

            foreach (var pair in schema.Defs)
            {
                Schema(pair.Value, Pointer.Append(pointer, "$defs", pair.Key));
            }

            foreach (var pair in schema.DependentSchemas)
            {
                Schema(pair.Value, Pointer.Append(pointer, "dependentSchemas", pair.Key));
            }

            SchemaList(schema.AllOf, Pointer.Append(pointer, "allOf"));
            SchemaList(schema.AnyOf, Pointer.Append(pointer, "anyOf"));
            SchemaList(schema.OneOf, Pointer.Append(pointer, "oneOf"));
            SchemaList(schema.PrefixItems, Pointer.Append(pointer, "prefixItems"));

            SchemaChild(schema.Items, Pointer.Append(pointer, "items"));
            SchemaChild(schema.Not, Pointer.Append(pointer, "not"));
            SchemaChild(schema.AdditionalProperties, Pointer.Append(pointer, "additionalProperties"));
            SchemaChild(schema.PropertyNames, Pointer.Append(pointer, "propertyNames"));
            SchemaChild(schema.Contains, Pointer.Append(pointer, "contains"));
            SchemaChild(schema.UnevaluatedItems, Pointer.Append(pointer, "unevaluatedItems"));
            SchemaChild(schema.UnevaluatedProperties, Pointer.Append(pointer, "unevaluatedProperties"));
            SchemaChild(schema.If, Pointer.Append(pointer, "if"));
            SchemaChild(schema.Then, Pointer.Append(pointer, "then"));
            SchemaChild(schema.Else, Pointer.Append(pointer, "else"));
            SchemaChild(schema.ContentSchema, Pointer.Append(pointer, "contentSchema"));
        }

        private void SchemaChild(OpenApiSchema? schema, string pointer)
        {
            if (schema is not null)
            {
                Schema(schema, pointer);
            }
        }

        private void SchemaList(IList<OpenApiSchema> schemas, string pointer)
        {
            for (var index = 0; index < schemas.Count; index++)
            {
                Schema(schemas[index], Pointer.Append(pointer, index.ToString()));
            }
        }
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
