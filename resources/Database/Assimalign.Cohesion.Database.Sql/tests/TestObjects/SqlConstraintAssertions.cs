using System;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Every existing typed-violation assertion also pins its single diagnostic.</summary>
internal static class SqlConstraintAssertions
{
    internal static async Task<SqlConstraintViolationException> ThrowAsync(Func<Task> action, string? customMessage = null)
    {
        var exception = await Should.ThrowAsync<DatabaseException>(action, customMessage);
        var failure = exception.ShouldBeOfType<SqlConstraintViolationException>(exception.Message);
        Verify(failure);
        return failure;
    }

    internal static void Verify(SqlConstraintViolationException failure)
    {
        string code = failure.ConstraintKind switch
        {
            "UNIQUE" or "PRIMARY KEY" => "CS03037",
            "CHECK" or "DOMAIN" => "CS03038",
            "FOREIGN KEY" => "CS03039",
            "NOT NULL" => "CS03024",
            _ => throw new InvalidOperationException("Unexpected constraint kind in the corpus."),
        };
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(failure.Message);
    }
}
