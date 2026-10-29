using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed partial class SqlSequenceSecondPassTests
{
    private readonly ITestOutputHelper _output;
    public SqlSequenceSecondPassTests(ITestOutputHelper output) { _output = output; }

    private static async Task Exec(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: token);
        result.Diagnostics.ShouldBeEmpty();
    }

    private static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
