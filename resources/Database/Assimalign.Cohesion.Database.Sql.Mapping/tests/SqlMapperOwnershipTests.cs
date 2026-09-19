using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Proves typed DML leaves code-first object ownership with the retained schema.</summary>
public sealed class SqlMapperOwnershipTests
{
    /// <summary>Normal mapper saves do not grant an ownership bypass to the connection or its pooled session.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Ownership: mapper save preserves schema DDL protection")]
    [InlineData("ALTER TABLE mapper_children ADD COLUMN extra INT")]
    [InlineData("ALTER TABLE mapper_children DROP COLUMN Note")]
    [InlineData("DROP TABLE mapper_children")]
    [InlineData("DROP INDEX IX_mapper_children_Name ON mapper_children")]
    public async Task SaveChangesAsync_SchemaOwnedTable_ShouldPreserveDdlProtection(string forbiddenDdl)
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        SqlMapping.Register(work, new MapperParentMapper()).Add(new MapperParent { Id = 1 });
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var child = new MapperChild { Id = 10, ParentId = 1, Name = "before", Note = "retained" };
        children.Add(child);
        await work.SaveChangesAsync(timeout.Token);

        // Act: even the underlying SQL connection remains unable to alter the code-first object.
        await using (var connection = await harness.Client.ConnectAsync(timeout.Token))
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.ExecuteAsync(forbiddenDdl, cancellationToken: timeout.Token));
            error.Message.ShouldContain("schema");
        }
        child.Name = "after";
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);

        // Assert: the retained shape and its data remain intact; ordinary typed DML is allowed.
        var loaded = (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token))
            .ShouldHaveSingleItem();
        loaded.Name.ShouldBe("after");
        loaded.Note.ShouldBe("retained");
    }
}
