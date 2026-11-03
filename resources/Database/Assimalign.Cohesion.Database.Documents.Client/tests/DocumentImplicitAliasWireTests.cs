using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentImplicitAliasWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Implicit alias: generated subquery variants preserve result and write digests on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_GeneratedSubqueryVariants_ShouldMatchDigestsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act / Assert
        await VerifyParityAsync(async statement =>
        {
            var result = await connection.QueryAsync(statement, cancellationToken: harness.Token);
            return result.AffectedCount + ":" + string.Concat(result.Documents.Select(value => value.GetRawText() + "|"));
        });
    }

    private const string exists = "EXISTS (SELECT RAW 1 FROM [1] AS s)";

    private static string Statement(string form, int variant)
    {
        bool combined = variant == 4 && form is not ("INSERT" or "UPSERT");
        string filter = "items.g >= 1" + (variant == 1 || combined ? " AND " + exists : "");
        if (variant == 3 && form is "UNSET" or "DELETE")
        { filter = "CASE WHEN " + exists + " THEN items.g >= 1 ELSE FALSE END"; }
        string value = variant == 2 || combined ? "CASE WHEN " + exists + " THEN items.g ELSE 0 END" : "items.g";
        string order = variant == 3 || combined ? "CASE WHEN " + exists + " THEN items.g ELSE 0 END" : "items.g";
        string image = variant == 4 || (variant == 2 && form is "UNSET" or "DELETE") || (variant == 3 && form == "UPDATE")
            ? "CASE WHEN " + exists + " THEN items ELSE NULL END" : "items";
        if (form == "SELECT")
        { return "SELECT RAW {g:" + value + ",self:items.items.g,id:META(items).id} FROM items WHERE " + filter + " ORDER BY " + order; }
        if (form == "UPDATE")
        { return "UPDATE items SET items.g = " + value + " + 1, items.copy = items.items.g UNSET items.removed WHERE " + filter + " RETURNING RAW " + image; }
        if (form == "UNSET")
        { return "UPDATE items UNSET items.removed, items.items.removed WHERE " + filter + " RETURNING RAW " + image; }
        if (form == "DELETE")
        { return "DELETE FROM items WHERE " + filter + " RETURNING RAW " + image; }
        string target = variant == 4 ? "CASE WHEN " + exists + " THEN copies ELSE NULL END" : "copies";
        return form + " INTO copies (KEY k, VALUE v) SELECT META(items).id AS k, {g:" + value +
            ",self:items.items.g} AS v FROM items WHERE " + filter + " ORDER BY " + order + " RETURNING RAW " + target;
    }

    private static async Task VerifyParityAsync(Func<string, Task<string>> execute)
    {
        await execute("CREATE COLLECTION items");
        await execute("CREATE COLLECTION copies");
        // 6 forms x 4 unrelated-subquery variants x 2 access configurations = 48 digest comparisons.
        // UPDATE/DELETE have no ORDER BY; their variants exercise legal filter and affected-image positions.
        for (int indexed = 0; indexed < 2; indexed++)
        {
            if (indexed == 1)
            {
                await execute("CREATE INDEX by_g ON items(g)");
                await execute("CREATE INDEX by_self ON items(items.g)");
            }
            foreach (string form in new[] { "SELECT", "UPDATE", "UNSET", "DELETE", "INSERT", "UPSERT" })
            {
                string? baseline = null;
                for (int variant = 0; variant < 5; variant++)
                {
                    await execute("DELETE FROM items");
                    await execute("DELETE FROM copies");
                    // Self-named fields deliberately disagree with the source's root fields.
                    for (int row = 0; row < 5; row++)
                    {
                        string g = row == 3 ? "NULL" : row == 4 ? "MISSING" : (row + 1).ToString(CultureInfo.InvariantCulture);
                        await execute("INSERT INTO items (KEY, VALUE) VALUES ('" + row + "', {g:" + g +
                            ",removed:1,items:{g:" + (9 - row) + ",removed:2},tags:[1,2]})");
                    }
                    if (form == "UPSERT") { await execute("INSERT INTO copies (KEY, VALUE) VALUES ('0', {old:1})"); }
                    string result = await execute(Statement(form, variant));
                    string images = await execute("SELECT RAW {id:META(items).id,body:items} FROM items ORDER BY META(items).id");
                    string copies = await execute("SELECT RAW {id:META(copies).id,body:copies} FROM copies ORDER BY META(copies).id");
                    string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result + images + copies)));
                    if (variant == 0) { baseline = digest; }
                    else { digest.ShouldBe(baseline, form + " variant " + variant + ", indexed=" + indexed); }
                }
            }
        }
    }
}
