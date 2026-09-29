namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a non-gating manifest-version mismatch observed while importing an external.
/// </summary>
/// <param name="Resource">The external resource whose manifests differ.</param>
/// <param name="ExpectedHash">The canonical hash embedded in the consuming closure.</param>
/// <param name="ObservedHash">The canonical hash exported by the provider.</param>
/// <param name="ExpectedSchemaVersion">The export schema version understood by the consumer.</param>
/// <param name="ObservedSchemaVersion">The provider's export schema version.</param>
public sealed record ManifestDrift(
    ResourceName Resource,
    string ExpectedHash,
    string ObservedHash,
    int ExpectedSchemaVersion,
    int ObservedSchemaVersion)
{
    /// <summary>Gets a stable machine-readable diagnostic code.</summary>
    public string Code => nameof(ManifestDrift);

    /// <summary>Gets an actionable human-readable warning.</summary>
    public string Message =>
        $"{Code}: external '{Resource}' expected manifest {ExpectedHash} " +
        $"(export schema {ExpectedSchemaVersion}) but observed {ObservedHash} " +
        $"(export schema {ObservedSchemaVersion}). Referenced endpoints remain compatible.";
}
