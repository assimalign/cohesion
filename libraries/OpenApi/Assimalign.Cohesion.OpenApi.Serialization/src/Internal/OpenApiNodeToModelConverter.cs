using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Maps a parsed <see cref="OpenApiNode"/> tree back into the canonical <see cref="OpenApiDocument"/>
/// graph. The reader detects the version-normalized forms (nullability expressed as a <c>nullable</c>
/// keyword or a <c>"null"</c> type entry, and exclusive bounds expressed as a boolean flag or a numeric
/// value) by inspecting node shape, so a document round-trips regardless of which line emitted it.
/// </summary>
internal static class OpenApiNodeToModelConverter
{
    internal static OpenApiDocument Convert(OpenApiObjectNode root)
    {
        var document = new OpenApiDocument();

        if (OpenApiVersionCapabilities.TryParseVersion(GetString(root, "openapi"), out var version))
        {
            document.SpecVersion = version;
        }

        document.Self = GetString(root, "$self");

        if (AsObject(Get(root, "info")) is { } infoNode)
        {
            document.Info = ReadInfo(infoNode);
        }

        document.JsonSchemaDialect = GetString(root, "jsonSchemaDialect");

        foreach (var server in ReadList(Get(root, "servers"), ReadServer))
        {
            document.Servers.Add(server);
        }

        if (AsObject(Get(root, "paths")) is { } pathsNode)
        {
            document.Paths = ReadPaths(pathsNode);
        }

        if (AsObject(Get(root, "webhooks")) is { } webhooksNode)
        {
            ReadObjectMap(webhooksNode, ReadPathItem, document.Webhooks);
        }

        if (AsObject(Get(root, "components")) is { } componentsNode)
        {
            document.Components = ReadComponents(componentsNode);
        }

        foreach (var requirement in ReadList(Get(root, "security"), ReadSecurityRequirement))
        {
            document.Security.Add(requirement);
        }

        foreach (var tag in ReadList(Get(root, "tags"), ReadTag))
        {
            document.Tags.Add(tag);
        }

        if (AsObject(Get(root, "externalDocs")) is { } externalDocsNode)
        {
            document.ExternalDocs = ReadExternalDocs(externalDocsNode);
        }

        ReadExtensions(root, document.Extensions);
        return document;
    }

    private static OpenApiInfo ReadInfo(OpenApiObjectNode node)
    {
        var info = new OpenApiInfo
        {
            Title = GetString(node, "title") ?? string.Empty,
            Summary = GetString(node, "summary"),
            Description = GetString(node, "description"),
            TermsOfService = GetString(node, "termsOfService"),
            Version = GetString(node, "version") ?? string.Empty
        };

        if (AsObject(Get(node, "contact")) is { } contactNode)
        {
            info.Contact = ReadContact(contactNode);
        }

        if (AsObject(Get(node, "license")) is { } licenseNode)
        {
            info.License = ReadLicense(licenseNode);
        }

        ReadExtensions(node, info.Extensions);
        return info;
    }

    private static OpenApiContact ReadContact(OpenApiObjectNode node)
    {
        var contact = new OpenApiContact
        {
            Name = GetString(node, "name"),
            Url = GetString(node, "url"),
            Email = GetString(node, "email")
        };

        ReadExtensions(node, contact.Extensions);
        return contact;
    }

    private static OpenApiLicense ReadLicense(OpenApiObjectNode node)
    {
        var license = new OpenApiLicense
        {
            Name = GetString(node, "name") ?? string.Empty,
            Identifier = GetString(node, "identifier"),
            Url = GetString(node, "url")
        };

        ReadExtensions(node, license.Extensions);
        return license;
    }

    private static OpenApiServer ReadServer(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var server = new OpenApiServer
        {
            Url = GetString(node, "url") ?? string.Empty,
            Description = GetString(node, "description")
        };

        if (AsObject(Get(node, "variables")) is { } variablesNode)
        {
            ReadObjectMap(variablesNode, ReadServerVariable, server.Variables);
        }

        ReadExtensions(node, server.Extensions);
        return server;
    }

