using System;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

/// <summary>
/// The transport between the programs a process runs and their gateway: their publications and
/// renewals queue on a channel, and the gateway's own flow drains it while the gateway runs.
/// </summary>
/// <remarks>
/// <para>
/// It is registered at an in-process endpoint (<see cref="ApplicationInstancePublisher.Register"/>),
/// which each program's environment names, so a program's ordinary publisher writes to it. Each
/// request carries its own completion: the program waits for the answer with its own cancellation,
/// and the gateway handles the request on the drain's flow, outside the program's environment and
/// unaffected by the program's cancellation.
/// </para>
/// <para>
/// It receives only while a gateway runs. A request made before a gateway starts, or after it
/// stops, is refused at once, as an HTTP request to an endpoint nothing listens on is; a renewal
/// that is refused is retried on the program's next tick.
/// </para>
/// </remarks>
internal sealed class InProcessChannel : IApplicationInstancePublisher
{
    private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
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
                throw new InvalidOperationException(ErrorMessages.TransportAlreadyStarted);
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
        return new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.TransportNotReceiving, _endpoint.AbsoluteUri));
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
