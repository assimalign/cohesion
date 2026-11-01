namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The one operation an invocation of an action requests.
/// </summary>
/// <remarks>
/// An action declares what it accepts with <see cref="ApplicationActionKind"/>, a set of operations;
/// an invocation requests exactly one. Each operation has the value of the
/// <see cref="ApplicationActionKind"/> member of the same name, so an action accepts an operation
/// when <c>kind.HasFlag((ApplicationActionKind)operation)</c>. There is no delete operation: a
/// resource is never deleted through its control plane.
/// </remarks>
public enum ApplicationActionOperation
{
    /// <summary>Observes the instance without changing it.</summary>
    Read = 1,

    /// <summary>Changes the instance.</summary>
    Write = 2,
}
