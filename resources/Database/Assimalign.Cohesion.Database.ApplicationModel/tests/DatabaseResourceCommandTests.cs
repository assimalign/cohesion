using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

public sealed class DatabaseResourceCommandTests
{
    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Commands: typed descriptors emit canonical source-generated payloads")]
    public void AddDatabaseAndPrincipal_WithTypedDescriptor_ShouldRecordWireCommands()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IDatabaseResourceDescriptor descriptor = builder.AddDatabase(DatabaseManifestFactory.Create());

        // Act
        IDatabaseResourceDescriptor returned = descriptor.AddDatabase("orders", "sql")
            .AddPrincipal("orders", "reader", optional: true);

        // Assert
        returned.ShouldBeSameAs(descriptor);
        descriptor.Commands.Count.ShouldBe(2);
        descriptor.Commands[0].Kind.ShouldBe("database.add-database");
        descriptor.Commands[0].Key.ShouldBe("sql/orders");
        descriptor.Commands[0].Owner.ShouldBe((ApplicationName)"appa");
        descriptor.Commands[0].Target.ShouldBeSameAs(descriptor.Resource);
        Encoding.UTF8.GetString(descriptor.Commands[0].Payload.Span).ShouldBe("{\"database\":\"orders\",\"engine\":\"sql\"}");
        descriptor.Commands[1].Kind.ShouldBe("database.add-principal");
        descriptor.Commands[1].Key.ShouldBe("orders/reader");
        descriptor.Commands[1].Optional.ShouldBeTrue();
        Encoding.UTF8.GetString(descriptor.Commands[1].Payload.Span).ShouldBe("{\"database\":\"orders\",\"name\":\"reader\"}");
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - RemoteReferenceDatabase: retains typed commands and graph identity")]
    public void RemoteReferenceDatabase_WithManifest_ShouldRetainTypedCommands()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        ResourceManifest manifest = DatabaseManifestFactory.Create("remote", "provider");
        var declaration = new ExternalResourceDeclaration(manifest.Name, manifest.Application, ["admin"], false, manifest);

        // Act
        IDatabaseResourceDescriptor remote = builder.RemoteReferenceDatabase(declaration,
            options => options.Gateway("https://provider.example"));
        IDatabaseResourceDescriptor rebound = builder.RemoteReferenceDatabase(declaration,
            options => options.Gateway("https://other.example"));
        remote.AddDatabase("orders");

        // Assert
        rebound.Resource.ShouldBeSameAs(remote.Resource);
        rebound.Commands[0].ShouldBeSameAs(remote.Commands[0]);
        remote.Commands[0].Owner.ShouldBe((ApplicationName)"appa");
        Encoding.UTF8.GetString(remote.Commands[0].Payload.Span).ShouldBe("{\"database\":\"orders\"}");
        remote.Commands[0].Key.ShouldBe("orders");
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - AddDatabase: engine-specific declarations have separate ownership keys")]
    public void AddDatabase_WithSameNameOnDifferentEngines_ShouldKeepIndependentKeys()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IDatabaseResourceDescriptor descriptor = builder.AddDatabase(DatabaseManifestFactory.Create());

        // Act
        descriptor.AddDatabase("orders", "sql").AddDatabase("orders", "documents");

        // Assert
        descriptor.Commands[0].Key.ShouldBe("sql/orders");
        descriptor.Commands[1].Key.ShouldBe("documents/orders");
        descriptor.Commands[0].Id.ShouldNotBe(descriptor.Commands[1].Id);
    }

    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Commands: database names cannot alias engine ownership keys")]
    public void AddDatabase_WithSlashInName_ShouldRejectAmbiguousOwnershipKey()
    {
        // Arrange
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", []);
        IDatabaseResourceDescriptor descriptor = builder.AddDatabase(DatabaseManifestFactory.Create());

        // Act
        descriptor.AddDatabase("orders", "group/sql");
        ArgumentException database = Should.Throw<ArgumentException>(() => descriptor.AddDatabase("sql/orders", "group"));
        ArgumentException principal = Should.Throw<ArgumentException>(() => descriptor.AddPrincipal("sql/orders", "reader"));

        // Assert
        database.ParamName.ShouldBe("name");
        principal.ParamName.ShouldBe("database");
        descriptor.Commands.Count.ShouldBe(1);
        descriptor.Commands[0].Key.ShouldBe("group/sql/orders");
    }
}
