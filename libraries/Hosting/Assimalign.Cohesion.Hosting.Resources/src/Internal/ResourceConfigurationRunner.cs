using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Applies the deployment record in <see cref="AppEnvironment.Variables.ConfigDirectory"/> before the host starts, then follows the
/// directory while the host runs, so instance restarts and scale-out pick up the record with no gateway in
/// the path. The platform delivers the files; this runner only reads them.
/// </summary>
/// <remarks>
/// The directory is polled rather than watched: Kubernetes swaps a projected volume's symbolic links and
/// Docker Desktop bind mounts can drop change events, and a few small reads per interval cost nothing. A read
/// is accepted only when two consecutive loads agree, so a platform that updates files one at a time is never
/// seen half-written.
/// </remarks>
[DebuggerDisplay("{Directory,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceConfigurationRunner : IHostRunner
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly IHostRunner _inner;
    private readonly IResourceControlPlane _plane;

    public ResourceConfigurationRunner(IHostRunner inner, IResourceControlPlane plane, string directory, TimeSpan pollInterval)
    {
        _inner = inner;
        _plane = plane;
        Directory = directory;
        PollInterval = pollInterval;
    }

    public string Directory { get; }

    public TimeSpan PollInterval { get; }

    public async Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
    {
        string? applied = await ApplyAsync(ResourceConfigurationPhase.Startup, null, cancellationToken).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task follow = FollowAsync(applied, stop.Token);
        try
        {
            await _inner.RunAsync(run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            await follow.ConfigureAwait(false);
        }
    }

    /// <summary>Loads and applies the record when it is stable and changed; returns the digest now applied.</summary>
    internal async ValueTask<string?> ApplyAsync(ResourceConfigurationPhase phase, string? applied, CancellationToken cancellationToken)
    {
        ResourceConfigurationRecord? record = TryLoadStable();
        if (record is null || (phase == ResourceConfigurationPhase.Running && record.Digest == applied))
        {
            return applied;
        }

        ResourceConfigurationStatus status = await _plane.ApplyConfigurationAsync(record, phase, cancellationToken).ConfigureAwait(false);
        return status.RecordDigest;
    }

    private async Task FollowAsync(string? applied, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                applied = await ApplyAsync(ResourceConfigurationPhase.Running, applied, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private ResourceConfigurationRecord? TryLoadStable()
    {
        try
        {
            ResourceConfigurationRecord first = ResourceConfigurationRecord.Load(Directory);
            ResourceConfigurationRecord second = ResourceConfigurationRecord.Load(Directory);
            return first.Digest == second.Digest ? second : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A missing, partially written or malformed record keeps the configuration in force; the next
            // poll reads the directory again.
            HostingResourceEventSource.Log.ConfigurationRecordUnreadable(exception);
            return null;
        }
    }

    private sealed class DebugView
    {
        private readonly ResourceConfigurationRunner _target;

        public DebugView(ResourceConfigurationRunner value)
        {
            _target = value;
        }

        public string Directory => _target.Directory;

        public TimeSpan PollInterval => _target.PollInterval;
    }
}
