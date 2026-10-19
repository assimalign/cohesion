using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>The five storage models the Studio drives.</summary>
internal enum StudioModel
{
    Sql,
    Documents,
    Graph,
    KeyValue,
    Blob,
}

/// <summary>How a model workspace reaches its engine.</summary>
internal enum ConnectionMode
{
    /// <summary>Direct <c>DatabaseSession</c> on the in-process engine.</summary>
    Embedded,

    /// <summary>The Studio starts the model's server on 127.0.0.1 and talks to it through the real client.</summary>
    WireLoopback,

    /// <summary>The real client against an already-running server (for example the SampleHost fixture).</summary>
    WireExternal,
}

/// <summary>Per-model connection settings chosen on the workspace page.</summary>
internal sealed class ModelSettings
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.Embedded;

    /// <summary>Loopback listener port; 0 lets the OS choose.</summary>
    public int LoopbackPort { get; set; }

    /// <summary><c>host:port</c> of an external server.</summary>
    public string ExternalEndpoint { get; set; } = "127.0.0.1:5740";

    public ModelSettings Clone() => new() { Mode = Mode, LoopbackPort = LoopbackPort, ExternalEndpoint = ExternalEndpoint };
}

/// <summary>The whole workspace configuration.</summary>
internal sealed class StudioSettings
{
    public string DataRoot { get; set; } = DefaultDataRoot;

    public Dictionary<StudioModel, ModelSettings> Models { get; } = new()
    {
        [StudioModel.Sql] = new(),
        [StudioModel.Documents] = new(),
        [StudioModel.Graph] = new(),
        [StudioModel.KeyValue] = new(),
        [StudioModel.Blob] = new(),
    };

    public static string DefaultDataRoot { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CohesionStudio", "data");

    public StudioSettings Clone()
    {
        var clone = new StudioSettings { DataRoot = DataRoot };
        foreach (var (model, settings) in Models)
        {
            clone.Models[model] = settings.Clone();
        }

        return clone;
    }
}

internal static class StudioModelExtensions
{
    extension(StudioModel model)
    {
        public string DisplayName => model switch
        {
            StudioModel.Sql => "SQL",
            StudioModel.Documents => "Documents",
            StudioModel.Graph => "Graph",
            StudioModel.KeyValue => "Key-Value",
            StudioModel.Blob => "Blob",
            _ => model.ToString(),
        };

        public string FolderName => model switch
        {
            StudioModel.Sql => "sql",
            StudioModel.Documents => "documents",
            StudioModel.Graph => "graph",
            StudioModel.KeyValue => "keyvalue",
            StudioModel.Blob => "blob",
            _ => model.ToString().ToLowerInvariant(),
        };

        /// <summary>Documents has no wire server or client yet (Documents.Client/src is empty).</summary>
        public bool HasWireServer => model is not StudioModel.Documents;
    }

    extension(ConnectionMode mode)
    {
        public string DisplayName => mode switch
        {
            ConnectionMode.Embedded => "Embedded",
            ConnectionMode.WireLoopback => "Wire (loopback)",
            ConnectionMode.WireExternal => "Wire (external host:port)",
            _ => mode.ToString(),
        };

        public bool IsWire => mode is ConnectionMode.WireLoopback or ConnectionMode.WireExternal;
    }
}
