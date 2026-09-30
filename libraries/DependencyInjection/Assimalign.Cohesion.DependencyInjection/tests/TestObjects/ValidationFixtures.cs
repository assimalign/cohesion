namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Services for scope validation: one service type registered with different lifetimes, and a
/// singleton that reaches a scoped service through a transient.
/// </summary>
internal static class ValidationFixtures
{
    internal interface IValidatedService
    {
    }

    internal sealed class ScopedValidatedService : IValidatedService
    {
    }

    internal sealed class SingletonValidatedService : IValidatedService
    {
    }

    internal sealed class ValidatedServiceConsumer
    {
        public ValidatedServiceConsumer(IValidatedService service)
        {
        }
    }

    internal sealed class ScopedDependency
    {
    }

    internal sealed class TransientOverScoped
    {
        public TransientOverScoped(ScopedDependency dependency)
        {
        }
    }

    internal sealed class SingletonOverTransient
    {
        public SingletonOverTransient(TransientOverScoped dependency)
        {
        }
    }
}
