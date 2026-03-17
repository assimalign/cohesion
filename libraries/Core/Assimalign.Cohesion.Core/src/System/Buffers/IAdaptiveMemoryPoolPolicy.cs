namespace System.Buffers;

/// <summary>
/// Defines a policy that controls how many idle blocks an <see cref="AdaptiveMemoryPool"/> should retain.
/// </summary>
public interface IAdaptiveMemoryPoolPolicy
{
    /// <summary>
    /// Calculates the maximum number of retained idle blocks for the current pool state.
    /// </summary>
    /// <param name="snapshot">A snapshot describing the current memory pool state.</param>
    /// <returns>The maximum number of idle blocks that should be retained.</returns>
    int GetRetentionLimit(AdaptiveMemoryPoolSnapshot snapshot);
}
