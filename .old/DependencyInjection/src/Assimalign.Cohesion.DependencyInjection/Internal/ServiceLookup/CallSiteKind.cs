namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal enum CallSiteKind
{
    Factory,
    Constructor,
    Constant,
    Enumerable,
    ServiceProvider,
    Scope,
    Transient,
    Singleton
}