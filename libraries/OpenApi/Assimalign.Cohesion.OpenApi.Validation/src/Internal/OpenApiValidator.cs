using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The default <see cref="IOpenApiValidator"/>. Runs an ordered set of <see cref="IOpenApiValidationRule"/>
/// instances and collects their diagnostics into a single result.
/// </summary>
internal sealed class OpenApiValidator : IOpenApiValidator
{
    private readonly List<IOpenApiValidationRule> _rules;

    public OpenApiValidator(IEnumerable<IOpenApiValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = new List<IOpenApiValidationRule>(rules);
    }

    public OpenApiValidationResult Validate(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var context = new OpenApiValidationContext(document);
        foreach (var rule in _rules)
        {
            rule.Validate(context);
        }

        return new OpenApiValidationResult(context.Diagnostics);
    }
}
