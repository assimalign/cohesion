using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

/// <summary>
/// A hand-written validator with no profiles, the shape <c>AddValidator&lt;T&gt;</c> exists for: it cannot
/// say which type it validates, so the registration names it.
/// </summary>
internal sealed class ProfilelessValidator : IValidator
{
    public IEnumerable<IValidationProfile> Profiles => [];

    public ValidationResult Validate<T>(T instance) => throw new NotSupportedException();

    public Task<ValidationResult> ValidateAsync<T>(T instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValidationResult Validate(IValidationContext context) => throw new NotSupportedException();

    public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
