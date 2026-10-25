using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

public sealed partial class ResourceControlPlane
{
    private readonly List<ResourceConfigurationSection> _sections = new();
    private readonly List<ResourceAction> _actions = new();
    private readonly List<ResourceTool> _tools = new();
    private readonly Dictionary<ResourceConfigurationPath, SectionRuntime> _sectionRuntime = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private ResourceRuntimeSurface? _surface;
    private ResourceConfigurationStatus _configurationStatus = ResourceConfigurationStatus.Empty;
    private int _startupRejections;
    private int _configurationHealthRegistered;

    /// <inheritdoc />
    public ResourceRuntimeSurface Surface
    {
        get
        {
            lock (_sync)
            {
                return _surface ?? new ResourceRuntimeSurface(_sections.ToArray(), _actions.ToArray(), _tools.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public ResourceConfigurationStatus ConfigurationStatus => Volatile.Read(ref _configurationStatus);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="section"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The host is built, or a section with the same path is registered.</exception>
    public void AddConfiguration(ResourceConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        lock (_sync)
        {
            ThrowIfSurfaceFrozen();
            if (_sections.Any(existing => existing.Path == section.Path))
            {
                throw new InvalidOperationException(ErrorMessages.ResourceConfigurationSectionDuplicate);
            }

            _sections.Add(section);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The host is built, or an action with the same name is registered.</exception>
    public void AddAction(ResourceAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            ThrowIfSurfaceFrozen();
            if (_actions.Any(existing => existing.Name == action.Name))
            {
                throw new InvalidOperationException(ErrorMessages.ResourceActionDuplicate);
            }

            _actions.Add(action);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="tool"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The host is built, or a tool with the same name is registered.</exception>
    public void AddTool(ResourceTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_sync)
        {
            ThrowIfSurfaceFrozen();
            if (_tools.Any(existing => existing.Name == tool.Name))
            {
                throw new InvalidOperationException(ErrorMessages.ResourceToolDuplicate);
            }

            _tools.Add(tool);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> is undefined.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the record was applied.</exception>
    public async ValueTask<ResourceConfigurationStatus> ApplyConfigurationAsync(
        ResourceConfigurationRecord record,
        ResourceConfigurationPhase phase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        ResourceRuntimeSurface surface = Surface;
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceConfigurationStatus current = Volatile.Read(ref _configurationStatus);
            if (phase == ResourceConfigurationPhase.Running && record.Digest == current.RecordDigest)
            {
                return current;
            }

            RegisterConfigurationHealth();
            var sections = new ResourceConfigurationSectionStatus[surface.Configuration.Count];
            int startupRejections = 0;
            for (int index = 0; index < surface.Configuration.Count; index++)
            {
                ResourceConfigurationSection section = surface.Configuration[index];
                if (!_sectionRuntime.TryGetValue(section.Path, out SectionRuntime? runtime))
                {
                    section.TryNormalize(JsonNodeConversion.ToElement(section.CreateBaseline()), out string? baseline, out _);
                    runtime = new SectionRuntime(baseline ?? string.Empty);
                    _sectionRuntime.Add(section.Path, runtime);
                }

                JsonNode? effective = Effective(section, record);
                string? error = null;
                string? normalized = null;
                JsonElement value = default;
                if (effective is null)
                {
                    error = ResourceConfigurationErrors.NullValue;
                }
                else
                {
                    value = JsonNodeConversion.ToElement(effective);
                    section.TryNormalize(value, out normalized, out error);
                }

                if (normalized == runtime.InForce)
                {
                    runtime.Settle(normalized);
                }
                else if (error is null && phase == ResourceConfigurationPhase.Running && section.Mutability != ResourceConfigurationMutability.Live)
                {
                    runtime.RequireRestart();
                    HostingResourceEventSource.Log.ConfigurationRestartRequired(section.Path);
                }
                else if ((error ?? TryApply(section, value)) is string rejection)
                {
                    runtime.Reject(rejection, phase);
                    HostingResourceEventSource.Log.ConfigurationRejected(section.Path);
                }
                else
                {
                    runtime.Settle(normalized!);
                    HostingResourceEventSource.Log.ConfigurationApplied(section.Path);
                }

                if (runtime.StartupRejected)
                {
                    startupRejections++;
                }

                sections[index] = new ResourceConfigurationSectionStatus(section.Path, runtime.State, runtime.Error);
            }

            var status = new ResourceConfigurationStatus(record.Digest, sections);
            Volatile.Write(ref _startupRejections, startupRejections);
            Volatile.Write(ref _configurationStatus, status);
            return status;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="name"/> is the default value, or the input does not bind.</exception>
    /// <exception cref="NotSupportedException">The resource exposes no action with this name.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> InvokeActionAsync(
        ResourceActionName name,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceActionNameInvalid, nameof(name));
        }

        ResourceAction action = Surface.Actions.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw new NotSupportedException(ErrorMessages.ResourceActionUnknown);
        return await action.InvokeAsync(input, cancellationToken).ConfigureAwait(false);
    }

    // Called under _sync by AttachHost: a built host's surface no longer changes.
    private void FreezeSurface()
    {
        _surface ??= new ResourceRuntimeSurface(_sections.ToArray(), _actions.ToArray(), _tools.ToArray());
    }

    private void ThrowIfSurfaceFrozen()
    {
        if (_surface is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ResourceSurfaceFrozen);
        }
    }

    private bool HasStartupRejections => Volatile.Read(ref _startupRejections) != 0;

    private void RegisterConfigurationHealth()
    {
        if (Interlocked.Exchange(ref _configurationHealthRegistered, 1) == 0)
        {
            AddHealthContributor(new ResourceConfigurationHealthContributor(() => ConfigurationStatus));
        }
    }

    // The section's baseline with every layer that names the section merged over it, in precedence order.
    // A null at the section level resets the section to its baseline.
    private static JsonNode? Effective(ResourceConfigurationSection section, ResourceConfigurationRecord record)
    {
        JsonNode? effective = section.CreateBaseline();
        foreach (JsonObject layer in record.LayersInPrecedenceOrder())
        {
            if (!layer.TryGetPropertyValue(section.Path.Value, out JsonNode? patch))
            {
                continue;
            }

            effective = patch is null
                ? section.CreateBaseline()
                : JsonMergePatch.Apply(effective, patch);
        }

        return effective;
    }

    private static string? TryApply(ResourceConfigurationSection section, JsonElement value)
    {
        try
        {
            return section.Apply(value);
        }
        catch (Exception exception)
        {
            // A feature's apply callback is application code; whatever it throws must leave its previous
            // value in force and be reported, never end the configuration runner or the host.
            return ResourceConfigurationErrors.ApplyFailed(exception);
        }
    }

    private sealed class SectionRuntime
    {
        public SectionRuntime(string baseline)
        {
            Baseline = baseline;
            InForce = baseline;
        }

        public string Baseline { get; }

        public string InForce { get; private set; }

        public ResourceConfigurationState State { get; private set; } = ResourceConfigurationState.Baseline;

        public string? Error { get; private set; }

        public bool StartupRejected { get; private set; }

        public void Settle(string inForce)
        {
            InForce = inForce;
            State = inForce == Baseline ? ResourceConfigurationState.Baseline : ResourceConfigurationState.Applied;
            Error = null;
            StartupRejected = false;
        }

        public void RequireRestart()
        {
            State = ResourceConfigurationState.RestartRequired;
            Error = null;
        }

        public void Reject(string error, ResourceConfigurationPhase phase)
        {
            State = ResourceConfigurationState.Rejected;
            Error = error;
            StartupRejected |= phase == ResourceConfigurationPhase.Startup;
        }
    }
}
