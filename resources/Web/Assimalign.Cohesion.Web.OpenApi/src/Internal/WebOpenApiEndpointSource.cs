using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.OpenApi.Integration;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Serialization;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// The Web adapter for the OpenApi integration contract: exposes an application's built route table as
/// the intermediate operation, schema, tag and security-scheme metadata the description provider turns
/// into a document.
/// </summary>
/// <remarks>
/// <para>
/// Everything comes from metadata already on the routes; nothing is discovered by reflection. A route is
/// described when Web.Api's source generator described it (<see cref="EndpointParameterMetadata"/>,
/// <see cref="EndpointResponseMetadata"/>) or the application did (tags, a summary, a description), and it
/// carries no <see cref="ExcludeFromDescriptionMetadata"/>. Raw middleware endpoints the application did
/// not describe stay out: nothing records their inputs or outputs.
/// </para>
/// <para>
/// One source is built per document build and targets one OpenAPI line, because the schemas it hands
/// through (<see cref="OpenApiSchemaGenerator"/>) are written for that line. Everything is computed in
/// the constructor; the properties only return the results.
/// </para>
/// </remarks>
internal sealed class WebOpenApiEndpointSource : IOpenApiEndpointSource
{
    private const string formMediaType = "application/x-www-form-urlencoded";
    private const string problemMediaType = "application/problem+json";

    private readonly IHttpContentSerializationFeature? _serialization;
    private readonly OpenApiSchemaGenerator _schemas;
    private readonly List<string> _declaredSchemes = [];
    private readonly string? _defaultAuthenticationScheme;

    /// <summary>
    /// Describes the routes of <paramref name="router"/> for one OpenAPI line.
    /// </summary>
    /// <param name="router">The application's built router.</param>
    /// <param name="options">The document options: declared security schemes and tags.</param>
    /// <param name="serialization">The application's content-serialization registry, or <see langword="null"/>.</param>
    /// <param name="defaultAuthenticationScheme">The application's default authenticate scheme, or <see langword="null"/>.</param>
    /// <param name="version">The OpenAPI line the schemas are written for.</param>
    /// <exception cref="InvalidOperationException">
    /// An endpoint cannot be described: it reads or returns a type no registered reader or writer covers,
    /// or the exporter rejects its serialization contract. The message names the endpoint.
    /// </exception>
    public WebOpenApiEndpointSource(
        IRouter router,
        OpenApiOptions options,
        IHttpContentSerializationFeature? serialization,
        string? defaultAuthenticationScheme,
        OpenApiSpecVersion version)
    {
        _serialization = serialization;
        _schemas = new OpenApiSchemaGenerator(serialization, version);
        _defaultAuthenticationScheme = defaultAuthenticationScheme;

        foreach (OpenApiSecuritySchemeMetadata scheme in options.SecuritySchemes)
        {
            _declaredSchemes.Add(scheme.Name);
        }

        List<OpenApiOperationMetadata> operations = [];
        HashSet<(string Path, OperationType Method)> described = [];
        List<string> usedTags = [];
        HashSet<string> seenTags = new(StringComparer.Ordinal);

        foreach (IRouterRoute route in router.Routes)
        {
            if (route.Pattern is not { } pattern || !IsDescribed(route.Metadata))
            {
                continue;
            }

            List<OperationType> methods = GetOperationTypes(route.Methods);

            if (methods.Count == 0)
            {
                continue; // a route that accepts any method names no operation
            }

            string path = OpenApiPathTemplate.Format(pattern);
            EndpointDescription endpoint;

            try
            {
                endpoint = Describe(route.Metadata, pattern);
            }
            catch (InvalidOperationException exception)
            {
                throw CreateEndpointException(methods, path, exception);
            }
            catch (NotSupportedException exception)
            {
                throw CreateEndpointException(methods, path, exception);
            }

            foreach (string tag in endpoint.Tags)
            {
                if (seenTags.Add(tag))
                {
                    usedTags.Add(tag);
                }
            }

            string? name = route.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName;

            foreach (OperationType method in methods)
            {
                // The first route registered for a path and method keeps it: OpenAPI has one operation each.
                if (!described.Add((path, method)))
                {
                    continue;
                }

                operations.Add(new OpenApiOperationMetadata
                {
                    Method = method,
                    Path = path,
                    OperationId = name is null || methods.Count == 1 ? name : name + "_" + method.ToString().ToLowerInvariant(),
                    Summary = endpoint.Summary,
                    Description = endpoint.Description,
                    Tags = endpoint.Tags,
                    Parameters = endpoint.Parameters,
                    RequestBody = endpoint.RequestBody,
                    Responses = endpoint.Responses,
                    Security = endpoint.Security
                });
            }
        }

        Operations = operations;
        Schemas = _schemas.GetComponents();
        Tags = CreateDocumentTags(options.Tags, usedTags);
        SecuritySchemes = options.SecuritySchemes;
    }