    private static OpenApiServerVariable ReadServerVariable(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var variable = new OpenApiServerVariable
        {
            Default = GetString(node, "default") ?? string.Empty,
            Description = GetString(node, "description")
        };

        foreach (var item in ReadList(Get(node, "enum"), n => ((OpenApiValueNode)n).GetString()))
        {
            variable.Enum.Add(item);
        }

        ReadExtensions(node, variable.Extensions);
        return variable;
    }

    private static OpenApiPaths ReadPaths(OpenApiObjectNode node)
    {
        var paths = new OpenApiPaths();
        foreach (var member in node)
        {
            if (IsExtension(member.Key))
            {
                paths.Extensions[member.Key] = member.Value;
            }
            else if (member.Value is OpenApiObjectNode itemNode)
            {
                paths.Items[member.Key] = ReadPathItem(itemNode);
            }
        }

        return paths;
    }

    private static OpenApiPathItem ReadPathItem(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var item = new OpenApiPathItem();

        if (TryReadReference(node, out var reference))
        {
            item.Reference = reference;
            return item;
        }

        item.Summary = GetString(node, "summary");
        item.Description = GetString(node, "description");

        foreach (var (key, type) in OperationKeys)
        {
            if (AsObject(Get(node, key)) is { } operationNode)
            {
                item.Operations[type] = ReadOperation(operationNode);
            }
        }

        if (AsObject(Get(node, "additionalOperations")) is { } additionalNode)
        {
            ReadObjectMap(additionalNode, n => ReadOperation((OpenApiObjectNode)n), item.AdditionalOperations);
        }

        foreach (var server in ReadList(Get(node, "servers"), ReadServer))
        {
            item.Servers.Add(server);
        }

        foreach (var parameter in ReadList(Get(node, "parameters"), ReadParameter))
        {
            item.Parameters.Add(parameter);
        }

        ReadExtensions(node, item.Extensions);
        return item;
    }

    private static OpenApiOperation ReadOperation(OpenApiObjectNode node)
    {
        var operation = new OpenApiOperation
        {
            Summary = GetString(node, "summary"),
            Description = GetString(node, "description"),
            OperationId = GetString(node, "operationId"),
            Deprecated = GetBool(node, "deprecated")
        };

        foreach (var tag in ReadList(Get(node, "tags"), n => ((OpenApiValueNode)n).GetString()))
        {
            operation.Tags.Add(tag);
        }

        if (AsObject(Get(node, "externalDocs")) is { } externalDocsNode)
        {
            operation.ExternalDocs = ReadExternalDocs(externalDocsNode);
        }

        foreach (var parameter in ReadList(Get(node, "parameters"), ReadParameter))
        {
            operation.Parameters.Add(parameter);
        }

        if (AsObject(Get(node, "requestBody")) is { } requestBodyNode)
        {
            operation.RequestBody = ReadRequestBody(requestBodyNode);
        }

        if (AsObject(Get(node, "responses")) is { } responsesNode)
        {
            operation.Responses = ReadResponses(responsesNode);
        }

        if (AsObject(Get(node, "callbacks")) is { } callbacksNode)
        {
            ReadObjectMap(callbacksNode, n => ReadCallback((OpenApiObjectNode)n), operation.Callbacks);
        }

        foreach (var requirement in ReadList(Get(node, "security"), ReadSecurityRequirement))
        {
            operation.Security.Add(requirement);
        }

        foreach (var server in ReadList(Get(node, "servers"), ReadServer))
        {
            operation.Servers.Add(server);
        }

        ReadExtensions(node, operation.Extensions);
        return operation;
    }

