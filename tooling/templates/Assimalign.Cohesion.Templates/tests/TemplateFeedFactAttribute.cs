using Xunit;

namespace Assimalign.Cohesion.Templates.Tests;

/// <summary>Skips only the package-backed assertion when its exact SDK and package closure is unavailable.</summary>
// Deviates from the repo interface-first rule per work-item requirements: xUnit v2 discovers FactAttribute subclasses.
public sealed class TemplateFeedFactAttribute : FactAttribute
{
    /// <summary>Checks the package requirements for the named template at test discovery.</summary>
    /// <param name="template">The exact dotnet new short name.</param>
    public TemplateFeedFactAttribute(string template) => Skip = TemplateRepository.MissingFeedReason(template);
}
