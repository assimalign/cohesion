using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Validation.Internal;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Validates values bound for the current request with the validators <c>AddValidation</c> registered,
/// answering an invalid one with <c>400 Bad Request</c> as <c>application/problem+json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated typed endpoints call <see cref="ValidateAsync{T}"/> for the request-body model they
/// bind, after every parameter is bound and before the handler runs, whenever the application references
/// this package; a handler that writes its own binding can call it the same way. Nothing is validated
/// unless the application called <c>AddValidation</c> and registered a validator for the value's type.
/// </para>
/// <para>
/// The <c>400</c> carries the RFC 9457 <c>errors</c> extension member in the shape binding failures use:
/// a map from the failing member to its messages. A rule's default source, its member selector
/// (<c>p =&gt; p.Address.City</c>), is reported as the member path (<c>Address.City</c>), the CLR member
/// names as the profile declares them; a source the profile set is reported as written, and an error with
/// no source under the empty key.
/// </para>
/// </remarks>
public static class HttpContextValidationExtensions
{
    private const string problemDetail = "One or more validation errors occurred.";

    extension(IHttpContext context)
    {
        /// <summary>
        /// Validates <paramref name="value"/>, bound for the current request as <typeparamref name="T"/>,
        /// and answers the request with <c>400 Bad Request</c> when it is invalid.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The value is validated when the application registered validation (<c>AddValidation</c>), the
        /// matched endpoint requires it — its <see cref="ValidationMetadata"/>, or the application's
        /// default (<see cref="EndpointValidationOptions.Enabled"/>) when it declares none — and a validator
        /// is registered for <typeparamref name="T"/>. Otherwise, and for a <see langword="null"/> value,
        /// the request proceeds unvalidated.
        /// </para>
        /// <para>
        /// When the value is invalid, the response is written as <c>application/problem+json</c> with an
        /// <c>errors</c> map, and the caller must not write it again: return without running the handler.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type the value is bound as, which selects the validator.</typeparam>
        /// <param name="value">The bound value.</param>
        /// <param name="cancellationToken">A token that cancels writing the <c>400</c> response.</param>
        /// <returns>
        /// <see langword="true"/> when the request may proceed; <see langword="false"/> when the value was
        /// invalid and the <c>400</c> response has been written.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public async ValueTask<bool> ValidateAsync<T>(T value, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (value is null || context.Features.Get<EndpointValidationFeature>() is not { } feature)
            {
                return true;
            }

            bool required = context.GetEndpointMetadata<ValidationMetadata>()?.RequiresValidation ?? feature.Enabled;

            if (!required || !feature.TryGetValidator(typeof(T), out IValidator? validator))
            {
                return true;
            }

            Dictionary<string, object?> errors;

            try
            {
                ValidationResult result = validator.Validate(value);

                if (result.IsValid)
                {
                    return true;
                }

                errors = ValidationErrorMap.Create(result.Errors);
            }
            catch (ValidationFailureException)
            {
                // A validator built with ThrowExceptionOnFailure reports a failure by throwing, and the
                // exception carries none of the errors: the value is still invalid.
                errors = new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.BadRequest, problemDetail);
            problem.Extensions["errors"] = errors;

            await context.Response.WriteProblemDetailsAsync(problem, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}
