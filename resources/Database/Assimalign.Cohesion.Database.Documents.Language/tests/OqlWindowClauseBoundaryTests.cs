using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlWindowClauseBoundaryTests
{
    public static IEnumerable<object[]> InvalidNames()
    {
        foreach (string name in OqlWindowFreeCorpus.Words[9..])
        {
            string arguments = name is "NTILE" ? "2" : name is "NTH_VALUE" ? "x,2" :
                name is "LAG" or "LEAD" or "FIRST_VALUE" or "LAST_VALUE" or "RATIO_TO_REPORT" ? "x" : "";
            foreach (string spelling in new[] { name, "\"" + name.ToLowerInvariant() + "\"" })
            {
                foreach (string token in new[] { "order", "unnest", "nest", "join", "left", "inner", "union", "intersect",
                    "except", "returning", "letting", "window", "1", ";", "" })
                { yield return [spelling + "(" + arguments + ")", token]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Window-only OVER reports its invalid name at the following token (#1805)")]
    [MemberData(nameof(InvalidNames))]
    public void Parse_WindowOnlyInvalidName_ShouldExplainQuoting(string call, string token)
    {
        // Arrange / Act
        string text = "SELECT " + call + " OVER " + token;
        var diagnostic = new OqlQueryParser().Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CD00002");
        diagnostic.Start.ShouldBe(text.LastIndexOf("OVER ", StringComparison.Ordinal) + 5);
        diagnostic.Message.ShouldBe("OVER requires a window name or a parenthesized specification; a window named like a clause word must be quoted.");
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - WINDOW declarations follow implicit aliases named over (#1805)")]
    [InlineData("SELECT * FROM c UNNEST COALESCE(c.a, []) over UNNEST c.b AS b")]
    [InlineData("SELECT * FROM c UNNEST COALESCE(c.a, []) over JOIN d ON META(d).id = over.k")]
    [InlineData("SELECT * FROM c UNNEST COALESCE(c.a, []) over UNION SELECT * FROM d")]
    [InlineData("SELECT over FROM c GROUP BY LOWER(c.name) over UNION SELECT 1 AS over FROM d")]
    [InlineData("SELECT RANK() OVER w FROM c UNNEST COALESCE(c.a, []) over WINDOW w AS ()")]
    [InlineData("SELECT RANK() OVER w FROM c GROUP BY LOWER(c.name) over WINDOW w AS ()")]
    [InlineData("SELECT RANK() OVER \"union\" FROM c WINDOW \"union\" AS ()")]
    public void Parse_ClauseBoundary_ShouldPreserveAliasAndWindow(string text)
    {
        // Arrange / Act / Assert
        new OqlQueryParser().Parse(text).Diagnostics.ShouldBeEmpty();
    }
}
