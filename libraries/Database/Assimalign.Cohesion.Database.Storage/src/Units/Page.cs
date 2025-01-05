namespace Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// 
/// </summary>
public abstract class Page
{
    /// <summary>
    /// 
    /// </summary>
    PageId Id { get; }
    /// <summary>
    /// 
    /// </summary>
    PageOrientation Orientation { get; }
    /// <summary>
    /// 
    /// </summary>
    public abstract PageType PageType { get; }
}