    private static OpenApiParameter ReadParameter(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var parameter = new OpenApiParameter();

        if (TryReadReference(node, out var reference))
        {
            parameter.Reference = reference;
            return parameter;
        }

        parameter.Name = GetString(node, "name") ?? string.Empty;
        parameter.In = ParseParameterLocation(GetString(node, "in"));
        parameter.Description = GetString(node, "description");
        parameter.Required = GetBool(node, "required");
        parameter.Deprecated = GetBool(node, "deprecated");
        parameter.AllowEmptyValue = GetBool(node, "allowEmptyValue");
        parameter.Style = ParseParameterStyle(GetString(node, "style"));
        parameter.Explode = GetBoolNullable(node, "explode");
        parameter.AllowReserved = GetBool(node, "allowReserved");

        if (AsObject(Get(node, "schema")) is { } schemaNode)
        {
            parameter.Schema = ReadSchema(schemaNode);
        }

        parameter.Example = Get(node, "example");
        ReadExampleMap(node, parameter.Examples);
        ReadContentMap(node, parameter.Content);
        ReadExtensions(node, parameter.Extensions);
        return parameter;
    }

    private static OpenApiHeader ReadHeader(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var header = new OpenApiHeader();

        if (TryReadReference(node, out var reference))
        {
            header.Reference = reference;
            return header;
        }

        header.Description = GetString(node, "description");
        header.Required = GetBool(node, "required");
        header.Deprecated = GetBool(node, "deprecated");
        header.AllowEmptyValue = GetBool(node, "allowEmptyValue");
        header.Style = ParseParameterStyle(GetString(node, "style"));
        header.Explode = GetBoolNullable(node, "explode");
        header.AllowReserved = GetBool(node, "allowReserved");

        if (AsObject(Get(node, "schema")) is { } schemaNode)
        {
            header.Schema = ReadSchema(schemaNode);
        }

        header.Example = Get(node, "example");
        ReadExampleMap(node, header.Examples);
        ReadContentMap(node, header.Content);
        ReadExtensions(node, header.Extensions);
        return header;
    }

    private static OpenApiRequestBody ReadRequestBody(OpenApiObjectNode node)
    {
        var body = new OpenApiRequestBody();

        if (TryReadReference(node, out var reference))
        {
            body.Reference = reference;
            return body;
        }

        body.Description = GetString(node, "description");
        body.Required = GetBool(node, "required");
        ReadContentMap(node, body.Content);
        ReadExtensions(node, body.Extensions);
        return body;
    }

    private static OpenApiMediaType ReadMediaType(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var media = new OpenApiMediaType();

        if (AsObject(Get(node, "schema")) is { } schemaNode)
        {
            media.Schema = ReadSchema(schemaNode);
        }

        media.Example = Get(node, "example");
        ReadExampleMap(node, media.Examples);

        if (AsObject(Get(node, "encoding")) is { } encodingNode)
        {
            ReadObjectMap(encodingNode, ReadEncoding, media.Encoding);
        }

        ReadExtensions(node, media.Extensions);
        return media;
    }

    private static OpenApiEncoding ReadEncoding(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var encoding = new OpenApiEncoding
        {
            ContentType = GetString(node, "contentType"),
            Style = ParseParameterStyle(GetString(node, "style")),
            Explode = GetBoolNullable(node, "explode"),
            AllowReserved = GetBool(node, "allowReserved")
        };

        if (AsObject(Get(node, "headers")) is { } headersNode)
        {
            ReadObjectMap(headersNode, ReadHeader, encoding.Headers);
        }

        ReadExtensions(node, encoding.Extensions);
        return encoding;
    }

    private static OpenApiResponses ReadResponses(OpenApiObjectNode node)
    {
        var responses = new OpenApiResponses();
        foreach (var member in node)
        {
            if (IsExtension(member.Key))
            {
                responses.Extensions[member.Key] = member.Value;
            }
            else if (member.Value is OpenApiObjectNode responseNode)
            {
                responses.Items[member.Key] = ReadResponse(responseNode);
            }
        }

        return responses;
    }

    private static OpenApiResponse ReadResponse(OpenApiObjectNode node)
    {
        var response = new OpenApiResponse();

        if (TryReadReference(node, out var reference))
        {
            response.Reference = reference;
            return response;
        }

        response.Description = GetString(node, "description") ?? string.Empty;

        if (AsObject(Get(node, "headers")) is { } headersNode)
        {
            ReadObjectMap(headersNode, ReadHeader, response.Headers);
        }

        ReadContentMap(node, response.Content);

        if (AsObject(Get(node, "links")) is { } linksNode)
        {
            ReadObjectMap(linksNode, ReadLink, response.Links);
        }

        ReadExtensions(node, response.Extensions);
        return response;
    }

