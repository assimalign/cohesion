using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlHarnessValidationTests
{
    [Fact(DisplayName = "Cohesion Test [SqlClient] - Harness: an injected engine rejects separate engine configuration (#1788)")]
    public async Task Start_WithEngineAndConfiguration_ShouldRefuse()
    {
        // Arrange
        int calls = 0;
        await using var engine = SqlDatabaseEngine.Create("harness-validation", new());

        // Act
        var failure = await Should.ThrowAsync<ArgumentException>(() => SqlClientTestHarness.StartAsync(
            configureEngine: _ => calls++, engine: engine));

        // Assert
        failure.ParamName.ShouldBe("engine");
        calls.ShouldBe(0);
    }
}
