using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Validation.Internal;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Composes the application's request validation: the <see cref="EndpointValidationOptions"/> (the
/// validator for each model type, and whether bound values are validated by default) that typed endpoints
/// and <c>context.ValidateAsync(value)</c> read.
/// </summary>
/// <remarks>
/// <para>
/// Applications receive one in the <c>builder.Services.AddValidation(validation => ...)</c> callback and
/// register validators on <see cref="Options"/>, for example
/// <c>validation => validation.Options.AddProfile(new CustomerProfile())</c>. That verb is a component
/// integration (<c>Properties/ComponentIntegrations.cs</c>, owner decisions 34 and 39): it creates the
/// builder, runs the optional callback, calls <see cref="Build"/>, and registers the result as an
/// <see cref="IHttpFeature"/> singleton. A composition surface without a service container registers
/// <see cref="Build"/>'s result through <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// Called bare, <c>AddValidation()</c> registers validation enabled by default with no validator, which
/// validates nothing: a value whose type has no registered validator is not validated. It compiles because
/// every builder-template callback is optional (owner decision 39); register the validators in the
/// callback.
/// </para>
/// </remarks>
public sealed class ValidationBuilder
{
    private readonly EndpointValidationOptions _options = new();

    /// <summary>
    /// Initializes a builder with validation enabled by default and no validators.
    /// </summary>
    public ValidationBuilder()
    {
    }

    /// <summary>
    /// Gets the validation options being composed.
    /// </summary>
    /// <remarks>
    /// <see cref="Build"/> reads them once; later changes are not observed by the feature it built.
    /// </remarks>
    public EndpointValidationOptions Options => _options;

    /// <summary>
    /// Builds the validation feature from a snapshot of <see cref="Options"/>.
    /// </summary>
    /// <remarks>
    /// Registering the feature again replaces the earlier registration: an exchange carries one.
    /// </remarks>
    /// <returns>The validation feature, an <see cref="IHttpFeature"/> for the application to register.</returns>
    public IHttpFeature Build()
    {
        return new EndpointValidationFeature(_options.Enabled, _options.Validators);
    }
}
