namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The index names every kind of directory object shares.
/// </summary>
internal static class IdentityObjectIndexes
{
    /// <summary>
    /// The unique index of an object's name, in lowercase.
    /// </summary>
    internal static IdentityHubRecordIndexName Name { get; } = new("name");
}
