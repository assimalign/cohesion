namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Options for a blob write.
/// </summary>
public sealed class BlobWriteOptions
{
    /// <summary>
    /// Gets or sets the declared media type of the content.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an existing blob with the same
    /// name may be replaced. When false, writing over an existing blob fails.
    /// </summary>
    public bool Overwrite { get; set; } = true;
}
