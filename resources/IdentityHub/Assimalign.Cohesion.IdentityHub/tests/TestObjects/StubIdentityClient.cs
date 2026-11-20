using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A registration that is not an <see cref="IdentityClient"/>: whatever a test sets, checked by
/// nothing. A registry has to copy one through its own validation.
/// </summary>
internal sealed class StubIdentityClient : IIdentityClient
{
    public IdentityClientId Id { get; set; }

    public string DisplayName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsLocked { get; set; }

    public IdentityHubRecordVersion? Version { get; set; }

    public bool IsConfidential => Credentials.Count > 0;

    public IdentityClientGrants Grants { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    public IReadOnlyList<IdentityClientRedirect> Redirects { get; set; } = [];

    public IReadOnlyList<IdentityClientCredential> Credentials { get; set; } = [];

    public IReadOnlyList<IdentityClientAudience> Audiences { get; set; } = [];

    public IReadOnlyList<IdentityClientScope> Scopes { get; set; } = [];

    public IReadOnlyList<IdentityClientRole> Roles { get; set; } = [];

    public IReadOnlyList<IdentityClientPermission> Permissions { get; set; } = [];
}
