using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// The single Studio session: settings, the engines, and one workspace per model. The UI pages and
/// the headless smoke mode both drive the engines through this type's workspaces.
/// </summary>
internal sealed class StudioState : IAsyncDisposable
{
    private readonly Dictionary<StudioModel, ModelWorkspace> _workspaces = [];
    private readonly Dictionary<StudioModel, string> _status = [];
    private readonly List<string> _log = [];
    private readonly object _logSync = new();

    public StudioSettings Settings { get; private set; } = new();

    public StudioEngines? Engines { get; private set; }

    /// <summary>Raised (on an arbitrary thread) after <see cref="ApplyAsync"/> replaces the workspaces.</summary>
    public event EventHandler? WorkspacesChanged;

    /// <summary>Raised (on an arbitrary thread) for every log line.</summary>
    public event EventHandler<string>? LogAdded;

    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_logSync)
            {
                return [.. _log];
            }
        }
    }

    public ModelWorkspace? Get(StudioModel model) => _workspaces.GetValueOrDefault(model);

    public T? Get<T>(StudioModel model) where T : ModelWorkspace => _workspaces.GetValueOrDefault(model) as T;

    public string GetStatus(StudioModel model) => _status.GetValueOrDefault(model, "not started");

    public void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_logSync)
        {
            _log.Add(line);
        }

        LogAdded?.Invoke(this, line);
    }

    /// <summary>Tears down the current engines/servers/workspaces and builds new ones from <paramref name="settings"/>.</summary>
    public async Task ApplyAsync(StudioSettings settings, CancellationToken cancellationToken = default)
    {
        settings = settings.Clone();
        settings.Models[StudioModel.Documents].Mode = ConnectionMode.Embedded;

        await CloseAsync().ConfigureAwait(false);
        Log($"Creating engines under {settings.DataRoot}");
        Engines = StudioEngines.Create(settings.DataRoot);

        foreach (StudioModel model in Enum.GetValues<StudioModel>())
        {
            ModelSettings modelSettings = settings.Models[model];
            if (Engines.DeclarationFailures.TryGetValue(model, out string? declarationFailure))
            {
                Log($"{model.DisplayName}: declared database '{StudioEngines.DeclaredDatabase}' failed the build, so the engine runs without it: {declarationFailure}");
            }

            try
            {
                EndPoint? endPoint = modelSettings.Mode switch
                {
                    ConnectionMode.WireLoopback => await Engines.StartServerAsync(model, modelSettings.LoopbackPort, cancellationToken).ConfigureAwait(false),
                    ConnectionMode.WireExternal => ParseEndPoint(modelSettings.ExternalEndpoint),
                    _ => null,
                };

                ModelWorkspace workspace = CreateWorkspace(model, modelSettings.Mode, Engines, endPoint);
                _workspaces[model] = workspace;
                _status[model] = declarationFailure is null
                    ? workspace.Description
                    : $"{workspace.Description}; no declared '{StudioEngines.DeclaredDatabase}' (see the log)";
                Log($"{model.DisplayName}: {workspace.Description}");
            }
            catch (Exception exception)
            {
                // One model failing (port in use, bad endpoint) must not take the others down.
                _status[model] = $"FAILED: {exception.Message}";
                Log($"{model.DisplayName}: FAILED to start {modelSettings.Mode.DisplayName}: {ErrorText.Describe(exception)}");
            }
        }

        Settings = settings;
        WorkspacesChanged?.Invoke(this, EventArgs.Empty);
    }

    public static ModelWorkspace CreateWorkspace(StudioModel model, ConnectionMode mode, StudioEngines engines, EndPoint? endPoint) => model switch
    {
        StudioModel.Sql => new SqlWorkspace(mode, engines, endPoint),
        StudioModel.Documents => new DocumentWorkspace(engines),
        StudioModel.Graph => new GraphWorkspace(mode, engines, endPoint),
        StudioModel.KeyValue => new KeyValueWorkspace(mode, engines, endPoint),
        StudioModel.Blob => new BlobWorkspace(mode, engines, endPoint),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    /// <summary>Parses <c>host:port</c>, <c>[v6]:port</c>, or a bare host (default port 5740).</summary>
    public static EndPoint ParseEndPoint(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.Trim();
        string host = text;
        int port = 5740;

        int separator = text.StartsWith('[') ? text.IndexOf("]:", StringComparison.Ordinal) + 1 : text.LastIndexOf(':');
        if (separator > 0)
        {
            host = text[..separator];
            if (!int.TryParse(text[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
            {
                throw new FormatException($"Invalid port in '{text}'.");
            }
        }

        host = host.Trim('[', ']');
        return IPAddress.TryParse(host, out IPAddress? address)
            ? new IPEndPoint(address, port)
            : new DnsEndPoint(host, port);
    }

    private async Task CloseAsync()
    {
        foreach (ModelWorkspace workspace in _workspaces.Values)
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }

        _workspaces.Clear();
        _status.Clear();

        if (Engines is { } engines)
        {
            Engines = null;
            await engines.DisposeAsync().ConfigureAwait(false);
            Log("Engines disposed.");
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