    private static OpenApiExample ReadExample(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var example = new OpenApiExample();

        if (TryReadReference(node, out var reference))
        {
            example.Reference = reference;
            return example;
        }

        example.Summary = GetString(node, "summary");
        example.Description = GetString(node, "description");
        example.Value = Get(node, "value");
        example.ExternalValue = GetString(node, "externalValue");
        ReadExtensions(node, example.Extensions);
        return example;
    }

    private static OpenApiLink ReadLink(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var link = new OpenApiLink();

        if (TryReadReference(node, out var reference))
        {
            link.Reference = reference;
            return link;
        }

        link.OperationRef = GetString(node, "operationRef");
        link.OperationId = GetString(node, "operationId");
        link.RequestBody = Get(node, "requestBody");
        link.Description = GetString(node, "description");

        if (AsObject(Get(node, "parameters")) is { } parametersNode)
        {
            foreach (var member in parametersNode)
            {
                if (!IsExtension(member.Key))
                {
                    link.Parameters[member.Key] = member.Value;
                }
            }
        }

        if (AsObject(Get(node, "server")) is { } serverNode)
        {
            link.Server = ReadServer(serverNode);
        }

        ReadExtensions(node, link.Extensions);
        return link;
    }

    private static OpenApiCallback ReadCallback(OpenApiObjectNode node)
    {
        var callback = new OpenApiCallback();

        if (TryReadReference(node, out var reference))
        {
            callback.Reference = reference;
            return callback;
        }

        foreach (var member in node)
        {
            if (IsExtension(member.Key))
            {
                callback.Extensions[member.Key] = member.Value;
            }
            else if (member.Value is OpenApiObjectNode pathItemNode)
            {
                callback.PathItems[member.Key] = ReadPathItem(pathItemNode);
            }
        }

        return callback;
    }

    private static OpenApiComponents ReadComponents(OpenApiObjectNode node)
    {
        var components = new OpenApiComponents();
        ReadNamedMap(node, "schemas", ReadSchemaValue, components.Schemas);
        ReadNamedMap(node, "responses", n => ReadResponse((OpenApiObjectNode)n), components.Responses);
        ReadNamedMap(node, "parameters", ReadParameter, components.Parameters);
        ReadNamedMap(node, "examples", ReadExample, components.Examples);
        ReadNamedMap(node, "requestBodies", n => ReadRequestBody((OpenApiObjectNode)n), components.RequestBodies);
        ReadNamedMap(node, "headers", ReadHeader, components.Headers);
        ReadNamedMap(node, "securitySchemes", ReadSecurityScheme, components.SecuritySchemes);
        ReadNamedMap(node, "links", ReadLink, components.Links);
        ReadNamedMap(node, "callbacks", n => ReadCallback((OpenApiObjectNode)n), components.Callbacks);
        ReadNamedMap(node, "pathItems", ReadPathItem, components.PathItems);
        ReadExtensions(node, components.Extensions);
        return components;
    }

    private static OpenApiSchema ReadSchemaValue(OpenApiNode value) => ReadSchema((OpenApiObjectNode)value);

