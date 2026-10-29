namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// The content serializer the tests' control planes use: this library's types plus the test types,
/// which keep <see cref="TestJsonContext"/>'s Pascal-case names.
/// </summary>
internal static class TestSerializer
{
    public static JsonResourceContentSerializer Json { get; } = JsonResourceContentSerializer.Create(TestJsonContext.Default);
}
