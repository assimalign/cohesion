using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The runtime surface a resource's build described: the configuration sections a deployment sets and the management
/// API may change, the actions an operator runs, and the tools a portal opens.
/// </summary>
/// <remarks>
/// <para>
/// The surface is the <c>cohesion/runtime/v1</c> document that the SDK's describe step keeps as
/// <c>resource.runtime.json</c> beside the resource's build output and in its manifest package. It is fixed when the
/// resource is built, so a gateway validates deployment configuration and management changes against it without a
/// running instance. It never contains configuration values.
/// </para>
/// <para>
/// Members this version does not know are ignored, so a newer resource's additions do not break an older gateway.
/// </para>
/// </remarks>
[DebuggerDisplay("Configuration = {Configuration.Count}, Actions = {Actions.Count}, Tools = {Tools.Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceSurface
{
    /// <summary>The schema identifier of the surface document.</summary>
    public const string DocumentSchema = "cohesion/runtime/v1";

    private readonly byte[] _document;
    private readonly Dictionary<ResourceSectionPath, ResourceSurfaceSection> _sections;
    private readonly Dictionary<string, ResourceSurfaceAction> _actions;

    private ResourceSurface(
        byte[] document,
        ResourceSurfaceSection[] configuration,
        ResourceSurfaceAction[] actions,
        ResourceSurfaceTool[] tools)
    {
        _document = document;
        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(document));
        Configuration = new ReadOnlyCollection<ResourceSurfaceSection>(configuration);
        Actions = new ReadOnlyCollection<ResourceSurfaceAction>(actions);
        Tools = new ReadOnlyCollection<ResourceSurfaceTool>(tools);
        _sections = new Dictionary<ResourceSectionPath, ResourceSurfaceSection>(configuration.Length);
        foreach (ResourceSurfaceSection section in configuration)
        {
            _sections.Add(section.Path, section);
        }

        _actions = new Dictionary<string, ResourceSurfaceAction>(actions.Length, StringComparer.Ordinal);
        foreach (ResourceSurfaceAction action in actions)
        {
            _actions.Add(action.Name, action);
        }
    }

    /// <summary>
    /// Gets the SHA-256 digest of the document in compact form, as <c>sha256:&lt;hex&gt;</c>. It equals the
    /// <c>surfaceDigest</c> an instance built from the same code reports, so a gateway detects version skew.
    /// </summary>
    public string Digest { get; }

    /// <summary>Gets the configuration sections, in the order the resource registered them.</summary>
    public IReadOnlyList<ResourceSurfaceSection> Configuration { get; }

    /// <summary>Gets the actions, in the order the resource registered them.</summary>
    public IReadOnlyList<ResourceSurfaceAction> Actions { get; }

    /// <summary>Gets the operator tools, in the order the resource registered them.</summary>
    public IReadOnlyList<ResourceSurfaceTool> Tools { get; }

    /// <summary>Parses and validates a surface document.</summary>
    /// <param name="json">The <c>cohesion/runtime/v1</c> JSON text.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the <c>cohesion/runtime/v1</c> contract.</exception>
    public static ResourceSurface Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Parses and validates a surface document.</summary>
    /// <param name="utf8Json">The <c>cohesion/runtime/v1</c> document as UTF-8 JSON.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="JsonException">The bytes are not JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the <c>cohesion/runtime/v1</c> contract.</exception>
    public static ResourceSurface Parse(ReadOnlySpan<byte> utf8Json)
    {
        // The digest covers the compact rewrite, which is the exact form the resource writes, so a surface carried
        // through an indented document reports the same digest as the instance.
        var compact = new ArrayBufferWriter<byte>();
        using (JsonDocument source = JsonDocument.Parse(utf8Json.ToArray()))
        using (var writer = new Utf8JsonWriter(compact))
        {
            source.RootElement.WriteTo(writer);
        }

        byte[] document = compact.WrittenSpan.ToArray();
        JsonElement root;
        using (JsonDocument parsed = JsonDocument.Parse(document))
        {
            root = parsed.RootElement.Clone();
        }

        return Create(document, root);
    }

    /// <summary>Loads and validates a surface document from a file, normally <c>resource.runtime.json</c>.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="JsonException">The file is not JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the <c>cohesion/runtime/v1</c> contract.</exception>
    public static ResourceSurface Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>Finds a configuration section by its path.</summary>
    /// <param name="path">The section path.</param>
    /// <param name="section">The section, when the surface declares it.</param>
    /// <returns>Whether the surface declares the section.</returns>
    public bool TryGetSection(ResourceSectionPath path, [NotNullWhen(true)] out ResourceSurfaceSection? section) =>
        _sections.TryGetValue(path, out section);

    /// <summary>Finds an action by its name.</summary>
    /// <param name="name">The action name.</param>
    /// <param name="action">The action, when the surface declares it.</param>
    /// <returns>Whether the surface declares the action.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetAction(string name, [NotNullWhen(true)] out ResourceSurfaceAction? action)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _actions.TryGetValue(name, out action);
    }

    /// <summary>Exports the surface as compact <c>cohesion/runtime/v1</c> UTF-8 JSON.</summary>
    /// <returns>A copy of the document.</returns>
    public byte[] Export() => (byte[])_document.Clone();

    /// <summary>Writes the surface document as a JSON value.</summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(_document, skipInputValidation: true);
    }

    private static ResourceSurface Create(byte[] document, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, "$");
        }

        string schema = RequiredString(root, "schema", "schema");
        if (!string.Equals(schema, DocumentSchema, StringComparison.Ordinal))
        {
            throw Invalid(ErrorMessages.ResourceSurfaceSchemaUnsupported, schema, DocumentSchema);
        }

        var configuration = new List<ResourceSurfaceSection>();
        var paths = new HashSet<ResourceSectionPath>();
        int index = 0;
        foreach (JsonElement entry in OptionalArray(root, "configuration"))
        {
            string member = "configuration[" + index++.ToString(CultureInfo.InvariantCulture) + "]";
            RequireObject(entry, member);
            string path = RequiredString(entry, "path", member + ".path");
            if (!ResourceSurfaceGrammar.IsSectionPath(path))
            {
                throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".path", path);
            }

            var section = new ResourceSurfaceSection(
                path,
                RequiredString(entry, "mutability", member + ".mutability") switch
                {
                    "deployment" => ResourceSurfaceMutability.Deployment,
                    "restart" => ResourceSurfaceMutability.Restart,
                    "live" => ResourceSurfaceMutability.Live,
                    string other => throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".mutability", other),
                },
                RequiredString(entry, "scope", member + ".scope") switch
                {
                    "process" => ResourceSurfaceScope.Process,
                    "backend" => ResourceSurfaceScope.Backend,
                    string other => throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".scope", other),
                },
                RequiredBoolean(entry, "claimable", member + ".claimable"),
                RequiredSchema(entry, "schema", member + ".schema"));
            if (!paths.Add(section.Path))
            {
                throw Invalid(ErrorMessages.ResourceSurfaceEntryDuplicate, path);
            }

            configuration.Add(section);
        }

        var actions = new List<ResourceSurfaceAction>();
        var actionNames = new HashSet<string>(StringComparer.Ordinal);
        index = 0;
        foreach (JsonElement entry in OptionalArray(root, "actions"))
        {
            string member = "actions[" + index++.ToString(CultureInfo.InvariantCulture) + "]";
            RequireObject(entry, member);
            string name = RequiredName(entry, member);
            var action = new ResourceSurfaceAction(
                name,
                RequiredString(entry, "target", member + ".target") switch
                {
                    "anyInstance" => ResourceSurfaceActionTarget.AnyInstance,
                    "everyInstance" => ResourceSurfaceActionTarget.EveryInstance,
                    string other => throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".target", other),
                },
                RequiredSchema(entry, "input", member + ".input"),
                RequiredSchema(entry, "result", member + ".result"));
            if (!actionNames.Add(name))
            {
                throw Invalid(ErrorMessages.ResourceSurfaceEntryDuplicate, name);
            }

            actions.Add(action);
        }

        var tools = new List<ResourceSurfaceTool>();
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        index = 0;
        foreach (JsonElement entry in OptionalArray(root, "tools"))
        {
            string member = "tools[" + index++.ToString(CultureInfo.InvariantCulture) + "]";
            RequireObject(entry, member);
            string name = RequiredName(entry, member);
            string endpoint = RequiredString(entry, "endpoint", member + ".endpoint");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".endpoint", endpoint);
            }

            ResourceSurfaceToolProtocol protocol = RequiredString(entry, "protocol", member + ".protocol") switch
            {
                "http" => ResourceSurfaceToolProtocol.Http,
                "websocket" => ResourceSurfaceToolProtocol.WebSocket,
                "tcp" => ResourceSurfaceToolProtocol.Tcp,
                string other => throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".protocol", other),
            };
            string? subprotocol = null;
            if (entry.TryGetProperty("subprotocol", out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                subprotocol = value.ValueKind == JsonValueKind.String && protocol == ResourceSurfaceToolProtocol.WebSocket
                    ? value.GetString()
                    : throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, member + ".subprotocol");
            }

            if (!toolNames.Add(name))
            {
                throw Invalid(ErrorMessages.ResourceSurfaceEntryDuplicate, name);
            }

            tools.Add(new ResourceSurfaceTool(
                name,
                endpoint,
                protocol,
                subprotocol,
                RequiredBoolean(entry, "requiresDataAccess", member + ".requiresDataAccess")));
        }

        return new ResourceSurface(document, configuration.ToArray(), actions.ToArray(), tools.ToArray());
    }

    private static string RequiredName(JsonElement entry, string member)
    {
        string name = RequiredString(entry, "name", member + ".name");
        return ResourceSurfaceGrammar.IsName(name)
            ? name
            : throw Invalid(ErrorMessages.ResourceSurfaceValueInvalid, member + ".name", name);
    }

    private static IEnumerable<JsonElement> OptionalArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return Array.Empty<JsonElement>();
        }

        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, name);
    }

    private static void RequireObject(JsonElement value, string member)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, member);
        }
    }

    private static string RequiredString(JsonElement entry, string name, string member) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, member);

    private static bool RequiredBoolean(JsonElement entry, string name, string member) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, member);

    // A JSON schema is an object or a boolean.
    private static JsonElement RequiredSchema(JsonElement entry, string name, string member) =>
        entry.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False
            ? value
            : throw Invalid(ErrorMessages.ResourceSurfaceMemberInvalid, member);

    private static InvalidDataException Invalid(string message, params object[] arguments) =>
        new(string.Format(CultureInfo.CurrentCulture, message, arguments));

    private sealed class DebugView
    {
        private readonly ResourceSurface _target;

        public DebugView(ResourceSurface value)
        {
            _target = value;
        }

        public string Digest => _target.Digest;

        public IReadOnlyList<ResourceSurfaceSection> Configuration => _target.Configuration;

        public IReadOnlyList<ResourceSurfaceAction> Actions => _target.Actions;

        public IReadOnlyList<ResourceSurfaceTool> Tools => _target.Tools;
    }
}
