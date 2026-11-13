using System;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language.Internal;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlMergeParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: both forms and every action render and reparse canonically")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN d.a + d.b")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN d.a * 2")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN -d.a")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN NOT d.a")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN d.a IS NULL")]
    [InlineData("MERGE INTO \"COHESION_SCHEMA.\" USING s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING \"COHESION_SCHEMA.\" s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING (SELECT RAW x FROM \"COHESION_SCHEMA.\" x) s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s ON KEY s.k WHEN MATCHED THEN UPDATE SET t.a = s.a UNSET t.b WHERE s.ok WHEN MATCHED THEN DELETE WHERE t.gone WHEN NOT MATCHED THEN INSERT s WHERE s.ok RETURNING t.a AS a")]
    [InlineData("MERGE INTO c USING d ON PRIMARY KEY d.k WHEN NOT MATCHED THEN INSERT {a:d.a} RETURNING RAW c")]
    [InlineData("MERGE INTO c USING d ON META(c).id = d.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING (SELECT RAW x FROM d AS x WHERE x.a > 0 ORDER BY x.a LIMIT 3) s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (KEY s.k, VALUE s) WHERE t IS MISSING")]
    [InlineData("MERGE INTO c USING (SELECT RAW x FROM d x LIMIT +1 OFFSET -0) s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING [{k:'a',v:1}] AS s ON KEY s.k WHEN MATCHED THEN UPDATE SET t.v = CASE WHEN s.v BETWEEN 1 AND 3 THEN ABS(s.v) ELSE NULL END")]
    [InlineData("MERGE INTO c USING @rows AS s ON KEY s.k WHEN NOT MATCHED THEN INSERT {a: ARRAY x FOR x IN s.a WHEN x IS NOT NULL END, b: ANY x IN s.a SATISFIES x IN [1,2] END}")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN UPDATE UNSET c.old")]
    [InlineData("MERGE INTO c USING d ON KEY $1 WHEN NOT MATCHED THEN INSERT $value")]
    [InlineData("MERGE INTO c USING [{'':1,'a''b':{'':2}}] AS s ON KEY s[''] WHEN MATCHED THEN UPDATE SET c[''] = s['a''b']['']")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN (SELECT RAW z FROM [1] AS z)")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN ((SELECT RAW z FROM [1] AS z))")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN (SELECT RAW z FROM [1] AS z UNION ALL SELECT RAW y FROM [2] AS y)")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN ((SELECT RAW z FROM [1] AS z UNION ALL SELECT RAW y FROM [2] AS y))")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN (((SELECT RAW z FROM [1] AS z) UNION ALL SELECT RAW y FROM [2] AS y))")]
    [InlineData("MERGE INTO c USING cohesion_schema.object_ownership s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING (SELECT RAW b FROM d a LEFT JOIN e b ON META(b).id = a.k LEFT UNNEST b.a x GROUP BY b GROUP AS g ORDER BY b LIMIT +1) s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.s NOT LIKE 'x!%' ESCAPE '!' AND NOT EXISTS (SELECT RAW z FROM [1] AS z)")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (NOT d.ok) IS NULL")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (EXISTS d.a) IS NULL")]
    [InlineData("MERGE INTO c USING d ON KEY 1 + (d.k IS NULL) WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (NOT d.ok) = TRUE")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (NOT d.ok) IN [TRUE]")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (NOT d.ok) BETWEEN FALSE AND TRUE")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE (NOT d.ok) LIKE 'x'")]
    [InlineData("MERGE INTO c USING ((SELECT RAW x FROM d x) UNION ALL (SELECT RAW x FROM e x)) AS s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO \"c\"\"q\" AS \"t\"\"q\" USING [\"hello\"] AS s ON KEY 'a' WHEN NOT MATCHED THEN INSERT {text:'it''s', value:FIRST x FOR x IN s END} RETURNING VALUE \"t\"\"q\"")]
    public void Parse_SupportedForms_ShouldRoundTrip(string text)
    {
        // Arrange / Act
        var first = Parse(text);
        first.Diagnostics.ShouldBeEmpty();
        var merge = first.OqlExpression.ShouldBeOfType<OqlMergeExpression>();
        string rendered = merge.ToString();
        var second = Parse(rendered);

        // Assert: canonical stability plus action/source/alias shape, independent of source spans.
        second.Diagnostics.ShouldBeEmpty(rendered);
        var reparsed = second.OqlExpression.ShouldBeOfType<OqlMergeExpression>();
        Shape(reparsed).ShouldBe(Shape(merge));
        reparsed.ToString().ShouldBe(rendered);
        reparsed.Collection.ShouldBe(merge.Collection);
        reparsed.Alias.ShouldBe(merge.Alias);
        reparsed.Source.Collection.ShouldBe(merge.Source.Collection);
        reparsed.Source.Alias.ShouldBe(merge.Source.Alias);
        reparsed.Source.Expression?.GetType().ShouldBe(merge.Source.Expression?.GetType());
        reparsed.IsLookup.ShouldBe(merge.IsLookup);
        reparsed.IsPrimary.ShouldBe(merge.IsPrimary);
        (reparsed.Update is not null).ShouldBe(merge.Update is not null);
        (reparsed.Delete is not null).ShouldBe(merge.Delete is not null);
        (reparsed.Insert is not null).ShouldBe(merge.Insert is not null);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: unsupported forms give one capability refusal")]
    [InlineData("MERGE INTO c t USING d s ON t.k = s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s ON s.k = META(t).id WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k AND t.ok WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING d ON KEYS d.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE LIMIT 1")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE OFFSET 1")]
    [InlineData("MERGE INTO c t USE KEYS ['a'] USING d s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s USE INDEX (ix) ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s USE HASH(BUILD) ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s JOIN e ON TRUE ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (KEY s.k, VALUE s, OPTIONS {})")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (KEY s.k, OPTIONS {})")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN NOT MATCHED THEN INSERT d OPTIONS {}")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (VALUE s)")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (s.k, s)")]
    public void Parse_UnsupportedForms_ShouldRefuseOnce(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: declarations and action grammar fail once")]
    [InlineData("MERGE INTO c USING c ON KEY c.k WHEN MATCHED THEN DELETE", "CD00010")]
    [InlineData("MERGE INTO c s USING d s ON KEY s.k WHEN MATCHED THEN DELETE", "CD00010")]
    [InlineData("MERGE INTO c USING d c ON KEY c.k WHEN MATCHED THEN DELETE", "CD00010")]
    [InlineData("MERGE INTO c USING (SELECT RAW d FROM d) ON KEY d.k WHEN MATCHED THEN DELETE", "CD00015")]
    [InlineData("MERGE INTO c USING [1] ON KEY 'a' WHEN MATCHED THEN DELETE", "CD00002")]
    [InlineData("MERGE INTO c USING d ON KEY d.k", "CD00002")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHEN MATCHED THEN UPDATE SET c.a = 1", "CD00002")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHEN MATCHED THEN DELETE", "CD00002")]
    [InlineData("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN UPDATE", "CD00002")]
    public void Parse_InvalidShape_ShouldGiveOneDiagnostic(string text, string code)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: deep expressions and long action lists stop at coded bounds")]
    public void Parse_DeepOrRepeatedActions_ShouldRefuseWithinBounds()
    {
        // Arrange
        string prefix = "MERGE INTO c USING d ON KEY ";
        string nested = new('(', 10000);
        string actions = string.Concat(Enumerable.Repeat(" WHEN MATCHED THEN DELETE", 10000));
        // Act / Assert
        Parse(prefix + nested + "d.k" + new string(')', 10000) + " WHEN MATCHED THEN DELETE").Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005");
        Parse(prefix + "d.k" + actions).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00002");
        Parse(prefix + string.Join(" + ", Enumerable.Repeat("d.k", 10000)) + " WHEN MATCHED THEN DELETE").Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: word remains contextual outside statement position")]
    [InlineData("SELECT merge.x FROM c merge")]
    [InlineData("SELECT merge FROM c")]
    [InlineData("EXPLAIN MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE")]
    public void Parse_ContextualWord_ShouldParse(string text)
    {
        // Arrange / Act / Assert
        Parse(text).Diagnostics.ShouldBeEmpty();
        OqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain("MERGE");
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: canonical prefixes preserve the parser's depth bound")]
    [InlineData("NOT ")]
    [InlineData("+ ")]
    [InlineData("- ")]
    public void Parse_DeepPrefixChain_ShouldReparseAtBoundary(string prefix)
    {
        // Arrange
        string expression = string.Concat(Enumerable.Repeat(prefix, 63)) + "d.k";
        // Act / Assert
        RoundTrip(expression);
        Parse("MERGE INTO c USING d ON KEY " + prefix + expression + " WHEN MATCHED THEN DELETE")
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: deep postfix and mixed prefixes retain their exact structure")]
    public void Parse_DeepPostfixAndMixedPrefixes_ShouldReparse()
    {
        // Arrange
        string postfix = "d.k" + string.Concat(Enumerable.Repeat(" IS NULL", 126));
        string mixed = "d.k";
        for (int index = 0; index < 42; index++) { mixed = "NOT (" + mixed + " IS NULL)"; }
        // Act / Assert
        RoundTrip(postfix);
        RoundTrip(mixed);
    }

    private static void RoundTrip(string expression)
    {
        var original = Parse("MERGE INTO c USING d ON KEY " + expression + " WHEN MATCHED THEN DELETE");
        original.Diagnostics.ShouldBeEmpty();
        var rendered = original.OqlExpression.ShouldBeOfType<OqlMergeExpression>().ToString();
        var reparsed = Parse(rendered);
        reparsed.Diagnostics.ShouldBeEmpty(rendered);
        Shape(reparsed.OqlExpression).ShouldBe(Shape(original.OqlExpression));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: the complete tree bound also protects canonical reparsing")]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void Parse_LeftDeepBoundary_ShouldProtectCanonicalReparse(int terms, bool supported)
    {
        // Arrange
        string text = "MERGE INTO c USING d ON KEY " + string.Join(" + ", Enumerable.Repeat("d.k", terms)) + " WHEN MATCHED THEN DELETE";
        // Act
        var statement = Parse(text);
        // Assert
        if (!supported) { statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005"); return; }
        statement.Diagnostics.ShouldBeEmpty();
        var merge = statement.OqlExpression.ShouldBeOfType<OqlMergeExpression>();
        var reparsed = Parse(merge.ToString());
        reparsed.Diagnostics.ShouldBeEmpty();
        Shape(reparsed.OqlExpression).ShouldBe(Shape(merge));
    }

    // Include all semantic fields and child positions, deliberately excluding source text/spans.
    internal static string Shape(OqlExpression expression)
    {
        string data = expression switch
        {
            OqlMergeExpression m => $"{m.Collection}|{m.Alias}|{m.Source.Collection}|{m.Source.Alias}|{m.IsLookup}|{m.IsPrimary}|" +
                $"{m.Source.Expression is not null}|{m.Update?.Assignments.Count}|{m.Update?.Unset.Count}|{m.Update?.Predicate is not null}|{m.Delete is not null}|{m.Delete?.Predicate is not null}|{m.Insert is not null}|{m.Insert?.Key is not null}|{m.Insert?.Predicate is not null}",
            OqlReturningExpression r => r.IsValue + ":" + string.Join("|", r.Projections.Select(p => p.Alias)),
            OqlSelectExpression s => $"{s.Collection}|{s.Alias}|{s.IsAliasImplicit}|{s.IsDistinct}|{s.IsValue}|{s.GroupAs}|" +
                $"{s.Source is not null}|{s.Predicate is not null}|{s.GroupBy.Count}|{s.Having is not null}|" +
                string.Join("|", s.Bindings.Select(b => b is OqlJoinExpression j ? $"{j.Collection}:{j.Alias}:{j.IsLeft}:{j.IsNest}" :
                    $"{((OqlUnnestExpression)b).Variable}:{((OqlUnnestExpression)b).Alias}:{b.IsLeft}")) + ":" +
                string.Join("|", s.Projections.Select(p => p.Alias)) + ":" + string.Join("|", s.GroupAliases) + ":" + string.Join("|", s.OrderBy.Select(o => o.Descending + ":" + o.NullPlacement)),
            OqlSetOperationExpression s => s.Operation + ":" + s.IsAll,
            OqlLiteralExpression l => l.Value is JsonElement json ? json.ValueKind + ":" + json.GetRawText() : l.Value?.GetType().Name + ":" + l.Value,
            OqlParameterExpression p => p.Name,
            OqlPathExpression p => string.Join("/", p.Segments.Select(s => s.Name + ":" + s.Index)),
            OqlBinaryExpression b => b.Operator,
            OqlUnaryExpression u => u.Operator,
            OqlMetaExpression m => m.Member ?? "",
            OqlWindowExpression w => w.Name ?? "inline",
            OqlWindowDefinition w => w.Name,
            OqlWindowSpecification w => w.Reference + ":" + w.PartitionBy.Count + ":" + string.Join("|", w.OrderBy.Select(o => o.Descending + ":" + o.NullPlacement)),
            OqlCallExpression c => c.Name + ":" + c.Quantifier,
            OqlObjectExpression o => string.Join("|", o.Members.Select(m => m.Key)),
            OqlComprehensionExpression c => c.Variable + ":" + c.IsFirst,
            OqlQuantifiedExpression q => q.Variable + ":" + q.IsEvery,
            OqlInExpression i => i.Negated + ":" + (i.Array is not null),
            OqlBetweenExpression b => b.Negated.ToString(),
            OqlLikeExpression l => l.Negated.ToString(),
            OqlCaseExpression c => $"{c.Operand is not null}:{c.Branches.Count}:{c.Otherwise is not null}",
            _ => "",
        };
        if (expression is OqlQueryExpression query)
        { data += $"|order={string.Join(",", query.OrderBy.Select(o => o.Descending + ":" + o.NullPlacement))}|limit={query.Limit is not null}|offset={query.Offset is not null}"; }
        return expression.GetType().Name + "[" + data + "](" + string.Join(",", OqlMergeRenderer.Children(expression).Select(Shape)) + ")";
    }
}
