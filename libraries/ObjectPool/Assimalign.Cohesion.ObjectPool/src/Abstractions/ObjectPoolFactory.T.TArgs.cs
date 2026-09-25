namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// Provides a base class for creating object pools that instantiate objects of type T using arguments of type TArgs.
/// </summary>
/// <remarks>Implementations of this class define how objects are created and may provide pooling or reuse
/// strategies. This class is intended to be subclassed to provide custom pooling behavior for specific object
/// types.</remarks>
/// <typeparam name="T">The type of objects to be created and managed by the pool. Must be a reference type.</typeparam>
/// <typeparam name="TArgs">The type of arguments required to create instances of T.</typeparam>
public abstract class ObjectPoolFactory<T, TArgs> where T : class
{
    /// <summary>
    /// Creates a new instance of type T using the specified arguments.
    /// </summary>
    /// <param name="args">The arguments used to initialize the new instance. The requirements and interpretation of these arguments depend
    /// on the implementation.</param>
    /// <returns>A new instance of type T initialized with the provided arguments.</returns>
    public abstract T Create(TArgs args);
}
