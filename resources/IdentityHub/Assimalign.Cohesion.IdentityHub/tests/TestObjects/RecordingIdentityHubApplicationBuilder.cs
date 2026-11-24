using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A builder that keeps what it is given, so a test can read what an extension verb handed to
/// the seam without composing an application.
/// </summary>
internal sealed class RecordingIdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    public List<IdentityUser> Users { get; } = [];

    public List<IdentityGroup> Groups { get; } = [];

    public IIdentityHubApplicationBuilder AddAudience(string audience)
    {
        throw new NotSupportedException();
    }

    public IIdentityHubApplicationBuilder AddClient(string clientId, Action<IdentityHubClientOptions> configure)
    {
        throw new NotSupportedException();
    }

    public IIdentityHubApplicationBuilder AddStore(IIdentityHubStore store)
    {
        throw new NotSupportedException();
    }

    public IIdentityHubApplicationBuilder AddStore(Func<IIdentityHubApplicationContext, IIdentityHubStore> factory)
    {
        throw new NotSupportedException();
    }

    public IIdentityHubApplicationBuilder AddUser(IdentityUser user)
    {
        Users.Add(user);
        return this;
    }

    public IIdentityHubApplicationBuilder AddGroup(IdentityGroup group)
    {
        Groups.Add(group);
        return this;
    }

    public IIdentityHubApplication Build()
    {
        throw new NotSupportedException();
    }
}
