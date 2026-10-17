using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlIdentityTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - Identity: generated values and coded failures round-trip")]
    public async Task ExecuteAsync_Identity_ShouldGenerateAndKeepConnectionUsableAfterFailure()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE identities (id BIGINT IDENTITY(100,5) PRIMARY KEY)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        await connection.ExecuteAsync("INSERT INTO identities DEFAULT VALUES", cancellationToken: SqlClientTestHarness.Timeout());
        var failure = await Should.ThrowAsync<SqlClientException>(() => connection.ExecuteAsync("INSERT INTO identities VALUES (8)", cancellationToken: SqlClientTestHarness.Timeout()).AsTask());

        // Assert
        failure.Message.ShouldContain("COHSQLE025", Case.Sensitive);
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id FROM identities", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem().GetInt64(0).ShouldBe(100L);
        await connection.ExecuteAsync("INSERT INTO identities DEFAULT VALUES", cancellationToken: SqlClientTestHarness.Timeout());
        (await connection.QueryAsync("SELECT id FROM identities ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout()))[1].GetInt64(0).ShouldBe(105L);
    }
}
