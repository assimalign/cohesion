namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Closed and open generic registrations of one service, including an open generic whose
/// constraint <see cref="Argument"/> does not satisfy.
/// </summary>
internal static class GenericServiceFixtures
{
    internal interface IGenericService<T>
    {
    }

    internal interface IConstraint
    {
    }

    internal sealed class Argument
    {
    }

    internal sealed class ClosedGenericService : IGenericService<Argument>
    {
    }

    internal sealed class OpenGenericService<T> : IGenericService<T>
    {
    }

    internal sealed class ConstrainedGenericService<T> : IGenericService<T>
        where T : IConstraint
    {
    }
}
