using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>A minimal resource for base-gateway tests.</summary>
internal sealed class TestResource : IApplicationResource
{
    public TestResource(string name)
    {
        Name = name;
    }

    public ResourceName Name { get; }
}

/// <summary>A minimal executable resource for local-gateway tests.</summary>
internal sealed class TestExecutableResource : IExecutableResource
{
    public TestExecutableResource(string name, string artifact)
    {
        Name = name;
        Artifact = artifact;
    }

    public ResourceName Name { get; }

    public string Artifact { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();
}