    /// <inheritdoc />
    public IReadOnlyList<OpenApiOperationMetadata> Operations { get; }

    /// <inheritdoc />
    public IReadOnlyList<OpenApiSchemaMetadata> Schemas { get; }

    /// <inheritdoc />
    public IReadOnlyList<OpenApiTagMetadata> Tags { get; }

    /// <inheritdoc />
    public IReadOnlyList<OpenApiSecuritySchemeMetadata> SecuritySchemes { get; }

    private static bool IsDescribed(IRouterRouteMetadataCollection metadata)
    {
        if (metadata.GetMetadata<ExcludeFromDescriptionMetadata>() is not null)
        {
            return false;
        }

        foreach (object item in metadata)
        {
            if (item is EndpointParameterMetadata
                or EndpointResponseMetadata
                or EndpointTagsMetadata
                or EndpointSummaryMetadata
                or EndpointDescriptionMetadata)
            {
                return true;
            }
        }

        return false;
    }

    private static List<OperationType> GetOperationTypes(IReadOnlyCollection<HttpMethod> methods)
    {
        List<OperationType> operations = [];

        foreach (HttpMethod method in methods)
        {
            OperationType? operation = method.Value.ToUpperInvariant() switch
            {
                "GET" => OperationType.Get,
                "PUT" => OperationType.Put,
                "POST" => OperationType.Post,
                "DELETE" => OperationType.Delete,
                "OPTIONS" => OperationType.Options,
                "HEAD" => OperationType.Head,
                "PATCH" => OperationType.Patch,
                "TRACE" => OperationType.Trace,
                "QUERY" => OperationType.Query,
                _ => null // CONNECT and extension methods have no fixed operation field
            };

            if (operation is { } known && !operations.Contains(known))
            {
                operations.Add(known);
            }
        }

        return operations;
    }

    private EndpointDescription Describe(IRouterRouteMetadataCollection metadata, RoutePattern pattern)
    {
        IReadOnlyList<EndpointParameterMetadata> inputs = metadata.GetOrderedMetadata<EndpointParameterMetadata>();
        List<OpenApiParameterMetadata> parameters = [];

        // Every template parameter is a path parameter, typed from the handler when it binds the value and
        // from the inline constraints otherwise (OpenAPI requires each templated name to be defined).
        foreach (RoutePatternParameterSegment templateParameter in pattern.Parameters)
        {
            pattern.Defaults.TryGetValue(templateParameter.Name, out object? defaultValue);

            parameters.Add(new OpenApiParameterMetadata
            {
                Name = templateParameter.Name,
                In = ParameterLocation.Path,
                Required = true,
                Schema = ClrSchemas.ForRouteParameter(templateParameter, FindRouteInput(inputs, templateParameter.Name)?.Type, defaultValue)
            });
        }

        List<EndpointParameterMetadata> formFields = [];
        EndpointParameterMetadata? body = null;
        bool bindsInput = false;

        foreach (EndpointParameterMetadata input in inputs)
        {
            switch (input.Source)
            {
                case EndpointParameterSource.Route:
                case EndpointParameterSource.RouteOrQuery when pattern.GetParameter(input.Name) is not null:
                    bindsInput = true; // described with the template above
                    break;
                case EndpointParameterSource.RouteOrQuery:
                case EndpointParameterSource.Query:
                    bindsInput = true;
                    parameters.Add(CreateParameter(input, ParameterLocation.Query));
                    break;
                case EndpointParameterSource.Header:
                    bindsInput = true;

                    // The OpenAPI Parameter Object ignores Accept, Content-Type and Authorization header parameters;
                    // content types and security requirements describe them.
                    if (!IsReservedHeader(input.Name))
                    {
                        parameters.Add(CreateParameter(input, ParameterLocation.Header));
                    }
                    break;
                case EndpointParameterSource.Form:
                    bindsInput = true;
                    formFields.Add(input);
                    break;
                case EndpointParameterSource.Body:
                    bindsInput = true;
                    body = input;
                    break;
                default:
                    // A source appended after this adapter was written (Web.Api says to treat it as
                    // undescribed rather than fail).
                    break;
            }
        }

        OpenApiRequestBodyMetadata? requestBody = body is not null
            ? DescribeBody(body)
            : formFields.Count > 0 ? DescribeForm(formFields) : null;

        return new EndpointDescription
        {
            Summary = metadata.GetMetadata<EndpointSummaryMetadata>()?.Summary,
            Description = metadata.GetMetadata<EndpointDescriptionMetadata>()?.Description,
            Tags = DescribeTags(metadata),
            Parameters = parameters,
            RequestBody = requestBody,
            Responses = DescribeResponses(metadata, bindsInput, body is not null),
            Security = DescribeSecurity(metadata)
        };
    }

