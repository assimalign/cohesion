using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Validates and copies the name lists the policy model accepts (roles, authentication schemes,
/// allowed claim values), so every public entry point rejects the same malformed input and no caller
/// keeps a reference into a stored list.
/// </summary>
internal static class AuthorizationNames
{
    /// <summary>
    /// Copies <paramref name="names"/> into a new array, rejecting <see langword="null"/> entries and,
    /// unless <paramref name="allowBlank"/> is set, empty or whitespace entries.
    /// </summary>
    /// <param name="names">The names to copy.</param>
    /// <param name="parameterName">The public parameter the names arrived through, for the exception.</param>
    /// <param name="kind">What a name is (for example <c>role</c>), for the exception message.</param>
    /// <param name="allowBlank">Whether empty and whitespace entries are accepted.</param>
    /// <returns>The copy; empty when <paramref name="names"/> is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry is <see langword="null"/>, or blank when blanks are not allowed.</exception>
    public static string[] Copy(IEnumerable<string> names, string parameterName, string kind, bool allowBlank = false)
    {
        ArgumentNullException.ThrowIfNull(names, parameterName);

        List<string> copy = new();

        foreach (string name in names)
        {
            if (name is null || (!allowBlank && string.IsNullOrWhiteSpace(name)))
            {
                throw new ArgumentException(
                    allowBlank ? $"A {kind} must not be null." : $"A {kind} must not be null, empty, or whitespace.",
                    parameterName);
            }

            copy.Add(name);
        }

        return copy.ToArray();
    }
}
