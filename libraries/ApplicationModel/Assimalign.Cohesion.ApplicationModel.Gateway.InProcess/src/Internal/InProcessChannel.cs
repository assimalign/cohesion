using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// The channel between the programs a process runs and their gateway, in both directions: their
/// publications and renewals queue on a channel the gateway's own flow drains while the gateway
/// runs, and their control planes are attached to it for the gateway to call.
/// </summary>
/// <remarks>
/// <para>
/// It is registered at the infrastructure's in-process endpoint
/// (<see cref="InProcessPlatform.RegisterGateway"/>), which each program's environment names, so a
/// program's in-process client writes to it. Each request carries its own completion: the program
/// waits for the answer with its own cancellation, and the gateway handles the request on the
/// drain's flow, outside the program's environment and unaffected by the program's cancellation.
/// </para>
/// <para>
/// It receives only while a gateway runs. A request made before a gateway starts, or after it
/// stops, is refused at once, as an HTTP request to an endpoint nothing listens on is; a renewal
/// that is refused is retried on the program's next tick. Attached control planes stay attached for
/// as long as their programs run, so a gateway that runs again over the same infrastructure calls
/// the programs it adopts.
/// </para>
/// </remarks>
internal sealed class InProcessChannel : IInProcessGateway
{
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<InstanceKey, IApplicationInstanceControlPlane> _controlPlanes = new();
    private readonly Lock _lock = new();
    private readonly Uri _endpoint;
    private CancellationTokenSource? _draining;
    private Task _drain = Task.CompletedTask;
    private bool _isReceiving;

    public InProcessChannel(Uri endpoint)
    {
        _endpoint = endpoint;
    }

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var request = new PublishRequest(manifest);
        Enqueue(request);
        return new ValueTask<ApplicationInstancePublication>(request.Answer.Task.WaitAsync(cancellationToken));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var request = new RenewRequest(manifest);
        Enqueue(request);
        return new ValueTask<bool>(request.Answer.Task.WaitAsync(cancellationToken));
    }

    public IDisposable Attach(IApplicationInstanceControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        InstanceKey key = controlPlane.Manifest.Key;
        _controlPlanes[key] = controlPlane;
        return new Attachment(_controlPlanes, key, controlPlane);
    }

    /// <summary>
    /// Gets the control plane attached for an instance, or <see langword="null"/> when its program has
    /// not finished starting or has stopped.
    /// </summary>
    public IApplicationInstanceControlPlane? Find(InstanceKey key)
    {
        return _controlPlanes.TryGetValue(key, out IApplicationInstanceControlPlane? controlPlane) ? controlPlane : null;
    }

    /// <summary>
    /// Starts handing what the channel receives to the gateway, on the caller's flow.
    /// </summary>
    /// <exception cref="InvalidOperationException">A gateway already receives from the channel.</exception>
    public void Start(IApplicationInstancePublisher gateway)
    {
        lock (_lock)
        {
            if (_isReceiving)
            {
                throw new InvalidOperationException(ErrorMessages.ChannelAlreadyStarted);
            }

            var draining = new CancellationTokenSource();
            _draining = draining;
            _isReceiving = true;

            // Task.Run captures the caller's execution context: the gateway's, not a program's.
            _drain = Task.Run(() => DrainAsync(gateway, draining.Token));
        }
    }

    /// <summary>
    /// Stops receiving: the request being handled is cancelled, and every request still queued is
    /// refused.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? draining;
        Task drain;
        lock (_lock)
        {
            if (!_isReceiving)
            {
                return;
            }

            // From here no request is queued, so the queue left once the drain ends is final.
            _isReceiving = false;
            draining = _draining;
            drain = _drain;
            _draining = null;
        }

        await draining!.CancelAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);
        draining.Dispose();

        while (_requests.Reader.TryRead(out Request? request))
        {
            request.Refuse(Unreachable());
        }
    }

    private void Enqueue(Request request)
    {
        lock (_lock)
        {
            if (!_isReceiving)
            {
                throw Unreachable();
            }

            _requests.Writer.TryWrite(request);
        }
    }

    private async Task DrainAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_requests.Reader.TryRead(out Request? request))
                {
                    await request.HandleAsync(gateway, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The gateway stopped.
        }
    }

    private InvalidOperationException Unreachable()
    {
        return new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelNotReceiving, _endpoint.AbsoluteUri));
    }

    private sealed class Attachment : IDisposable
    {
        private readonly ConcurrentDictionary<InstanceKey, IApplicationInstanceControlPlane> _controlPlanes;
        private readonly KeyValuePair<InstanceKey, IApplicationInstanceControlPlane> _entry;

        public Attachment(ConcurrentDictionary<InstanceKey, IApplicationInstanceControlPlane> controlPlanes, InstanceKey key, IApplicationInstanceControlPlane controlPlane)
        {
            _controlPlanes = controlPlanes;
            _entry = new KeyValuePair<InstanceKey, IApplicationInstanceControlPlane>(key, controlPlane);
        }

        // Detaches the control plane only while it is still the one attached for its instance.
        public void Dispose()
        {
            _controlPlanes.TryRemove(_entry);
        }
    }

    private abstract class Request
    {
        protected Request(ApplicationInstanceManifest manifest)
        {
            Manifest = manifest;
        }

        protected ApplicationInstanceManifest Manifest { get; }

        public abstract Task HandleAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken);

        public abstract void Refuse(Exception exception);
    }

    private sealed class PublishRequest : Request
    {
        public PublishRequest(ApplicationInstanceManifest manifest)
            : base(manifest)
        {
        }

        public TaskCompletionSource<ApplicationInstancePublication> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task HandleAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
        {
            try
            {
                Answer.TrySetResult(await gateway.PublishAsync(Manifest, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                // Deliberately broad: the gateway's refusal, whatever it is, is the program's answer.
                Answer.TrySetException(exception);
            }
        }

        public override void Refuse(Exception exception)
        {
            Answer.TrySetException(exception);
        }
    }

    private sealed class RenewRequest : Request
    {
        public RenewRequest(ApplicationInstanceManifest manifest)
            : base(manifest)
        {
        }

        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task HandleAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
        {
            try
            {
                Answer.TrySetResult(await gateway.RenewAsync(Manifest, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                // Deliberately broad: the gateway's refusal, whatever it is, is the program's answer.
                Answer.TrySetException(exception);
            }
        }

        public override void Refuse(Exception exception)
        {
            Answer.TrySetException(exception);
        }
    }
}