    private static OpenApiSchema ReadSchema(OpenApiObjectNode node)
    {
        var schema = new OpenApiSchema();

        if (TryReadReference(node, out var reference))
        {
            schema.Reference = reference;
            return schema;
        }

        schema.Title = GetString(node, "title");
        schema.Description = GetString(node, "description");
        schema.Format = GetString(node, "format");
        ReadSchemaType(node, schema);
        schema.Default = Get(node, "default");
        schema.Const = Get(node, "const");
        schema.MultipleOf = GetDouble(node, "multipleOf");
        ReadNumberBounds(node, schema);
        schema.MaxLength = GetInt(node, "maxLength");
        schema.MinLength = GetInt(node, "minLength");
        schema.Pattern = GetString(node, "pattern");
        schema.MaxItems = GetInt(node, "maxItems");
        schema.MinItems = GetInt(node, "minItems");
        schema.UniqueItems = GetBoolNullable(node, "uniqueItems");
        schema.MaxProperties = GetInt(node, "maxProperties");
        schema.MinProperties = GetInt(node, "minProperties");
        schema.ReadOnly = GetBool(node, "readOnly");
        schema.WriteOnly = GetBool(node, "writeOnly");
        schema.Deprecated = GetBool(node, "deprecated");
        schema.Example = Get(node, "example");

        foreach (var item in ReadList(Get(node, "enum"), n => n))
        {
            schema.Enum.Add(item);
        }

        foreach (var item in ReadList(Get(node, "examples"), n => n))
        {
            schema.Examples.Add(item);
        }

        foreach (var name in ReadList(Get(node, "required"), n => ((OpenApiValueNode)n).GetString()))
        {
            schema.Required.Add(name);
        }

        if (AsObject(Get(node, "properties")) is { } propertiesNode)
        {
            ReadObjectMap(propertiesNode, ReadSchemaValue, schema.Properties);
        }

        ReadAdditionalProperties(node, schema);

        if (AsObject(Get(node, "items")) is { } itemsNode)
        {
            schema.Items = ReadSchema(itemsNode);
        }

        foreach (var item in ReadList(Get(node, "allOf"), ReadSchemaValue))
        {
            schema.AllOf.Add(item);
        }

        foreach (var item in ReadList(Get(node, "anyOf"), ReadSchemaValue))
        {
            schema.AnyOf.Add(item);
        }

        foreach (var item in ReadList(Get(node, "oneOf"), ReadSchemaValue))
        {
            schema.OneOf.Add(item);
        }

        if (AsObject(Get(node, "not")) is { } notNode)
        {
            schema.Not = ReadSchema(notNode);
        }

        if (AsObject(Get(node, "discriminator")) is { } discriminatorNode)
        {
            schema.Discriminator = ReadDiscriminator(discriminatorNode);
        }

        if (AsObject(Get(node, "xml")) is { } xmlNode)
        {
            schema.Xml = ReadXml(xmlNode);
        }

        if (AsObject(Get(node, "externalDocs")) is { } externalDocsNode)
        {
            schema.ExternalDocs = ReadExternalDocs(externalDocsNode);
        }

        ReadExtensions(node, schema.Extensions);
        return schema;
    }

    private static void ReadSchemaType(OpenApiObjectNode node, OpenApiSchema schema)
    {
        if (Get(node, "type") is OpenApiArrayNode typeArray)
        {
            foreach (var item in typeArray)
            {
                if (item is OpenApiValueNode { Kind: OpenApiValueKind.String } value)
                {
                    var text = value.GetString();
                    if (text == "null")
                    {
                        schema.Nullable = true;
                    }
                    else if (schema.Type is null)
                    {
                        schema.Type = ParseSchemaType(text);
                    }
                }
            }
        }
        else if (GetString(node, "type") is { } single)
        {
            schema.Type = ParseSchemaType(single);
        }

        if (GetBool(node, "nullable"))
        {
            schema.Nullable = true;
        }
    }

    private static void ReadNumberBounds(OpenApiObjectNode node, OpenApiSchema schema)
    {
        var maximum = GetDouble(node, "maximum");
        if (Get(node, "exclusiveMaximum") is OpenApiValueNode exclusiveMaximum)
        {
            if (exclusiveMaximum.Kind == OpenApiValueKind.Boolean)
            {
                if (exclusiveMaximum.GetBoolean())
                {
                    schema.ExclusiveMaximum = maximum;
                }
                else
                {
                    schema.Maximum = maximum;
                }
            }
            else
            {
                schema.ExclusiveMaximum = exclusiveMaximum.GetDouble();
                schema.Maximum = maximum;
            }
        }
        else
        {
            schema.Maximum = maximum;
        }

        var minimum = GetDouble(node, "minimum");
        if (Get(node, "exclusiveMinimum") is OpenApiValueNode exclusiveMinimum)
        {
            if (exclusiveMinimum.Kind == OpenApiValueKind.Boolean)
            {
                if (exclusiveMinimum.GetBoolean())
                {
                    schema.ExclusiveMinimum = minimum;
                }
                else
                {
                    schema.Minimum = minimum;
                }
            }
            else
            {
                schema.ExclusiveMinimum = exclusiveMinimum.GetDouble();
                schema.Minimum = minimum;
            }
        }
        else
        {
            schema.Minimum = minimum;
        }
    }