    private static EndpointParameterMetadata? FindRouteInput(IReadOnlyList<EndpointParameterMetadata> inputs, string name)
    {
        foreach (EndpointParameterMetadata input in inputs)
        {
            if (input.Source is EndpointParameterSource.Route or EndpointParameterSource.RouteOrQuery
                && string.Equals(input.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return input;
            }
        }

        return null;
    }

    private static OpenApiParameterMetadata CreateParameter(EndpointParameterMetadata input, ParameterLocation location) => new()
    {
        Name = input.Name,
        In = location,
        Required = input.IsRequired,
        Schema = ClrSchemas.ForText(input.Type)
    };

    private static bool IsReservedHeader(string name)
        => string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase);

    private OpenApiRequestBodyMetadata DescribeBody(EndpointParameterMetadata body)
    {
        string contentType = GetReaderMediaType(body.Type)
            ?? throw new InvalidOperationException(
                $"The request body '{body.Name}' is a '{body.Type}', but no registered content reader can read it. " +
                $"Add the type to the application's JsonSerializerContext ([JsonSerializable(typeof({body.Type.Name}))]) passed to AddJsonSerialization.");

        return new OpenApiRequestBodyMetadata
        {
            ContentType = contentType,
            Required = body.IsRequired,
            Schema = DescribeValue(body.Type)
        };
    }

    private static OpenApiRequestBodyMetadata DescribeForm(List<EndpointParameterMetadata> fields)
    {
        OpenApiSchema schema = new() { Type = SchemaType.Object };

        foreach (EndpointParameterMetadata field in fields)
        {
            schema.Properties[field.Name] = ClrSchemas.ForText(field.Type);

            if (field.IsRequired)
            {
                schema.Required.Add(field.Name);
            }
        }

        return new OpenApiRequestBodyMetadata
        {
            ContentType = formMediaType,
            Required = schema.Required.Count > 0,
            Schema = schema
        };
    }

