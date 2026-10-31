using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// One gateway's resources, the rules for adding them, and whether the model has been built. The
// model builder owns one, and each resource definition reaches it to look up the resources its
// dependencies and settings name.
[DebuggerDisplay("{Owner.Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationSetComposition
{
    private readonly List<ApplicationResourceDefinition> _resources = [];

    public ApplicationSetComposition(IApplicationSet owner)
    {
        Owner = owner;
        Resources = _resources.AsReadOnly();
    }

    public IApplicationSet Owner { get; }

    // The parent gateway the model joins, once named.
    public GatewayName? Parent { get; set; }

    public IReadOnlyCollection<IApplicationResourceDefinition> Resources { get; }

    // Every resource by provider and name, so a setting can find the resource its output belongs to.
    public Dictionary<(ProviderName Provider, ResourceName Name), ApplicationResourceDefinition> Index { get; } = [];

    public bool IsBuilt { get; private set; }

    public void MarkBuilt()
    {
        IsBuilt = true;
    }

    public void ThrowIfBuilt()
    {
        if (IsBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationCompositionClosed);
        }
    }

    public IApplicationResourceDefinition AddResource(ProviderName provider, ResourceName name)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        ThrowIfBuilt();

        if (Index.ContainsKey((provider, name)))
        {
            throw new InvalidOperationException(Format(ErrorMessages.ApplicationResourceDuplicate, Owner.Name, provider, name));
        }

        var definition = new ApplicationResourceDefinition(this, provider, name);
        _resources.Add(definition);
        Index.Add((provider, name), definition);
        return definition;
    }

    public ApplicationManifest GetManifest()
    {
        var resources = new ApplicationResourceManifest[_resources.Count];
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index] = _resources[index].ToManifest();
        }

        return new ApplicationManifest(Owner.Name, Parent, Array.AsReadOnly(resources));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationSetComposition _composition;

        public DebugView(ApplicationSetComposition composition)
        {
            _composition = composition;
        }

        public IApplicationSet Owner => _composition.Owner;

        public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _composition.Resources;
    }
}
