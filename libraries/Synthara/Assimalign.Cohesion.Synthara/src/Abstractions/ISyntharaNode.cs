namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaNode
{
    /// <summary>
    /// 
    /// </summary>
    NodeId Id { get; }

    /// <summary>
    /// 
    /// </summary>
    NodeName Name { get; }

    /// <summary>
    /// 
    /// </summary>
    ISyntharaRegion Region { get; }
}