    private IReadOnlyList<OpenApiResponseMetadata> DescribeResponses(IRouterRouteMetadataCollection metadata, bool bindsInput, bool readsBody)
    {
        // Responses form a set keyed by status: the last item for a status wins (group items come first,
        // then the generated ones, then the endpoint's own chain).
        Dictionary<int, EndpointResponseMetadata> declared = new();

        foreach (EndpointResponseMetadata response in metadata.GetOrderedMetadata<EndpointResponseMetadata>())
        {
            declared[response.StatusCode.Value] = response;
        }

        SortedDictionary<int, OpenApiResponseMetadata> responses = new();
        bool negotiates = false;

        foreach (KeyValuePair<int, EndpointResponseMetadata> response in declared)
        {
            responses[response.Key] = DescribeResponse(response.Value);
            negotiates |= response.Value.Type is { } type && response.Value.ContentType is null && type != typeof(ProblemDetails);
        }

        // The outcomes the binding thunk answers on its own (Web.Api DESIGN, "Failure Semantics"), unless the
        // endpoint describes them itself.
        if (bindsInput)
        {
            responses.TryAdd(HttpStatusCode.BadRequest.Value, CreateProblemResponse(HttpStatusCode.BadRequest.Value));
        }

        if (readsBody)
        {
            responses.TryAdd(HttpStatusCode.UnsupportedMediaType.Value, CreateProblemResponse(HttpStatusCode.UnsupportedMediaType.Value));
        }

        if (negotiates)
        {
            responses.TryAdd(HttpStatusCode.NotAcceptable.Value, new OpenApiResponseMetadata
            {
                StatusCode = FormatStatus(HttpStatusCode.NotAcceptable.Value),
                Description = HttpReasonPhrases.Get(HttpStatusCode.NotAcceptable.Value)
            });
        }

        if (responses.Count == 0)
        {
            responses[HttpStatusCode.Ok.Value] = new OpenApiResponseMetadata
            {
                StatusCode = FormatStatus(HttpStatusCode.Ok.Value),
                Description = HttpReasonPhrases.Get(HttpStatusCode.Ok.Value)
            };
        }

        return [.. responses.Values];
    }

    private OpenApiResponseMetadata DescribeResponse(EndpointResponseMetadata response)
    {
        int status = response.StatusCode.Value;
        string statusCode = FormatStatus(status);
        string description = HttpReasonPhrases.Get(status);

        if (response.Type is not { } type)
        {
            return new OpenApiResponseMetadata
            {
                StatusCode = statusCode,
                Description = description,
                ContentType = response.ContentType?.ToString()
            };
        }

        string? contentType;

        if (response.ContentType is { } fixedType)
        {
            contentType = fixedType.ToString();
        }
        else if (type == typeof(ProblemDetails))
        {
            contentType = problemMediaType; // written by the problem-details writer, never negotiated
        }
        else
        {
            contentType = GetWriterMediaType(type)
                ?? throw new InvalidOperationException(
                    $"The endpoint returns a '{type}' for status {statusCode}, but no registered content writer can serialize it. " +
                    $"Add the type to the application's JsonSerializerContext ([JsonSerializable(typeof({type.Name}))]) passed to AddJsonSerialization.");
        }

        return new OpenApiResponseMetadata
        {
            StatusCode = statusCode,
            Description = description,
            ContentType = contentType,
            Schema = type == typeof(string) ? new OpenApiSchema { Type = SchemaType.String } : DescribeValue(type)
        };
    }

    private OpenApiResponseMetadata CreateProblemResponse(int status) => new()
    {
        StatusCode = FormatStatus(status),
        Description = HttpReasonPhrases.Get(status),
        ContentType = problemMediaType,
        Schema = _schemas.GetProblemDetailsSchema()
    };

    // The schema of a body value: the RFC 9457 component for problem details, otherwise the JSON
    // contract the application registered. A type only a non-JSON format covers has no schema.
    private OpenApiSchema? DescribeValue(Type type)
    {
        if (type == typeof(ProblemDetails))
        {
            return _schemas.GetProblemDetailsSchema();
        }

        return _schemas.TryGetJsonTypeInfo(type, out JsonTypeInfo? typeInfo) ? _schemas.GetSchema(typeInfo) : null;
    }

    // The media type a request body of the type is read as: the first registered reader (registration
    // order is the server's preference) that can read it.
    private string? GetReaderMediaType(Type type)
    {
        if (_serialization is null)
        {
            return null;
        }

        foreach (IHttpContentReader reader in _serialization.Readers)
        {
            if (reader.CanRead(type) && GetConcreteMediaType(reader.MediaTypes) is { } mediaType)
            {
                return mediaType;
            }
        }

        return null;
    }

    // The media type a negotiated value is written as by default: the first registered writer that can
    // write it, under its canonical media type.
    private string? GetWriterMediaType(Type type)
    {
        if (_serialization is null)
        {
            return null;
        }

        foreach (IHttpContentWriter writer in _serialization.Writers)
        {
            if (writer.CanWrite(type) && GetConcreteMediaType(writer.MediaTypes) is { } mediaType)
            {
                return mediaType;
            }
        }

        return null;
    }

