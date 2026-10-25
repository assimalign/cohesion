using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// An immutable snapshot of what a resource lets a deployment set, the management API change, and an
/// operator invoke or open: its configuration sections, actions and tools.
/// </summary>
/// <remarks>
/// The control plane freezes the surface when the resource's host is built. The SDK's describe step writes
/// <see cref="Export"/> beside <c>resource.json</c>, so a gateway and a management portal know the surface
/// without contacting a running instance. The document never contains configuration values.
/// </remarks>
[DebuggerDisplay("Configuration = {Configuration.Count}, Actions = {Actions.Count}, Tools = {Tools.Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceRuntimeSurface
{
    /// <summary>The schema identifier of the exported document.</summary>
    public const string DocumentSchema = "cohesion/runtime/v1";

    private readonly byte[] _document;

    internal ResourceRuntimeSurface(
        IReadOnlyList<ResourceConfigurationSection> configuration,
        IReadOnlyList<ResourceAction> actions,
        IReadOnlyList<ResourceTool> tools)
    {
        Configuration = configuration;
        Actions = actions;
        Tools = tools;
        _document = Write(configuration, actions, tools);
        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_document));
    }

    /// <summary>Gets a surface with nothing to configure, invoke or open.</summary>
    public static ResourceRuntimeSurface Empty { get; } = new(
        Array.Empty<ResourceConfigurationSection>(),
        Array.Empty<ResourceAction>(),
        Array.Empty<ResourceTool>());

    /// <summary>Gets the configuration sections, in registration order.</summary>
    public IReadOnlyList<ResourceConfigurationSection> Configuration { get; }

    /// <summary>Gets the actions, in registration order.</summary>
    public IReadOnlyList<ResourceAction> Actions { get; }

    /// <summary>Gets the operator tools, in registration order.</summary>
    public IReadOnlyList<ResourceTool> Tools { get; }

    /// <summary>Gets the SHA-256 digest of the exported document, which a gateway compares to detect version skew.</summary>
    public string Digest { get; }

    /// <summary>Exports the <c>cohesion/runtime/v1</c> document as UTF-8 JSON.</summary>
    /// <returns>A copy of the document.</returns>
    public byte[] Export() => (byte[])_document.Clone();

    /// <summary>Writes the <c>cohesion/runtime/v1</c> document as a JSON value.</summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(_document, skipInputValidation: true);
    }

    private static byte[] Write(
        IReadOnlyList<ResourceConfigurationSection> configuration,
        IReadOnlyList<ResourceAction> actions,
        IReadOnlyList<ResourceTool> tools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", DocumentSchema);
            writer.WriteStartArray("configuration");
            foreach (ResourceConfigurationSection section in configuration)
            {
                writer.WriteStartObject();
                writer.WriteString("path", section.Path.Value);
                writer.WriteString("mutability", section.Mutability switch
                {
                    ResourceConfigurationMutability.Deployment => "deployment",
                    ResourceConfigurationMutability.Restart => "restart",
                    _ => "live",
                });
                writer.WriteString("scope", section.Scope == ResourceConfigurationScope.Backend ? "backend" : "process");
                writer.WriteBoolean("claimable", section.Claimable);
                writer.WritePropertyName("schema");
                section.Schema.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("actions");
            foreach (ResourceAction action in actions)
            {
                writer.WriteStartObject();
                writer.WriteString("name", action.Name.Value);
                writer.WriteString("target", action.Target == ResourceActionTarget.EveryInstance ? "everyInstance" : "anyInstance");
                writer.WritePropertyName("input");
                action.InputSchema.WriteTo(writer);
                writer.WritePropertyName("result");
                action.ResultSchema.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("tools");
            foreach (ResourceTool tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name.Value);
                writer.WriteString("endpoint", tool.Endpoint);
                writer.WriteString("protocol", tool.Protocol switch
                {
                    ResourceToolProtocol.WebSocket => "websocket",
                    ResourceToolProtocol.Tcp => "tcp",
                    _ => "http",
                });
                if (tool.Subprotocol is not null)
                {
                    writer.WriteString("subprotocol", tool.Subprotocol);
                }

                writer.WriteBoolean("requiresDataAccess", tool.RequiresDataAccess);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed class DebugView
    {
        private readonly ResourceRuntimeSurface _target;

        public DebugView(ResourceRuntimeSurface value)
        {
            _target = value;
        }

        public IReadOnlyList<ResourceConfigurationSection> Configuration => _target.Configuration;

        public IReadOnlyList<ResourceAction> Actions => _target.Actions;

        public IReadOnlyList<ResourceTool> Tools => _target.Tools;

        public string Digest => _target.Digest;
    }
}
