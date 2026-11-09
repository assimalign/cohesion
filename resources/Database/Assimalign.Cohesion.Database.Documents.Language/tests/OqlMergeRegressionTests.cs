using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Language.Internal;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlMergeRegressionTests
{
    private readonly ITestOutputHelper _output;

    public OqlMergeRegressionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: array IN operands retain their precedence-four tree")]
    [InlineData("d.a + d.b")]
    [InlineData("d.a * 2")]
    [InlineData("-d.a")]
    [InlineData("NOT d.a")]
    [InlineData("d.a IS NULL")]
    [InlineData("d.a + d.b * d.c IS NOT DISTINCT FROM d.d")]
    [InlineData("-d.a * 2 IS VALUED")]
    [InlineData("EXISTS (d.a IN [1])")]
    [InlineData("NOT (d.a OR d.b)")]
    [InlineData("d.a IS NULL + d.b")]
    [InlineData("d.a + d.b IS NULL * d.c")]
    [InlineData("d.a IS DISTINCT FROM d.b IS NULL + d.c")]
    public void Render_ArrayMembership_ShouldPreserveShape(string operand)
    {
        // Arrange / Act / Assert
        RoundTrip("MERGE INTO c USING d ON KEY d.k WHEN MATCHED THEN DELETE WHERE d.x IN " + operand);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: user collections with reserved prefixes remain opaque")]
    [InlineData("MERGE INTO \"COHESION_SCHEMA.\" USING s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING \"COHESION_SCHEMA.\" s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO c USING (SELECT RAW x FROM \"COHESION_SCHEMA.\" x) s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO \"COHESION_SCHEMA.custom\" USING s ON KEY s.k WHEN MATCHED THEN DELETE")]
    public void Render_ReservedPrefixCollection_ShouldRoundTrip(string text)
    {
        // Arrange / Act / Assert
        RoundTrip(text);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: unsupported suffixes name and locate their complete construct")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (KEY s.k, VALUE s, OPTIONS {})", "OPTIONS", "MERGE OPTIONS")]
    [InlineData("MERGE INTO c t USING d s ON META(t).id = s.k WHEN NOT MATCHED THEN INSERT (KEY s.k, OPTIONS {})", "OPTIONS", "MERGE OPTIONS")]
    [InlineData("MERGE INTO c t USING d s LEFT UNNEST s.a a ON KEY s.k WHEN MATCHED THEN DELETE", "LEFT", "MERGE LEFT UNNEST")]
    [InlineData("MERGE INTO c t USING d s LEFT OUTER UNNEST s.a a ON KEY s.k WHEN MATCHED THEN DELETE", "LEFT", "MERGE LEFT OUTER UNNEST")]
    [InlineData("MERGE INTO c t USING d s INNER UNNEST s.a a ON KEY s.k WHEN MATCHED THEN DELETE", "INNER", "MERGE INNER UNNEST")]
    [InlineData("MERGE INTO c t USING d s JOIN e ON TRUE ON KEY s.k WHEN MATCHED THEN DELETE", "JOIN", "JOIN in MERGE")]
    [InlineData("MERGE INTO c t USING d s NEST e ON TRUE ON KEY s.k WHEN MATCHED THEN DELETE", "NEST", "NEST in MERGE")]
    public void Parse_UnsupportedSuffix_ShouldLocateConstruct(string text, string token, string construct)
    {
        // Arrange / Act
        var diagnostic = Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive);
        diagnostic.Start.ShouldBe(text.IndexOf(token, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: duplicate names point at the repeated binding")]
    [InlineData("MERGE INTO c s USING d s ON KEY s.k WHEN MATCHED THEN DELETE", "s ON")]
    [InlineData("MERGE INTO c USING c ON KEY c.k WHEN MATCHED THEN DELETE", "c ON")]
    [InlineData("MERGE INTO c INDEXES USING COHESION_SCHEMA.INDEXES ON KEY INDEXES.k WHEN MATCHED THEN DELETE", "INDEXES ON")]
    [InlineData("MERGE INTO c x USING holder['x'] ON KEY x.k WHEN MATCHED THEN DELETE", "'x']")]
    [InlineData("MERGE INTO c x USING (((holder['x']))) ON KEY x.k WHEN MATCHED THEN DELETE", "'x']")]
    public void Parse_DuplicateName_ShouldLocateRepeatedName(string text, string marker)
    {
        // Arrange / Act
        var diagnostic = Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CD00010");
        diagnostic.Start.ShouldBe(text.IndexOf(marker, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: capability probe waits for execution support")]
    public void Supports_Merge_ShouldRemainFalse()
    {
        // Arrange / Act / Assert
        OqlLanguageProfile.Instance.Supports("MERGE").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: every required member and mutation-list element rejects null")]
    [InlineData(0, "collection")] [InlineData(1, "source")] [InlineData(2, "on")]
    [InlineData(3, "collection")] [InlineData(4, "value")]
    [InlineData(5, "assignments")] [InlineData(6, "unset")]
    [InlineData(7, "assignments")] [InlineData(8, "unset")]
    public void Construct_NullMember_ShouldRejectAtBoundary(int member, string parameter)
    {
        // Arrange
        var value = new OqlLiteralExpression(1);
        Func<object> create = member switch
        {
            0 => () => new OqlMergeExpression(null!, null, new("s", null), value, true),
            1 => () => new OqlMergeExpression("t", null, null!, value, true),
            2 => () => new OqlMergeExpression("t", null, new("s", null), null!, true),
            3 => () => new OqlMergeSource(null!, null),
            4 => () => new OqlMergeInsertAction(null, null!),
            5 => () => new OqlMergeUpdateAction(null!, []),
            6 => () => new OqlMergeUpdateAction([], null!),
            7 => () => new OqlMergeUpdateAction([null!], []),
            _ => () => new OqlMergeUpdateAction([], [null!]),
        };
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => create()).ParamName.ShouldBe(parameter);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: clearing or replacing caller list entries cannot change the action")]
    [InlineData(true)] [InlineData(false)]
    public void Construct_MutableLists_ShouldTakeSnapshots(bool set)
    {
        // Arrange
        var path = new OqlPathExpression([new OqlPathSegment("t", null), new OqlPathSegment("n", null)]);
        var assignment = new OqlUpdateAssignment(path, new OqlLiteralExpression(1));
        var assignments = new List<OqlUpdateAssignment> { assignment };
        var unset = new List<OqlPathExpression> { path };
        var action = new OqlMergeUpdateAction(assignments, unset);
        // Act
        if (set) { assignments[0] = null!; }
        else { unset.Clear(); }
        // Assert
        action.Assignments.ShouldHaveSingleItem().ShouldBeSameAs(assignment);
        action.Unset.ShouldHaveSingleItem().ShouldBeSameAs(path);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: expression-source alias message requires AS")]
    public void Parse_BareExpressionAlias_ShouldNameAs()
    {
        // Arrange / Act
        var diagnostic = Parse("MERGE INTO t USING [1] s ON KEY s.k WHEN MATCHED THEN DELETE").Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Message.ShouldNotBeNull().ShouldContain("AS followed by an alias", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - MERGE: diagnostic recovery trees always render safely")]
    [InlineData("MERGE INTO t USING s ON KEY \"\" WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO t")]
    [InlineData("MERGE INTO t USING [1] s ON KEY s.k WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO t USING s ON KEY s.k WHEN NOT MATCHED THEN INSERT")]
    public void Parse_RecoveryTree_ShouldRender(string text)
    {
        // Arrange / Act
        var statement = Parse(text);
        // Assert
        statement.Diagnostics.ShouldNotBeEmpty();
        Should.NotThrow(() => statement.OqlExpression.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: rendering parser depth refusals never throws")]
    public void Parse_DepthRecovery_ShouldRender()
    {
        // Arrange
        string[] expressions = [new string('(', 10000) + "s.k" + new string(')', 10000),
            string.Join(" + ", Enumerable.Repeat("s.k", 10000)), string.Concat(Enumerable.Repeat("NOT ", 64)) + "s.k"];
        // Act / Assert
        foreach (string expression in expressions)
        {
            var statement = Parse("MERGE INTO t USING s ON KEY " + expression + " WHEN MATCHED THEN DELETE");
            statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00005");
            Should.NotThrow(() => statement.OqlExpression.ToString());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - MERGE: seeded ten-thousand-statement property preserves structure and canonical text")]
    public void Render_SeededExpressions_ShouldRoundTrip()
    {
        // Arrange
        const int seed = 1804;
        var random = new Random(seed);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var arrayKinds = new HashSet<string>(StringComparer.Ordinal);
        var listKinds = new HashSet<string>(StringComparer.Ordinal);
        string[] names = ["c", "\"COHESION_SCHEMA.\"", "\"COHESION_SCHEMA.custom\"", "\"SELECT\"", "\"a\"\"b\"", "\"with space\""];
        // Act / Assert
        for (int index = 0; index < 10000; index++)
        {
            string expression = Expression(3, index % 24);
            string collection = names[index % names.Length];
            string query = Query(1);
            string source = index % 3 == 0 ? collection + " AS d" : index % 3 == 1 ? "(" + query + ") AS d" : "[" + expression + "] AS d";
            bool lookup = index % 2 == 0;
            string text = "MERGE INTO " + collection + " AS t USING " + source + (lookup ? " ON KEY " : " ON META(t).id = ") + Expression(2) +
                " WHEN MATCHED THEN UPDATE SET t.n = " + expression + " WHERE " + Expression(2) +
                " WHEN MATCHED THEN DELETE WHERE " + Expression(2) + " WHEN NOT MATCHED THEN INSERT " + (lookup ? "" : "(KEY d.k, VALUE ") + "{v:" + Expression(2) +
                "}" + (lookup ? "" : ")") + " RETURNING " + Expression(2) + " AS result";
            var merge = RoundTrip(text);
            Collect(merge);
            // Truncation also exercises parser recovery without assuming a particular diagnostic.
            Should.NotThrow(() => Parse(text[..random.Next(text.Length)]).OqlExpression.ToString());
        }
        _output.WriteLine($"Seed={seed}; statements=10000; distinct node kinds={kinds.Count}: {string.Join(", ", kinds.Order())}");
        kinds.Count.ShouldBe(25);
        _output.WriteLine($"IN array operand kinds={arrayKinds.Count}: {string.Join(", ", arrayKinds.Order())}; list operand kinds={listKinds.Count}: {string.Join(", ", listKinds.Order())}");
        arrayKinds.Count.ShouldBe(15);
        listKinds.Count.ShouldBe(18);

        void Collect(OqlExpression node)
        {
            kinds.Add(node.GetType().Name);
            if (node is OqlMergeExpression merge)
            { foreach (var assignment in merge.Update?.Assignments ?? []) { kinds.Add(assignment.GetType().Name); } }
            if (node is OqlSelectExpression select)
            { foreach (var binding in select.Bindings) { kinds.Add(binding.GetType().Name); } }
            if (node is OqlInExpression membership)
            {
                if (membership.Array is not null) { arrayKinds.Add(membership.Array.GetType().Name); }
                foreach (var value in membership.Values) { listKinds.Add(value.GetType().Name); }
            }
            foreach (var child in OqlMergeRenderer.Children(node)) { Collect(child); }
        }

        string Query(int depth)
        {
            string left = "SELECT RAW " + Expression(depth) + " FROM " + names[random.Next(names.Length)] + " AS q";
            if (random.Next(3) == 0)
            { left += (random.Next(2) == 0 ? " LEFT " : " INNER ") + (random.Next(2) == 0 ? "JOIN " : "NEST ") +
                names[random.Next(names.Length)] + " AS j ON META(j).id = q.k"; }
            if (random.Next(3) == 0) { left += (random.Next(2) == 0 ? " LEFT UNNEST " : " UNNEST ") + "q.a AS u"; }
            if (random.Next(2) == 0)
            { return random.Next(3) == 0 ? left + " GROUP BY q.a AS g GROUP AS grouped HAVING TRUE ORDER BY g DESC NULLS LAST LIMIT +2 OFFSET -0" : left; }
            if (random.Next(3) == 0) { left = "(" + left + " LIMIT +1 OFFSET -0)"; }
            string right = depth > 0 && random.Next(2) == 0 ? Query(depth - 1) : "SELECT RAW " + Expression(depth) + " FROM [1] AS z";
            string compound = left + " " + new[] { "UNION", "UNION ALL", "INTERSECT", "INTERSECT ALL", "EXCEPT", "EXCEPT ALL" }[random.Next(6)] + " (" + right + ")";
            return random.Next(4) == 0 ? compound + " LIMIT $1 OFFSET 0" : compound;
        }

        string Expression(int depth, int? forced = null)
        {
            string[] atoms = ["NULL", "NIL", "MISSING", "TRUE", "FALSE", "0", "00012", ".25", "1.20", "1e+2", "1E-2", "1e9999", "1234567890123456789012345678901234567890", "'é☃'", "'it''s'", "\"double string\"", "'\\n'", "$1", "@rows", "$value", "d.x", "\"SELECT\".\"FROM\"", "d[''][0]['a''b']", "\"COHESION_SCHEMA.\".\"limit\"", "d.*"];
            if (depth == 0) { return atoms[random.Next(atoms.Length)]; }
            string E() => Expression(depth - 1);
            return (forced ?? random.Next(24)) switch
            {
                0 => "(" + E() + " " + new[] { "+", "-", "*", "/", "%", "||", "AND", "OR", "=", "!=", "<>", "<", ">=", ">", "<=" }[random.Next(15)] + " " + E() + ")",
                1 => new[] { "+ ", "- ", "NOT ", "EXISTS " }[random.Next(4)] + "(" + E() + ")",
                2 => "(" + E() + " IS " + new[] { "NULL", "NOT NULL", "MISSING", "NOT MISSING", "VALUED", "NOT VALUED" }[random.Next(6)] + ")",
                3 => "(" + E() + " IS " + (random.Next(2) == 0 ? "NOT " : "") + "DISTINCT FROM " + E() + ")",
                4 => "(" + E() + " IN " + ArrayOperand(depth - 1) + ")",
                5 => "(" + E() + " NOT IN (" + (random.Next(10) == 0 ? "*" : E()) + ", " + E() + "))",
                6 => "(" + E() + " IN (" + Query(depth - 1) + "))",
                7 => "(" + E() + " IN ((" + Query(depth - 1) + ")))",
                8 => "(" + E() + " BETWEEN (" + E() + ") AND (" + E() + "))",
                9 => "(" + E() + " NOT BETWEEN (" + E() + ") AND (" + E() + "))",
                10 => "(" + E() + " LIKE (" + E() + ") ESCAPE '!')",
                11 => "(" + E() + " NOT LIKE (" + E() + "))",
                12 => "CASE WHEN " + E() + " THEN " + E() + " ELSE " + E() + " END",
                13 => "CASE " + E() + " WHEN " + E() + " THEN " + E() + " END",
                14 => "[" + E() + ", " + E() + "]",
                15 => "{'':" + E() + ", 'a''b':" + E() + "}",
                16 => "ARRAY " + E() + " FOR v IN " + E() + " WHEN " + E() + " END",
                17 => "FIRST " + E() + " FOR v IN " + E() + " END",
                18 => "ANY v IN " + E() + " SATISFIES " + E() + " END",
                19 => "EVERY v IN " + E() + " SATISFIES " + E() + " END",
                20 => "(" + Query(depth - 1) + ")",
                21 => "META(d).id",
                22 => new[] { "ABS(" + E() + ")", "COUNT(*)", "SUM(DISTINCT " + E() + ")", "COUNT(ALL " + E() + ")" }[random.Next(4)],
                _ => atoms[random.Next(atoms.Length)],
            };
        }

        string ArrayOperand(int depth)
        {
            string E() => Expression(depth);
            return random.Next(15) switch
            {
                0 => "1", 1 => "MISSING", 2 => "$rows", 3 => "*", 4 => "d.a",
                5 => "NOT (" + E() + ")",
                6 => new[] { "d.a + (", "d.a IS NULL + (", "d.a + d.b IS VALUED * (", "d.a IS NOT DISTINCT FROM d.b IS NOT MISSING % (" }[random.Next(4)] + E() + ")",
                7 => "ABS(" + E() + ")",
                8 => "[" + E() + "]", 9 => "{a:" + E() + "}", 10 => "(" + Query(depth) + ")",
                11 => "ARRAY " + E() + " FOR v IN [1] END", 12 => "ANY v IN [1] SATISFIES " + E() + " END",
                13 => "CASE WHEN d.a THEN " + E() + " END", _ => "META(d).id",
            };
        }
    }

    private static OqlMergeExpression RoundTrip(string text)
    {
        var first = Parse(text);
        first.Diagnostics.ShouldBeEmpty(text + string.Join(";", first.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        var merge = first.OqlExpression.ShouldBeOfType<OqlMergeExpression>();
        string rendered = merge.ToString();
        var second = Parse(rendered);
        second.Diagnostics.ShouldBeEmpty(text + " => " + rendered);
        OqlMergeParserTests.Shape(second.OqlExpression).ShouldBe(OqlMergeParserTests.Shape(merge), text + " => " + rendered);
        second.OqlExpression.ToString().ShouldBe(rendered);
        return merge;
    }

    private static OqlQueryStatement Parse(string text) => (OqlQueryStatement)new OqlQueryParser().Parse(text);
}
