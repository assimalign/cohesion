using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Produces instances of <see cref="OutcomeO{TResult}"/>.
/// </summary>
public static class OutcomeO
{
    /// <summary>
    /// Returns a <see cref="OutcomeO{TResult}"/> with the given <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="value">The result value.</param>
    /// <returns>An instance of <see cref="OutcomeO{TResult}"/>.</returns>
    public static OutcomeO<TResult> FromResult<TResult>(TResult? value) => new(value);

    /// <summary>
    /// Returns a <see cref="OutcomeO{TResult}"/> with the given <paramref name="value"/> wrapped as <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="value">The result value.</param>
    /// <returns>A completed <see cref="ValueTask{TResult}"/> that produces <see cref="OutcomeO{TResult}"/>.</returns>
    public static ValueTask<OutcomeO<TResult>> FromResultAsValueTask<TResult>(TResult value) => new(FromResult(value));

    /// <summary>
    /// Returns a <see cref="OutcomeO{TResult}"/> with the given <paramref name="exception"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="exception">The exception.</param>
    /// <returns>An instance of <see cref="OutcomeO{TResult}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is <see langword="null"/>.</exception>
    public static OutcomeO<TResult> FromException<TResult>(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new(exception);
    }

    /// <summary>
    /// Returns a <see cref="OutcomeO{TResult}"/> with the given <paramref name="exception"/> wrapped as <see cref="ValueTask{TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="exception">The exception.</param>
    /// <returns>A completed <see cref="ValueTask{TResult}"/> that produces <see cref="OutcomeO{TResult}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is <see langword="null"/>.</exception>
    public static ValueTask<OutcomeO<TResult>> FromExceptionAsValueTask<TResult>(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new(FromException<TResult>(exception));
    }

    internal static OutcomeO<VoidResult> Void => FromResult(VoidResult.Instance);

    internal static OutcomeO<VoidResult> FromException(Exception exception) => FromException<VoidResult>(exception);
}
