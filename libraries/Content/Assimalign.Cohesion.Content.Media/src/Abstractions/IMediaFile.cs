namespace Assimalign.Cohesion.Files;

/// <summary>
/// 
/// </summary>
public interface IMediaFile : IBinaryFile, IComposableContent
{
    /// <summary>
    /// 
    /// </summary>
    MediaFileKind Kind { get; }
}
