namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// Entry point to the built-in <see cref="IProblemDetailsWriter"/>.
/// </summary>
public static class ProblemDetailsWriter
{
    /// <summary>
    /// Gets the shared, stateless default writer that renders <see cref="ProblemDetails"/> as
    /// AOT-safe <c>application/problem+json</c>.
    /// </summary>
    public static IProblemDetailsWriter Default => ProblemDetailsJsonWriter.Instance;
}
