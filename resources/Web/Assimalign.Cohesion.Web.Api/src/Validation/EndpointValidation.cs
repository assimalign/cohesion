using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Runtime support the source-generated endpoint binding thunk calls to surface validation
/// failures. Not intended to be called directly by application code.
/// </summary>
/// <remarks>
/// Failures are rendered as an RFC 9457 <c>application/problem+json</c> 400 response whose
/// <c>errors</c> extension groups messages by the offending member.
/// </remarks>
public static class EndpointValidation
{
    /// <summary>
    /// Writes an RFC 9457 <c>application/problem+json</c> <c>400 Bad Request</c> response describing
    /// the supplied validation failures, grouping messages under an <c>errors</c> extension keyed by
    /// the offending member.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="result">The validation result whose failures are rendered.</param>
    /// <param name="cancellationToken">A token used to cancel the write.</param>
    /// <returns>A task that completes when the problem response has been written.</returns>
    public static Task WriteProblemAsync(IHttpContext context, ValidationResult result, CancellationToken cancellationToken = default)
    {
        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.BadRequest, "One or more validation errors occurred.");

        Dictionary<string, object?> errors = new();

        foreach (IValidationError error in result.Errors)
        {
            string key = NormalizeSource(error.Source);

            if (errors.TryGetValue(key, out object? existing) && existing is List<string> messages)
            {
                messages.Add(error.Message);
            }
            else
            {
                errors[key] = new List<string> { error.Message };
            }
        }

        problem.Extensions["errors"] = errors;

        return context.Response.WriteProblemDetailsAsync(problem, cancellationToken);
    }

    /// <summary>
    /// Reduces a validation error source to a bare member name. Selector-shaped sources such as
    /// <c>"p =&gt; p.FirstName"</c> collapse to <c>"FirstName"</c>; an empty source becomes <c>"$"</c>.
    /// </summary>
    /// <param name="source">The raw validation error source.</param>
    /// <returns>The normalized member key.</returns>
    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "$";
        }

        string value = source!.Trim();

        int arrow = value.IndexOf("=>", System.StringComparison.Ordinal);
        if (arrow >= 0)
        {
            value = value.Substring(arrow + 2).Trim();
        }

        int lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < value.Length - 1)
        {
            value = value.Substring(lastDot + 1);
        }

        return value.Length == 0 ? "$" : value;
    }
}
