using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Threading;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Stands in for the providers' per-operation transport factory: hands out transports over one
/// <see cref="RecordingHttpMessageHandler"/>, remembers the server-certificate validator each
/// operation asked for, and counts how many transports were created and disposed.
/// </summary>
internal sealed class RecordingTransportFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly List<RemoteCertificateValidationCallback?> _validators = new();
    private int _created;
    private int _disposed;

    internal RecordingTransportFactory(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    internal IReadOnlyList<RemoteCertificateValidationCallback?> Validators
    {
        get
        {
            lock (_validators)
            {
                return _validators.ToArray();
            }
        }
    }

    internal int Created => Volatile.Read(ref _created);

    internal int Disposed => Volatile.Read(ref _disposed);

    internal HttpMessageInvoker Create(RemoteCertificateValidationCallback? validator)
    {
        lock (_validators)
        {
            _validators.Add(validator);
        }

        Interlocked.Increment(ref _created);
        return new TrackedInvoker(_handler, this);
    }

    private void OnDisposed() => Interlocked.Increment(ref _disposed);

    private sealed class TrackedInvoker : HttpMessageInvoker
    {
        private readonly RecordingTransportFactory _owner;

        internal TrackedInvoker(HttpMessageHandler handler, RecordingTransportFactory owner)
            : base(handler, disposeHandler: false)
        {
            _owner = owner;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.OnDisposed();
            }

            base.Dispose(disposing);
        }
    }
}
