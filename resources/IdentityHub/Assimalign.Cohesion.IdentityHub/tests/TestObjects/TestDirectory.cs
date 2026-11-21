using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The users, groups and directories the directory tests share.
/// </summary>
internal static class TestDirectory
{
    public static IdentityUser User(string name, string? displayName = null, IdentityUserId id = default)
    {
        return IdentityUser.Create(new() { Id = id, Name = name, DisplayName = displayName });
    }

    public static IdentityGroup Group(string name, string? displayName = null, IdentityGroupId id = default)
    {
        return IdentityGroup.Create(new() { Id = id, Name = name, DisplayName = displayName });
    }

    public static IdentityHubDirectory WithUsers(IIdentityHubStore store, params IdentityUser[] users)
    {
        var descriptor = new IdentityHubDirectoryDescriptor();
        foreach (IdentityUser user in users)
        {
            descriptor.Users.Add(user);
        }

        return IdentityHubDirectory.Create(store, descriptor);
    }

    public static IdentityHubDirectory WithGroups(IIdentityHubStore store, params IdentityGroup[] groups)
    {
        var descriptor = new IdentityHubDirectoryDescriptor();
        foreach (IdentityGroup group in groups)
        {
            descriptor.Groups.Add(group);
        }

        return IdentityHubDirectory.Create(store, descriptor);
    }

    public static async Task<List<TObject>> ListAllAsync<TObject, TId>(
        IIdentityObjectRegistry<TObject, TId> registry,
        int limit)
        where TObject : class
        where TId : struct
    {
        var all = new List<TObject>();
        string? continuation = null;
        int pages = 0;
        do
        {
            IdentityObjectPage<TObject> page = await registry.ListAsync(limit, continuation, CancellationToken.None);
            all.AddRange(page.Items);
            continuation = page.Continuation;
        }
        while (continuation is not null && ++pages < 1000);

        return all;
    }
}
