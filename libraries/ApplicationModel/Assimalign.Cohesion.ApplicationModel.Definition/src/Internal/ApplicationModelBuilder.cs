using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ApplicationModelBuilder : IApplicationModelBuilder
{
    private readonly ApplicationSetComposition _composition;
    private ApplicationParent? _parent;

    public ApplicationModelBuilder(GatewayName name)
    {
        Name = name;
        _composition = new ApplicationSetComposition(this);
    }

    public GatewayName Name { get; }

    public IApplicationParent? Parent => _parent;

    public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _composition.Resources;

    public IApplicationResourceDefinition AddResource(ProviderName provider, ResourceName name)
    {
        return _composition.AddResource(provider, name);
    }

    public IApplicationParent UseParent(GatewayName name)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayNameMissing, nameof(name));
        }

        if (name == Name)
        {
            throw new ArgumentException(Format(ErrorMessages.ApplicationParentSelf, Name), nameof(name));
        }

        _composition.ThrowIfBuilt();

        if (_parent is not null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.ApplicationParentAlreadySet, Name, _parent.Name));
        }

        _parent = new ApplicationParent(name);
        _composition.Parent = name;
        return _parent;
    }

    public ApplicationManifest GetManifest()
    {
        return _composition.GetManifest();
    }

    public IApplicationModel Build()
    {
        if (_composition.IsBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationAlreadyBuilt);
        }

        // The model validates the declarations as manifest data, so code and manifests follow the
        // same rules; the builder closes only once they pass.
        ApplicationModel model = ApplicationModel.Create(GetManifest());
        _composition.MarkBuilt();
        return model;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationModelBuilder _builder;

        public DebugView(ApplicationModelBuilder builder)
        {
            _builder = builder;
        }

        public GatewayName Name => _builder.Name;

        public IApplicationParent? Parent => _builder.Parent;

        public IReadOnlyCollection<IApplicationResourceDefinition> Resources => _builder.Resources;
    }
}