    private static void ReadAdditionalProperties(OpenApiObjectNode node, OpenApiSchema schema)
    {
        switch (Get(node, "additionalProperties"))
        {
            case OpenApiObjectNode additionalSchema:
                schema.AdditionalProperties = ReadSchema(additionalSchema);
                break;
            case OpenApiValueNode { Kind: OpenApiValueKind.Boolean } flag:
                schema.AdditionalPropertiesAllowed = flag.GetBoolean();
                break;
        }
    }

    private static OpenApiDiscriminator ReadDiscriminator(OpenApiObjectNode node)
    {
        var discriminator = new OpenApiDiscriminator
        {
            PropertyName = GetString(node, "propertyName") ?? string.Empty
        };

        if (AsObject(Get(node, "mapping")) is { } mappingNode)
        {
            foreach (var member in mappingNode)
            {
                if (member.Value is OpenApiValueNode { Kind: OpenApiValueKind.String } value)
                {
                    discriminator.Mapping[member.Key] = value.GetString();
                }
            }
        }

        ReadExtensions(node, discriminator.Extensions);
        return discriminator;
    }

    private static OpenApiXml ReadXml(OpenApiObjectNode node)
    {
        var xml = new OpenApiXml
        {
            Name = GetString(node, "name"),
            Namespace = GetString(node, "namespace"),
            Prefix = GetString(node, "prefix"),
            Attribute = GetBool(node, "attribute"),
            Wrapped = GetBool(node, "wrapped")
        };

        ReadExtensions(node, xml.Extensions);
        return xml;
    }

    private static OpenApiSecurityScheme ReadSecurityScheme(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var scheme = new OpenApiSecurityScheme();

        if (TryReadReference(node, out var reference))
        {
            scheme.Reference = reference;
            return scheme;
        }

        scheme.Type = ParseSecuritySchemeType(GetString(node, "type"));
        scheme.Description = GetString(node, "description");
        scheme.Name = GetString(node, "name");
        scheme.In = GetString(node, "in") is { } location ? ParseParameterLocation(location) : null;
        scheme.Scheme = GetString(node, "scheme");
        scheme.BearerFormat = GetString(node, "bearerFormat");
        scheme.OpenIdConnectUrl = GetString(node, "openIdConnectUrl");

        if (AsObject(Get(node, "flows")) is { } flowsNode)
        {
            scheme.Flows = ReadOAuthFlows(flowsNode);
        }

        ReadExtensions(node, scheme.Extensions);
        return scheme;
    }

    private static OpenApiOAuthFlows ReadOAuthFlows(OpenApiObjectNode node)
    {
        var flows = new OpenApiOAuthFlows();

        if (AsObject(Get(node, "implicit")) is { } implicitNode)
        {
            flows.Implicit = ReadOAuthFlow(implicitNode);
        }

        if (AsObject(Get(node, "password")) is { } passwordNode)
        {
            flows.Password = ReadOAuthFlow(passwordNode);
        }

        if (AsObject(Get(node, "clientCredentials")) is { } clientCredentialsNode)
        {
            flows.ClientCredentials = ReadOAuthFlow(clientCredentialsNode);
        }

        if (AsObject(Get(node, "authorizationCode")) is { } authorizationCodeNode)
        {
            flows.AuthorizationCode = ReadOAuthFlow(authorizationCodeNode);
        }

        if (AsObject(Get(node, "deviceAuthorization")) is { } deviceAuthorizationNode)
        {
            flows.DeviceAuthorization = ReadOAuthFlow(deviceAuthorizationNode);
        }

        ReadExtensions(node, flows.Extensions);
        return flows;
    }

