using System;

using Assimalign.Cohesion.ConfigurationStore.Hosting;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ConfigurationStore.Tests;

public sealed class NamespaceDeclarationTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - AddNamespace: captures one fluent declaration and rejects duplicates")]
    public void AddNamespace_WithValidDeclaration_ShouldReturnBuilderAndRejectDuplicate()
    {
        IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder([]);

        IConfigurationStoreApplicationBuilder returned = builder.AddNamespace(
            "app",
            ns => ns.Set("Mode", "production").Set("Optional", null));

        returned.ShouldBeSameAs(builder);
        Should.Throw<InvalidOperationException>(() =>
            builder.AddNamespace("app", ns => ns.Set("Mode", "development")));
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore] - AddNamespace: rejects blank names, keys, and null callbacks")]
    public void AddNamespace_WithInvalidDeclaration_ShouldRejectInput()
    {
        IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder([]);

        Should.Throw<ArgumentException>(() => builder.AddNamespace(" ", _ => { }));
        Should.Throw<ArgumentNullException>(() => builder.AddNamespace("app", null!));
        Should.Throw<ArgumentException>(() => builder.AddNamespace("app", ns => ns.Set(" ", "value")));
        Should.Throw<ArgumentException>(() => builder.AddNamespace("other", ns => ns.Set("section/key", "value")));
    }
}
