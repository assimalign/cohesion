namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("GenericPlannedResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GenericPlannedResource<>.DebugView))]
internal sealed class GenericPlannedResource<TOptions> : PlannedResource
    where TOptions : class, IResourceOptions
{
    public GenericPlannedResource(ResourceManifest manifest, TOptions options)
        : base(manifest, options)
    {
    }

    private sealed class DebugView
    {
        private readonly GenericPlannedResource<TOptions> _target;

        public DebugView(GenericPlannedResource<TOptions> value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.ApplicationModel.ResourceName Name => _target.Name;
        public global::Assimalign.Cohesion.ApplicationModel.ResourceManifest Manifest => _target.Manifest;
        public global::Assimalign.Cohesion.ApplicationModel.IResourceOptions Options => _target.Options;
        public string Artifact => _target.Artifact;
        public global::System.Collections.Generic.IReadOnlyDictionary<string, string> EnvironmentVariables => _target.EnvironmentVariables;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.ApplicationModel.ResourceEndpoint> Endpoints => _target.Endpoints;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.ApplicationModel.ResourceMount> Mounts => _target.Mounts;
    }
}
