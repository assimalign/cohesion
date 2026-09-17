using System.Reflection;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal sealed record InProcessResourceArtifact(
    ResourceId Resource,
    Assembly EntryAssembly,
    string ContentRootPath) : IResourceArtifact;