    private static string? GetConcreteMediaType(IReadOnlyList<HttpMediaType> mediaTypes)
    {
        foreach (HttpMediaType mediaType in mediaTypes)
        {
            if (!mediaType.IsEmpty && !mediaType.HasWildcard)
            {
                return mediaType.ToString();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> DescribeTags(IRouterRouteMetadataCollection metadata)
    {
        List<string> tags = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (EndpointTagsMetadata item in metadata.GetOrderedMetadata<EndpointTagsMetadata>())
        {
            foreach (string tag in item.Tags)
            {
                if (seen.Add(tag))
                {
                    tags.Add(tag);
                }
            }
        }

        return tags;
    }

    // Mirrors UseAuthorization: every authorization item applies, and the most specific AllowAnonymous
    // clears the items declared before it. Each scheme the remaining items authenticate with is one
    // alternative requirement, when the document declares it.
    private IReadOnlyList<OpenApiSecurityRequirementMetadata> DescribeSecurity(IRouterRouteMetadataCollection metadata)
    {
        List<AuthorizationMetadata> requirements = [];

        foreach (AuthorizationMetadata item in metadata.GetOrderedMetadata<AuthorizationMetadata>())
        {
            if (item.AllowsAnonymous)
            {
                requirements.Clear();
            }
            else
            {
                requirements.Add(item);
            }
        }

        if (requirements.Count == 0 || _declaredSchemes.Count == 0)
        {
            return [];
        }

        List<string> schemes = [];

        foreach (AuthorizationMetadata item in requirements)
        {
            AddSchemes(schemes, item.AuthenticationSchemes);

            if (item.Policy is { } policy)
            {
                AddSchemes(schemes, policy.AuthenticationSchemes);
            }
        }

        if (schemes.Count == 0)
        {
            // The requirement evaluates context.User, which the default authenticate scheme establishes;
            // without one, any declared scheme may be the one the application authenticates with.
            if (_defaultAuthenticationScheme is { } defaultScheme)
            {
                schemes.Add(defaultScheme);
            }
            else
            {
                AddSchemes(schemes, _declaredSchemes);
            }
        }

        List<OpenApiSecurityRequirementMetadata> security = [];

        foreach (string scheme in schemes)
        {
            if (_declaredSchemes.Contains(scheme))
            {
                security.Add(new OpenApiSecurityRequirementMetadata { Scheme = scheme });
            }
        }

        return security;
    }

    private static void AddSchemes(List<string> schemes, IReadOnlyList<string> source)
    {
        foreach (string scheme in source)
        {
            if (!schemes.Contains(scheme))
            {
                schemes.Add(scheme);
            }
        }
    }

    private static IReadOnlyList<OpenApiTagMetadata> CreateDocumentTags(IReadOnlyList<OpenApiTagMetadata> declared, List<string> used)
    {
        List<OpenApiTagMetadata> tags = [.. declared];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (OpenApiTagMetadata tag in declared)
        {
            names.Add(tag.Name);
        }

        foreach (string tag in used)
        {
            if (names.Add(tag))
            {
                tags.Add(new OpenApiTagMetadata { Name = tag });
            }
        }

        return tags;
    }

    private static string FormatStatus(int status) => status.ToString(CultureInfo.InvariantCulture);

    private static InvalidOperationException CreateEndpointException(List<OperationType> methods, string path, Exception exception)
    {
        string verbs = string.Join(", ", methods).ToUpperInvariant();

        return new InvalidOperationException(
            $"The OpenAPI description of the endpoint {verbs} {path} could not be generated. {exception.Message}",
            exception);
    }

    /// <summary>
    /// The parts of an operation shared by every method a route accepts.
    /// </summary>
    private sealed class EndpointDescription
    {
        public string? Summary { get; init; }

        public string? Description { get; init; }

        public required IReadOnlyList<string> Tags { get; init; }

        public required IReadOnlyList<OpenApiParameterMetadata> Parameters { get; init; }

        public OpenApiRequestBodyMetadata? RequestBody { get; init; }

        public required IReadOnlyList<OpenApiResponseMetadata> Responses { get; init; }

        public required IReadOnlyList<OpenApiSecurityRequirementMetadata> Security { get; init; }
    }
}