    private static OpenApiOAuthFlow ReadOAuthFlow(OpenApiObjectNode node)
    {
        var flow = new OpenApiOAuthFlow
        {
            AuthorizationUrl = GetString(node, "authorizationUrl"),
            TokenUrl = GetString(node, "tokenUrl"),
            RefreshUrl = GetString(node, "refreshUrl")
        };

        if (AsObject(Get(node, "scopes")) is { } scopesNode)
        {
            foreach (var member in scopesNode)
            {
                if (member.Value is OpenApiValueNode { Kind: OpenApiValueKind.String } value)
                {
                    flow.Scopes[member.Key] = value.GetString();
                }
            }
        }

        ReadExtensions(node, flow.Extensions);
        return flow;
    }

    private static OpenApiSecurityRequirement ReadSecurityRequirement(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var requirement = new OpenApiSecurityRequirement();
        foreach (var member in node)
        {
            var scopes = new List<string>();
            if (member.Value is OpenApiArrayNode array)
            {
                foreach (var scope in array)
                {
                    if (scope is OpenApiValueNode { Kind: OpenApiValueKind.String } value2)
                    {
                        scopes.Add(value2.GetString());
                    }
                }
            }

            requirement.Schemes[member.Key] = scopes;
        }

        return requirement;
    }

    private static OpenApiTag ReadTag(OpenApiNode value)
    {
        var node = (OpenApiObjectNode)value;
        var tag = new OpenApiTag
        {
            Name = GetString(node, "name") ?? string.Empty,
            Summary = GetString(node, "summary"),
            Description = GetString(node, "description"),
            Parent = GetString(node, "parent"),
            Kind = GetString(node, "kind")
        };

        if (AsObject(Get(node, "externalDocs")) is { } externalDocsNode)
        {
            tag.ExternalDocs = ReadExternalDocs(externalDocsNode);
        }

        ReadExtensions(node, tag.Extensions);
        return tag;
    }

    private static OpenApiExternalDocumentation ReadExternalDocs(OpenApiObjectNode node)
    {
        var docs = new OpenApiExternalDocumentation
        {
            Description = GetString(node, "description"),
            Url = GetString(node, "url") ?? string.Empty
        };

        ReadExtensions(node, docs.Extensions);
        return docs;
    }

    private static bool TryReadReference(OpenApiObjectNode node, out OpenApiReference reference)
    {
        if (GetString(node, "$ref") is { } refValue)
        {
            reference = new OpenApiReference
            {
                Ref = refValue,
                Summary = GetString(node, "summary"),
                Description = GetString(node, "description")
            };
            return true;
        }

        reference = null!;
        return false;
    }

    private static void ReadExampleMap(OpenApiObjectNode node, IDictionary<string, OpenApiExample> target)
    {
        if (AsObject(Get(node, "examples")) is { } examplesNode)
        {
            ReadObjectMap(examplesNode, ReadExample, target);
        }
    }

    private static void ReadContentMap(OpenApiObjectNode node, IDictionary<string, OpenApiMediaType> target)
    {
        if (AsObject(Get(node, "content")) is { } contentNode)
        {
            ReadObjectMap(contentNode, ReadMediaType, target);
        }
    }

    private static void ReadNamedMap<T>(OpenApiObjectNode parent, string key, Func<OpenApiNode, T> read, IDictionary<string, T> target)
    {
        if (AsObject(Get(parent, key)) is { } mapNode)
        {
            ReadObjectMap(mapNode, read, target);
        }
    }

    private static void ReadObjectMap<T>(OpenApiObjectNode node, Func<OpenApiNode, T> read, IDictionary<string, T> target)
    {
        foreach (var member in node)
        {
            if (!IsExtension(member.Key))
            {
                target[member.Key] = read(member.Value);
            }
        }
    }

