namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Describes how a management write changes a section's override.</summary>
public enum ResourceOverrideChangeKind
{
    /// <summary>Merges an RFC 7396 merge patch into the stored override; a null member is kept as a tombstone (PATCH).</summary>
    Merge,

    /// <summary>Replaces the section's override with the patch (PUT).</summary>
    Replace,

    /// <summary>Removes the section's override, so the code value returns (DELETE).</summary>
    Remove,
}
