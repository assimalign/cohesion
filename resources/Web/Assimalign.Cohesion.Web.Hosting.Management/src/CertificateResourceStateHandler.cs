using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Management.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Replaces the certificate selected for new TLS handshakes through versioned resource state.</summary>
/// <remarks>Existing connections retain their TLS session. Dispose this adapter after its listeners stop; resolved certificates remain owned until then.</remarks>
[DebuggerDisplay("CertificateResourceStateHandler")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class CertificateResourceStateHandler : IResourceStateHandler, IDisposable
{
    private readonly Lock _sync = new();
    private readonly IResourceCertificateResolver _resolver;
    private readonly ResourceStateDefinition _definition;
    private readonly List<X509Certificate2> _retired = new();
    private ResourceStateDeclaration? _initialState;
    private X509Certificate2 _certificate;
    private ResourceCertificateReference? _reference;
    private Guid _authority;
    private long _revision;
    private string _digest = string.Empty;
    private bool _ownsCurrent;
    private bool _disposed;

    private CertificateResourceStateHandler(ResourceStateDefinition definition, IResourceCertificateResolver resolver, X509Certificate2 initialCertificate)
    {
        _definition = definition;
        _resolver = resolver;
        _certificate = initialCertificate;
    }

    /// <inheritdoc />
    public ResourceStateDefinition Definition => _definition;

    /// <inheritdoc />
    public IReadOnlyList<ResourceStateDeclaration> InitialState => _initialState is null ? [] : [_initialState];

    /// <summary>Creates a runtime-discovered adapter with an optional immutable startup certificate reference.</summary>
    /// <param name="resolver">The authorized exact-version certificate provider.</param>
    /// <param name="initialCertificate">The configured listener certificate, borrowed from its owner.</param>
    /// <param name="initialReference">The reproducible configured certificate identity; private key bytes are never published.</param>
    /// <returns>The runtime state adapter.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The startup certificate is invalid.</exception>
    public static CertificateResourceStateHandler Create(IResourceCertificateResolver resolver, X509Certificate2 initialCertificate,
        ResourceCertificateReference? initialReference = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(initialCertificate);
        if (!initialCertificate.HasPrivateKey) { throw new ArgumentException(ErrorMessages.CertificateContractInvalid, nameof(initialCertificate)); }
        var handler = new CertificateResourceStateHandler(BuiltInStateDefinitions.Load("web.tls.certificate"), resolver, initialCertificate);
        handler._reference = initialReference;
        if (initialReference is not null)
        {
            handler._initialState = new ResourceStateDeclaration("web.tls.certificate", "https", new ResourceStateValue(true,
                JsonSerializer.SerializeToElement(new CertificateStateValue(initialReference), ManagementJsonContext.Default.CertificateStateValue)));
        }
        return handler;
    }

    /// <summary>Configures a listener to select this adapter's current certificate for every new handshake.</summary>
    /// <param name="options">The existing listener TLS options to configure.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="InvalidOperationException">The listener already has a certificate selection callback.</exception>
    public void ConfigureTls(TlsServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); }
        if (options.AuthenticationOptions.ServerCertificateSelectionCallback is not null) { throw new InvalidOperationException(ErrorMessages.CertificateCallbackConfigured); }
        options.AuthenticationOptions.ServerCertificateSelectionCallback = (_, _) => Volatile.Read(ref _certificate);
    }

    /// <inheritdoc />
    public ValueTask<ResourceStateValidation> ValidateAsync(ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ResourceCertificateReference? reference = JsonSerializer.Deserialize(desired.Value, ManagementJsonContext.Default.CertificateStateValue)?.CertificateRef;
            bool valid = desired.Exists && key.Value == "https" && reference is not null;
            return ValueTask.FromResult(new ResourceStateValidation(valid, valid ? null : "certificate-reference-invalid"));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return ValueTask.FromResult(new ResourceStateValidation(false, "certificate-reference-invalid")); }
    }

    /// <inheritdoc />
    public ValueTask<ResourceStateObservation> ReadAsync(ResourceStateKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            JsonElement value = JsonSerializer.SerializeToElement(new CertificateStateValue(_reference), ManagementJsonContext.Default.CertificateStateValue);
            return ValueTask.FromResult(new ResourceStateObservation(new ResourceStateValue(true, value), _revision, _digest, Guid.Empty));
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceStateApplyResult> ApplyAsync(ResourceStateApplyContext context, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default)
    {
        ResourceStateValidation validation = await ValidateAsync(key, desired, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid) { return new ResourceStateApplyResult(ResourceStateApplyStatus.Rejected, 0, validation.ErrorCode); }
        lock (_sync)
        {
            ResourceStateApplyResult? prior = CheckVersion(context);
            if (prior is not null) { return prior; }
        }
        ResourceCertificateReference reference = JsonSerializer.Deserialize(desired.Value, ManagementJsonContext.Default.CertificateStateValue)!.CertificateRef!;
        X509Certificate2 candidate = await _resolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        bool retained = false;
        try
        {
            DateTime now = DateTime.UtcNow;
            if (!candidate.HasPrivateKey || candidate.NotBefore.ToUniversalTime() > now || candidate.NotAfter.ToUniversalTime() <= now)
            {
                return new ResourceStateApplyResult(ResourceStateApplyStatus.Rejected, 0, "certificate-bundle-invalid");
            }
            lock (_sync)
            {
                ResourceStateApplyResult? prior = CheckVersion(context);
                if (prior is not null) { return prior; }
                if (_ownsCurrent) { _retired.Add(_certificate); }
                _reference = reference;
                _authority = context.ResourceIncarnation;
                _revision = context.Revision;
                _digest = context.ValueDigest;
                _ownsCurrent = true;
                Volatile.Write(ref _certificate, candidate);
                retained = true;
                return new ResourceStateApplyResult(ResourceStateApplyStatus.Applied, _revision);
            }
        }
        finally { if (!retained) { candidate.Dispose(); } }
    }

    private ResourceStateApplyResult? CheckVersion(ResourceStateApplyContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_authority != Guid.Empty && _authority != context.ResourceIncarnation) { return new ResourceStateApplyResult(ResourceStateApplyStatus.Conflict, _revision); }
        if (context.Revision < _revision) { return new ResourceStateApplyResult(ResourceStateApplyStatus.Stale, _revision); }
        return context.Revision == _revision ? new ResourceStateApplyResult(context.ValueDigest == _digest ? ResourceStateApplyStatus.AlreadyCurrent : ResourceStateApplyStatus.Conflict, _revision) : null;
    }

    /// <summary>Disposes resolved certificates after listeners and their in-flight handshakes have stopped.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            if (_ownsCurrent) { _certificate.Dispose(); }
            foreach (X509Certificate2 retired in _retired) { retired.Dispose(); }
            _retired.Clear();
        }
    }

    private sealed class DebugView
    {
        private readonly CertificateResourceStateHandler _target;
        public DebugView(CertificateResourceStateHandler target) { _target = target; }
        public ResourceStateDefinition Definition => _target.Definition;
    }
}