    private static List<T> ReadList<T>(OpenApiNode? node, Func<OpenApiNode, T> read)
    {
        var result = new List<T>();
        if (node is OpenApiArrayNode array)
        {
            foreach (var item in array)
            {
                result.Add(read(item));
            }
        }

        return result;
    }

    private static void ReadExtensions(OpenApiObjectNode node, IDictionary<string, OpenApiNode> extensions)
    {
        foreach (var member in node)
        {
            if (IsExtension(member.Key))
            {
                extensions[member.Key] = member.Value;
            }
        }
    }

    private static bool IsExtension(string key) => key.StartsWith("x-", StringComparison.Ordinal);

    private static OpenApiNode? Get(OpenApiObjectNode node, string key) => node.TryGetValue(key, out var value) ? value : null;

    private static OpenApiObjectNode? AsObject(OpenApiNode? node) => node as OpenApiObjectNode;

    private static string? GetString(OpenApiObjectNode node, string key) =>
        Get(node, key) is OpenApiValueNode { Kind: OpenApiValueKind.String } value ? value.GetString() : null;

    private static bool GetBool(OpenApiObjectNode node, string key) =>
        Get(node, key) is OpenApiValueNode { Kind: OpenApiValueKind.Boolean } value && value.GetBoolean();

    private static bool? GetBoolNullable(OpenApiObjectNode node, string key) =>
        Get(node, key) is OpenApiValueNode { Kind: OpenApiValueKind.Boolean } value ? value.GetBoolean() : null;

    private static int? GetInt(OpenApiObjectNode node, string key) =>
        Get(node, key) is OpenApiValueNode { Kind: OpenApiValueKind.Integer } value ? (int)value.GetInteger() : null;

    private static double? GetDouble(OpenApiObjectNode node, string key) =>
        Get(node, key) is OpenApiValueNode { Kind: OpenApiValueKind.Integer or OpenApiValueKind.Double } value ? value.GetDouble() : null;

    private static readonly (string Key, OperationType Type)[] OperationKeys =
    [
        ("get", OperationType.Get),
        ("put", OperationType.Put),
        ("post", OperationType.Post),
        ("delete", OperationType.Delete),
        ("options", OperationType.Options),
        ("head", OperationType.Head),
        ("patch", OperationType.Patch),
        ("trace", OperationType.Trace)
    ];

    private static ParameterLocation ParseParameterLocation(string? value) => value switch
    {
        "query" => ParameterLocation.Query,
        "header" => ParameterLocation.Header,
        "path" => ParameterLocation.Path,
        "cookie" => ParameterLocation.Cookie,
        _ => ParameterLocation.Query
    };

    private static ParameterStyle? ParseParameterStyle(string? value) => value switch
    {
        "matrix" => ParameterStyle.Matrix,
        "label" => ParameterStyle.Label,
        "simple" => ParameterStyle.Simple,
        "form" => ParameterStyle.Form,
        "spaceDelimited" => ParameterStyle.SpaceDelimited,
        "pipeDelimited" => ParameterStyle.PipeDelimited,
        "deepObject" => ParameterStyle.DeepObject,
        _ => null
    };

    private static SchemaType ParseSchemaType(string value) => value switch
    {
        "boolean" => SchemaType.Boolean,
        "object" => SchemaType.Object,
        "array" => SchemaType.Array,
        "number" => SchemaType.Number,
        "string" => SchemaType.String,
        "integer" => SchemaType.Integer,
        "null" => SchemaType.Null,
        _ => throw new OpenApiException($"Unknown schema type '{value}'.")
    };

    private static SecuritySchemeType ParseSecuritySchemeType(string? value) => value switch
    {
        "apiKey" => SecuritySchemeType.ApiKey,
        "http" => SecuritySchemeType.Http,
        "mutualTLS" => SecuritySchemeType.MutualTLS,
        "oauth2" => SecuritySchemeType.OAuth2,
        "openIdConnect" => SecuritySchemeType.OpenIdConnect,
        _ => SecuritySchemeType.ApiKey
    };
}
