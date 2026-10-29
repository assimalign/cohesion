namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

/// <summary>
/// The content serializer the tests' control planes and invocations use: the resource contracts'
/// own types plus the test types.
/// </summary>
internal static class TestSerializer
{
    public static Assimalign.Cohesion.ApplicationModel.Hosting.JsonResourceContentSerializer Json { get; } =
        Assimalign.Cohesion.ApplicationModel.Hosting.JsonResourceContentSerializer.Create(TestJsonContext.Default);
}