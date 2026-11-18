using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What one platform contributed when it was configured: its extensions, its platform-native
/// providers, and its fault classifiers, checked for duplicates.
/// </summary>
/// <remarks>
/// The ResourceManager and the AppHost each create one registration for the platform they
/// compile in. The registration is complete when <see cref="Create"/> returns; a builder the
/// platform kept refuses later registrations.
/// </remarks>
[DebuggerDisplay("{Name,nq} {ContractVersion,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class FabricPlatformRegistration
{
    private readonly List<IFabricExtension> _extensions = [];
    private readonly List<IFabricResourceProvider> _resourceProviders = [];
    private readonly List<IFabricFaultClassifier> _faultClassifiers = [];

    private FabricPlatformRegistration(IFabricPlatform platform)
    {
        Platform = platform;
    }

    /// <summary>
    /// Gets the platform.
    /// </summary>
    public IFabricPlatform Platform { get; }

    /// <summary>
    /// Gets the platform's name.
    /// </summary>
    public PlatformName Name => Platform.Name;

    /// <summary>
    /// Gets the platform's contract version.
    /// </summary>
    public ApiVersion ContractVersion => Platform.ContractVersion;

    /// <summary>
    /// Gets the typed platform extensions, in registration order.
    /// </summary>
    public IReadOnlyList<IFabricExtension> Extensions => _extensions;

    /// <summary>
    /// Gets the platform-native providers, in registration order.
    /// </summary>
    public IReadOnlyList<IFabricResourceProvider> ResourceProviders => _resourceProviders;

    /// <summary>
    /// Gets the fault classifiers, in registration order.
    /// </summary>
    public IReadOnlyList<IFabricFaultClassifier> FaultClassifiers => _faultClassifiers;

    /// <summary>
    /// Configures a platform and records what it registered.
    /// </summary>
    /// <param name="platform">The platform.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="platform"/> or a registered component is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the platform declares no name or contract version, registers a resource type or an extension host type twice, registers another platform's extension, or registers after configuration ended.</exception>
    public static FabricPlatformRegistration Create(IFabricPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (platform.Name == default)
        {
            throw new InvalidOperationException(ErrorMessages.PlatformDefaultName);
        }

        if (platform.ContractVersion == default)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformDefaultContractVersion, platform.Name));
        }

        var registration = new FabricPlatformRegistration(platform);
        var builder = new Builder(registration);
        platform.Configure(builder);
        builder.Complete();
        return registration;
    }

    private sealed class Builder : IFabricPlatformBuilder
    {
        private readonly FabricPlatformRegistration _registration;
        private readonly HashSet<ResourceTypeName> _resourceTypes = [];
        private readonly HashSet<ResourceTypeName> _extensionHosts = [];
        private bool _completed;

        public Builder(FabricPlatformRegistration registration)
        {
            _registration = registration;
        }

        public void Complete() => _completed = true;

        public IFabricPlatformBuilder AddExtension<THub>(FabricExtension<THub> extension) where THub : class
        {
            ArgumentNullException.ThrowIfNull(extension);
            EnsureOpen();

            if (extension.Platform != _registration.Name)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformExtensionForeign, _registration.Name, extension.Platform));
            }

            if (!_extensionHosts.Add(extension.HostType))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformExtensionDuplicate, _registration.Name, extension.HostType));
            }

            _registration._extensions.Add(extension);
            return this;
        }

        public IFabricPlatformBuilder AddResourceType(IFabricResourceProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            EnsureOpen();

            foreach (var type in provider.Manifest.Types)
            {
                if (!_resourceTypes.Add(type.Type))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformResourceTypeDuplicate, _registration.Name, type.Type));
                }
            }

            _registration._resourceProviders.Add(provider);
            return this;
        }

        public IFabricPlatformBuilder AddFaultClassifier(IFabricFaultClassifier classifier)
        {
            ArgumentNullException.ThrowIfNull(classifier);
            EnsureOpen();

            _registration._faultClassifiers.Add(classifier);
            return this;
        }

        private void EnsureOpen()
        {
            if (_completed)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformRegistrationCompleted, _registration.Name));
            }
        }
    }

    private sealed class DebugView
    {
        private readonly FabricPlatformRegistration _registration;

        public DebugView(FabricPlatformRegistration registration)
        {
            _registration = registration;
        }

        public PlatformName Name => _registration.Name;

        public ApiVersion ContractVersion => _registration.ContractVersion;

        public IReadOnlyList<IFabricExtension> Extensions => _registration.Extensions;

        public IReadOnlyList<IFabricResourceProvider> ResourceProviders => _registration.ResourceProviders;

        public IReadOnlyList<IFabricFaultClassifier> FaultClassifiers => _registration.FaultClassifiers;
    }
}
