namespace Assimalign.Cohesion.Files;

/// <summary>
/// 
/// </summary>
public interface IMediaFile : IBinaryFile, IComposableFile
{
    /// <summary>
    /// 
    /// </summary>
    MediaFileKind Kind { get; }
}
