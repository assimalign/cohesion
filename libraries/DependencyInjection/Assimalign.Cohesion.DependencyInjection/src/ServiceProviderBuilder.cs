using System;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Default implementation of <see cref="IServiceProviderBuilder"/>.
/// </summary>
public sealed class ServiceProviderBuilder : IServiceProviderBuilder, IDisposable
{
    private readonly ServiceContainer _services = new();
    private readonly ServiceProviderOptions _options = ServiceProviderOptions.Default;

    private bool _isDisposed;

    /// <summary>
    /// Initializes a new <see cref="ServiceProviderBuilder"/> using <see cref="ServiceProviderOptions.Default"/>.
    /// </summary>
    public ServiceProviderBuilder()
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ServiceProviderBuilder"/> using the provided options.
    /// </summary>
    /// <param name="options">The options applied to the <see cref="IServiceProvider"/> this builder creates.</param>
    public ServiceProviderBuilder(ServiceProviderOptions options)
    {
        _options = ArgumentNullException.ThrowIfNull<ServiceProviderOptions>(options);
    }

    /// <inheritdoc />
    public IServiceContainer Services
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            return _services;
        }
    }

    /// <inheritdoc />
    public IServiceProviderBuilder Add(ServiceDescriptor serviceDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serviceDescriptor);

        Services.Register(serviceDescriptor);

        return this;
    }
    IServiceProvider IServiceProviderBuilder.Build()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        return new ServiceProvider(_services, _options);
    }

    /// <summary>
    /// Releases the registration state held by this builder.
    /// </summary>
    /// <remarks>
    /// The builder does not own the <see cref="IServiceProvider"/> instances it creates: every call to
    /// <see cref="IServiceProviderBuilder.Build"/> hands a new provider to the caller, whose lifetime the caller
    /// owns. A provider copies the descriptors it needs when it is built, so disposing the builder never affects
    /// a provider that was already built. Disposal therefore only releases the descriptors this builder is
    /// holding, and is safe to call more than once.
    /// </remarks>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _services.Clear();
    }
}
