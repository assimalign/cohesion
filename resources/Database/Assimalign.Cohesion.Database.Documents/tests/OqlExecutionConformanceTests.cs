using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>Measures every advertised OQL clause through a real document session.</summary>
public sealed class OqlExecutionConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Conformance: doubled identifier quotes preserve collection, property and alias names (#1235)")]
    public async Task ExecuteAsync_DoubledIdentifierQuotes_ShouldPreserveDocumentNames()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("quoted-identifiers", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("t\"x", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO \"t\"\"x\" (KEY, VALUE) VALUES ('k', {\"a\"\"b\": 7})", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync("SELECT \"a\"\"b\" AS \"c\"\"d\" FROM \"t\"\"x\"", cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldBeAssignableTo<DatabaseResultSet>().Columns.ShouldHaveSingleItem().Name.ShouldBe("c\"d");
        await VerifyRowsAsync(result, [[7m]], "Embedded identifier quotes", CancellationToken.None);
    }

    private static readonly IReadOnlyDictionary<string, ExecutionCase[]> _cases =
        new Dictionary<string, ExecutionCase[]>(StringComparer.OrdinalIgnoreCase)
        {
            [OqlClauses.Begin] =
            [
                new("BEGIN", [[4L]], VerificationStatement: "SELECT COUNT(*) FROM items"),
                new("START WORK ISOLATION LEVEL READ COMMITTED", [[4L]], VerificationStatement: "SELECT COUNT(*) FROM items"),
            ],
            [OqlClauses.Commit] = [new("COMMIT TRANSACTION", [[5L]],
                ["BEGIN", "INSERT INTO items (KEY, VALUE) VALUES ('committed', 1)"], "SELECT COUNT(*) FROM items")],
            [OqlClauses.Rollback] = [new("ROLLBACK WORK", [[4L]],
                ["BEGIN", "DELETE FROM items"], "SELECT COUNT(*) FROM items")],
            [OqlClauses.Explain] =
            [
                new("EXPLAIN SELECT name FROM items",
                    [[0, null, "Select", "items", null, "filter=False; grouped=False; order_keys=0; limit=none; offset=0", null, null],
                     [1, 0, "Project", "items", null, null, null, null],
                     [2, 1, "CollectionScan", "items", null, null, null, null]]),
            ],
            [OqlClauses.Insert] =
            [
                InsertCase(),
                new("INSERT INTO items (KEY, VALUE) VALUES ('object', {'absent': MISSING, 'null_value': NULL})", [[true, true]],
                    VerificationStatement: "SELECT absent IS MISSING AS absent_value, null_value IS NULL AS null_value FROM items WHERE META().id = 'object'",
                    ExpectedAffectedCount: 1),
            ],
            [OqlClauses.Upsert] =
            [
                UpsertCase(),
            ],
            [OqlClauses.Update] =
            [
                new("UPDATE items SET amount = amount + 1, details.score = 9 WHERE category = 'a'",
                    [["alpha", 3m, 9m], ["beta", 7m, 9m], ["delta", null, 9m], ["gamma", 4m, 2m]],
                    VerificationStatement: "SELECT name, amount, details.score AS score FROM items ORDER BY name", ExpectedAffectedCount: 3),
            ],
            [OqlClauses.Unset] =
            [
                new("UPDATE items UNSET tags WHERE name = 'delta'", [[true]],
                    VerificationStatement: "SELECT tags IS MISSING FROM items WHERE name = 'delta'", ExpectedAffectedCount: 1),
            ],
            [OqlClauses.Delete] =
            [
                new("DELETE FROM items WHERE category = 'a'", [["gamma"]],
                    VerificationStatement: "SELECT name FROM items", ExpectedAffectedCount: 3),
                new("DELETE FROM items", [], VerificationStatement: "SELECT name FROM items", ExpectedAffectedCount: 4),
                new("DELETE FROM items", [["by_score"]], ["CREATE INDEX by_score ON items (details.score)"],
                    "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES", ExpectedAffectedCount: 4),
            ],
            [OqlClauses.Returning] =
            [
                new("INSERT INTO items (KEY, VALUE) VALUES ('e', {name:'echo'}) RETURNING name", [["echo"]], ExpectedAffectedCount: 1),
                new("UPSERT INTO items (KEY, VALUE) VALUES ('a', {name:'replaced'}) RETURNING VALUE name", [["replaced"]], ExpectedAffectedCount: 1),
                new("UPDATE items SET amount = 10 WHERE name = 'alpha' RETURNING RAW amount", [[10m]], ExpectedAffectedCount: 1),
                new("DELETE FROM items WHERE name = 'alpha' RETURNING name", [["alpha"]], ExpectedAffectedCount: 1),
            ],
            [OqlClauses.Values] =
            [
                InsertCase(),
            ],
            [OqlClauses.Select] =
            [
                new("SELECT RAW g FROM items ORDER BY items.g", [[3m], [2m], [1m]],
                    ["DELETE FROM items", "INSERT INTO items (KEY, VALUE) VALUES ('a', {g:3}), ('b', {g:1,items:{g:30}}), ('c', {g:2,items:{g:10}})"]),
                new("SELECT RAW g FROM items d ORDER BY d.g", [[1m], [2m], [3m]],
                    ["DELETE FROM items", "INSERT INTO items (KEY, VALUE) VALUES ('a', {g:3}), ('b', {g:1,items:{g:30}}), ('c', {g:2,items:{g:10}})"]),
                new("SELECT amount = 1 IS NOT NULL, 1 = NULL IS NULL, 1 IN [1] IS NULL, 2 BETWEEN 1 AND 3 IS NOT NULL, 2 BETWEEN 1 IS NOT NULL AND 3 FROM items WHERE name = 'alpha'", [[false, false, null, false, false]]),
                new("SELECT 'abc' LIKE 'a%' IS NOT NULL, 'a%' LIKE 'a!%' ESCAPE '!' IS NOT NULL, 'a' || 'b', MISSING IS DISTINCT FROM NULL, NULL IS NOT DISTINCT FROM NULL FROM items WHERE name = 'alpha'", [[null, null, "ab", true, true]]),
                new("SELECT ARRAY_AGG(category) FROM items GROUP BY category HAVING ARRAY_LENGTH(ARRAY_AGG(category)) > 0 ORDER BY ARRAY_AGG(category)", [[Json("[\"a\",\"a\",\"a\"]")], [Json("[\"b\"]")]]),
                new("SELECT EVERY x IN [TRUE] SATISFIES EVERY(amount > 0) END FROM items", [[true]]),
                new("SELECT STDDEV(v) FROM items UNNEST [-79228162514264337593543950335,0,79228162514264337593543950335] v WHERE name = 'alpha'", [[decimal.MaxValue]]),
                new("SELECT d.name AS label, d.details.score + @increment AS adjusted, d.tags[0] AS tag FROM items AS d WHERE d.name = 'alpha'",
                    [["alpha", 3m, "blue"]], Parameters: new Dictionary<string, object?> { ["increment"] = 2 }),
                new("SELECT COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items",
                    [[4L, 3L, 12m, 4m, 2m, 6m]]),
                new("SELECT COUNT(DISTINCT category) AS c, COUNT(ALL category) AS all_c, SUM(DISTINCT amount) AS s, AVG(DISTINCT amount) AS a, MIN(ALL amount) AS lo, MAX(DISTINCT amount) AS hi FROM items",
                    [[2L, 4L, 12m, 4m, 2m, 6m]]),
                new("SELECT COUNT(*) AS total, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items WHERE amount > 100",
                    [[0L, null, null, null, null]]),
            ],
            [OqlClauses.Subquery] =
            [
                new("SELECT RAW (SELECT RAW items.category FROM [1] AS s) FROM items GROUP BY category ORDER BY category", [[Json("[\"a\"]")], [Json("[\"b\"]")]]),
                new("SELECT RAW (SELECT RAW META(items).id FROM [1] AS s) FROM items GROUP BY META().id ORDER BY META().id", [[Json("[\"a\"]")], [Json("[\"b\"]")], [Json("[\"c\"]")], [Json("[\"d\"]")]]),
                new("SELECT RAW (SELECT RAW (SELECT RAW d.n FROM [1] AS s) FROM [1] AS t) FROM [{n:10}] AS d GROUP BY (SELECT RAW d.n FROM [1] AS s)", [[Json("[[10]]")]]),
                new("SELECT RAW (SELECT s.amount AS d FROM items s ORDER BY d.amount) FROM items d WHERE name = 'alpha'", [[Json("[{\"d\":2},{\"d\":6},{\"d\":4},{\"d\":null}]")]],
                    ["CREATE INDEX collision ON items (d.amount INCLUDE MISSING)"]),
                new("SELECT RAW (SELECT RAW META(s).id FROM items s ORDER BY META(d).id DESC LIMIT 1) FROM items d LIMIT 1", [[Json("[\"a\"]")]]),
                new("SELECT RAW (SELECT RAW d.category FROM [1] AS s) FROM items d GROUP BY d.category ORDER BY d.category", [[Json("[\"a\"]")], [Json("[\"b\"]")]]),
                new("SELECT RAW (SELECT RAW META(s).id FROM items s WHERE d.category = 'a' ORDER BY META(s).id) FROM items d WHERE name = 'alpha'",
                    [[Json("[\"a\",\"b\",\"c\",\"d\"]")]], ["CREATE INDEX captured ON items (d.category)"]),
                new("SELECT RAW 1 FROM [1] AS d WHERE EVERY v IN [1,2,3] SATISFIES EXISTS (SELECT RAW v FROM [1] AS s) END", [[1m]]),
                new("SELECT (SELECT RAW d.name FROM [1] AS s) AS names FROM items d WHERE name = 'alpha'", [[Json("[\"alpha\"]")]]),
                new("SELECT (SELECT RAW META(d).id FROM [1] AS s) AS ids FROM items d WHERE name = 'alpha'", [[Json("[\"a\"]")]]),
                new("SELECT (SELECT d.name AS n, COUNT(*) AS c FROM [1,2] AS s HAVING d.name = 'alpha') AS a FROM items d WHERE name = 'alpha'", [[Json("[{\"n\":\"alpha\",\"c\":2}]")]]),
                new("SELECT (SELECT RAW v FROM d.tags AS v) AS tags FROM items d WHERE name = 'alpha'", [[Json("[\"blue\"]")]]),
                new("UPDATE items d SET names = (SELECT RAW d.name FROM [1] AS s) WHERE EXISTS (SELECT RAW s.name FROM items s WHERE s.name = d.name)",
                    [[Json("[\"alpha\"]")]], VerificationStatement: "SELECT names FROM items WHERE name = 'alpha'", ExpectedAffectedCount: 4),
                new("DELETE FROM items d WHERE d.name IN (SELECT RAW s.name FROM items s WHERE s.name = 'alpha')", [["beta"], ["gamma"], ["delta"]],
                    VerificationStatement: "SELECT name FROM items", ExpectedAffectedCount: 1),
                new("SELECT (SELECT RAW name FROM items WHERE name = 'alpha') AS names FROM items WHERE name = 'beta'", [[Json("[\"alpha\"]")]]),
                new("SELECT name FROM items WHERE name IN (SELECT RAW name FROM items WHERE category = 'b')", [["gamma"]]),
                new("SELECT (SELECT RAW name FROM items s WHERE category = 'b') AS names FROM items d WHERE name = 'alpha'", [[Json("[\"gamma\"]")]]),
            ],
            [OqlClauses.Exists] =
            [
                new("SELECT EXISTS [] AS empty, EXISTS [NULL] AS nonempty, NOT EXISTS [] AS negated FROM items WHERE name = 'alpha'", [[false, true, true]]),
                new("SELECT EXISTS 'a' || 'b' AS c, EXISTS NULL IS DISTINCT FROM MISSING AS d, NULL IS NOT DISTINCT FROM NULL AS n FROM items WHERE name = 'alpha'", [[null, null, true]]),
                new("SELECT EXISTS [] IS NULL AS p, EXISTS [] = FALSE AS q, NOT EXISTS [] AND TRUE AS r FROM items WHERE name = 'alpha'", [[null, true, true]]),
            ],
            [OqlClauses.Distinct] =
            [
                new("SELECT DISTINCT category FROM items ORDER BY category", [["a"], ["b"]]),
            ],
            [OqlClauses.SelectValue] =
            [
                new("SELECT VALUE name FROM items WHERE category = 'b'", [["gamma"]]),
            ],
            [OqlClauses.ObjectConstructor] =
            [
                new("SELECT VALUE {\"n\": name, \"t\": tags} FROM items WHERE name = 'alpha'", [[Json("{\"n\":\"alpha\",\"t\":[\"blue\"]}")]]),
            ],
            [OqlClauses.ArrayConstructor] =
            [
                new("SELECT [name, amount] AS pair FROM items WHERE name = 'beta'", [[Json("[\"beta\",6]")]]),
            ],
            [OqlClauses.ArrayComprehension] =
            [
                new("SELECT ARRAY x FOR x IN [1,2] WHEN x > 1 END AS a FROM items WHERE name = 'alpha'", [[Json("[2]")]]),
                new("SELECT ARRAY x.a FOR x IN [{},{a:null},{a:2}] END AS a FROM items WHERE name = 'alpha'", [[Json("[null,2]")]]),
                new("SELECT ARRAY (x) + CASE WHEN TRUE THEN 1 ELSE 2 END FOR x IN [1] WHEN CASE WHEN TRUE THEN TRUE ELSE FALSE END END AS result FROM items WHERE name = 'alpha'", [[Json("[2]")]]),
                new("SELECT ARRAY (x) IN ARRAY y FOR y IN [x] END FOR x IN [1] WHEN ARRAY y FOR y IN [x] END END AS result FROM items WHERE name = 'alpha'", [[Json("[true]")]]),
                new("SELECT ARRAY (x) + FIRST y FOR y IN [x] END FOR x IN [1] WHEN FIRST y FOR y IN [x] END END AS result FROM items WHERE name = 'alpha'", [[Json("[2]")]]),
                new("SELECT ARRAY (x = 1) = ANY y IN [x] SATISFIES y END FOR x IN [1] WHEN ANY y IN [x] SATISFIES y END END AS result FROM items WHERE name = 'alpha'", [[Json("[true]")]]),
                new("SELECT ARRAY (x = 1) = EVERY y IN [x] SATISFIES y END FOR x IN [1] WHEN EVERY y IN [x] SATISFIES y END END AS result FROM items WHERE name = 'alpha'", [[Json("[true]")]]),
            ],
            [OqlClauses.FirstComprehension] =
            [
                new("SELECT FIRST x FOR x IN [1,2] WHEN x > 1 END AS f FROM items WHERE name = 'alpha'", [[2m]]),
                new("SELECT FIRST (x) + CASE WHEN TRUE THEN 1 ELSE 2 END FOR x IN [1] END AS result FROM items WHERE name = 'alpha'", [[2m]]),
            ],
            [OqlClauses.DerivedFrom] =
            [
                new("SELECT d.name FROM (SELECT name FROM items WHERE name = 'alpha') d", [["alpha"]]),
            ],
            [OqlClauses.ExpressionFrom] =
            [
                new("SELECT RAW v FROM [1,2] AS v", [[1m], [2m]]),
                new("SELECT RAW v FROM NULL AS v", [[null]]),
                new("SELECT RAW v FROM MISSING AS v", []),
            ],
            [OqlClauses.From] =
            [
                new("SELECT d.name FROM items d", [["alpha"], ["beta"], ["gamma"], ["delta"]]),
                new("SELECT OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", [["items"]]),
            ],
            [OqlClauses.Where] =
            [
                new("SELECT 1 FROM items WHERE FALSE AND amount = (ANY x IN [0] SATISFIES 1 / x > 0 END)", []),
                new("SELECT 1 FROM items WHERE FALSE AND amount BETWEEN 0 AND (FIRST 1 / x FOR x IN [0] END)", []),
                new("SELECT name FROM items WHERE (amount >= @minimum AND amount < 6) OR amount IS NULL",
                    [["gamma"], ["delta"]], Parameters: new Dictionary<string, object?> { ["minimum"] = 3 }),
                new("SELECT name FROM items WHERE details.score IS NULL AND NOT (amount IS NOT NULL)", []),
                new("SELECT name FROM items WHERE details.score IS MISSING", [["delta"]]),
                new("SELECT name FROM items WHERE amount IS MISSING", []),
                new("SELECT name FROM items WHERE details.score IS NOT MISSING", [["alpha"], ["beta"], ["gamma"]]),
                new("SELECT name FROM items WHERE amount IS NOT VALUED", [["delta"]]),
                new("SELECT name FROM items WHERE details.score IS VALUED", [["alpha"], ["beta"], ["gamma"]]),
            ],
            [OqlClauses.Unnest] =
            [
                new("SELECT d.name, t FROM items d UNNEST d.tags AS t ORDER BY d.name", [["alpha", "blue"], ["beta", "green"], ["gamma", "blue"]]),
                new("SELECT t, COUNT(*) AS n FROM items d UNNEST d.tags t GROUP BY t ORDER BY t", [["blue", 2L], ["green", 1L]]),
            ],
            [OqlClauses.LeftUnnest] =
            [
                new("SELECT d.name, t FROM items d LEFT UNNEST d.tags t ORDER BY d.name", [["alpha", "blue"], ["beta", "green"], ["delta", null], ["gamma", "blue"]], Missing: [(2, 1)]),
            ],
            [OqlClauses.Any] =
            [
                new("SELECT name FROM items d WHERE ANY t IN d.tags SATISFIES t = 'blue' END", [["alpha"], ["gamma"]]),
                new("SELECT name FROM items d WHERE SOME t IN d.tags SATISFIES t = 'blue' END", [["alpha"], ["gamma"]]),
            ],
            [OqlClauses.Every] =
            [
                new("SELECT name FROM items d WHERE EVERY t IN d.tags SATISFIES t = 'blue' END", [["alpha"], ["gamma"], ["delta"]]),
            ],
            [OqlClauses.In] =
            [
                new("SELECT name FROM items WHERE category IN ('b', 'z')", [["gamma"]]),
                new("SELECT name FROM items d WHERE 'green' IN d.tags", [["beta"]]),
            ],
            [OqlClauses.Between] =
            [
                new("SELECT name FROM items WHERE amount NOT BETWEEN 3 AND 5", [["alpha"], ["beta"]]),
            ],
            [OqlClauses.Like] =
            [
                new("SELECT name FROM items WHERE name LIKE '%ta'", [["beta"], ["delta"]]),
            ],
            [OqlClauses.Case] =
            [
                new("SELECT name, CASE WHEN amount > 3 THEN 'big' WHEN amount IS NULL THEN 'none' ELSE 'small' END AS size FROM items",
                    [["alpha", "small"], ["beta", "big"], ["gamma", "big"], ["delta", "none"]]),
                new("SELECT CASE WHEN amount > 3 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n FROM items GROUP BY CASE WHEN amount > 3 THEN 'big' ELSE 'small' END",
                    [["big", 2L], ["small", 2L]]),
                new("SELECT SUM(CASE WHEN amount > 3 THEN 1 ELSE 0 END) AS big FROM items", [[2m]]),
            ],
            [OqlClauses.GroupBy] =
            [
                new("SELECT COUNT(*) FROM items UNNEST [" + string.Join(",", Enumerable.Range(0, 100)) + "] x WHERE META().id = 'a' GROUP BY @v AS key",
                    [[100L]], Parameters: new Dictionary<string, object?> { ["v"] = new string('p', 16000) }, StatementMemoryBudget: 1024 * 1024),
                new("SELECT key, COUNT(*) AS n FROM items GROUP BY category AS key ORDER BY key", [["a", 3L], ["b", 1L]]),
                new("SELECT category, COUNT(*) AS total, COUNT(amount) AS present, SUM(amount) AS sum, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items GROUP BY category ORDER BY category",
                    [["a", 3L, 2L, 8m, 4m, 2m, 6m], ["b", 1L, 1L, 4m, 4m, 4m, 4m]]),
                new("SELECT category, tags[0] AS tag, COUNT(*) AS total FROM items GROUP BY category, tags[0] ORDER BY category, tag",
                    [["a", null, 1L], ["a", "blue", 1L], ["a", "green", 1L], ["b", "blue", 1L]], Missing: [(0, 1)]),
            ],
            [OqlClauses.GroupAs] =
            [
                new("SELECT key, ARRAY_LENGTH(g) AS n, FIRST r.d.name FOR r IN g WHEN r.d.name = 'gamma' END AS found FROM items d GROUP BY category AS key GROUP AS g HAVING ANY r IN g SATISFIES r.d.amount > 0 END ORDER BY key",
                    [["a", 3L, null], ["b", 1L, "gamma"]], Missing: [(0, 2)]),
                new("SELECT g FROM items WHERE FALSE GROUP BY TRUE GROUP AS g", []),
            ],
            [OqlClauses.Having] =
            [
                new("SELECT category, SUM(amount) AS total FROM items GROUP BY category HAVING SUM(amount) > 5", [["a", 8m]]),
                new("SELECT COUNT(*) AS total FROM items HAVING COUNT(*) > 3", [[4L]]),
                new("SELECT COUNT(*) AS total FROM items HAVING COUNT(*) > 4", []),
            ],
            [OqlClauses.OrderBy] =
            [
                new("SELECT META().id FROM items ORDER BY amount", [["d"], ["a"], ["c"], ["b"]],
                    ["CREATE INDEX ordered_amount ON items (amount INCLUDE MISSING)"]),
                new("SELECT META().id FROM items WHERE amount >= 2 AND category = 'a' ORDER BY amount DESC", [["b"], ["a"]],
                    ["CREATE INDEX ordered_amount ON items (amount)"]),
                new("SELECT name AS label, amount FROM items ORDER BY amount DESC NULLS FIRST, label ASC NULLS LAST",
                    [["delta", null], ["beta", 6m], ["gamma", 4m], ["alpha", 2m]]),
                new("SELECT name AS label, amount FROM items ORDER BY amount DESC, label ASC",
                    [["beta", 6m], ["gamma", 4m], ["alpha", 2m], ["delta", null]]),
            ],
            [OqlClauses.Limit] =
            [
                new("SELECT RAW META(items).id FROM items ORDER BY META(items).id LIMIT 2", [["a"], ["b"]]),
                new("SELECT RAW v FROM [1/0] AS v ORDER BY v LIMIT 0", []),
                new("SELECT DISTINCT RAW v FROM [1/0] AS v LIMIT 0", []),
                new("SELECT COUNT(*) FROM [1/0] AS v LIMIT 0", []),
                new("SELECT RAW d FROM (SELECT RAW v FROM [1/0] AS v) d ORDER BY d LIMIT 0", []),
                new("SELECT RAW name FROM items WHERE 1/0 = 1 ORDER BY name LIMIT 0", []),
                new("SELECT RAW OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE 1/0 = 1 ORDER BY OBJECT_NAME LIMIT 0", []),
                new("SELECT * FROM items ORDER BY k LIMIT 1", [[Json("{\"k\":1}")]],
                    ["DELETE FROM items", "INSERT INTO items (KEY, VALUE) VALUES ('one', {'k':1}), ('two', {'k':2})",
                     "CREATE INDEX ordered_k ON items (k INCLUDE MISSING)"]),
                new("SELECT * FROM items ORDER BY k DESC LIMIT 1", [[Json("{\"k\":2}")]],
                    ["DELETE FROM items", "INSERT INTO items (KEY, VALUE) VALUES ('one', {'k':1}), ('two', {'k':2})",
                     "CREATE INDEX ordered_k ON items (k INCLUDE MISSING)"]),
                new("SELECT META().id FROM items ORDER BY category DESC LIMIT 2 OFFSET 1", [["a"], ["b"]],
                    ["CREATE INDEX ordered_category ON items (category INCLUDE MISSING, amount)"]),
                new("SELECT name FROM items ORDER BY name LIMIT 2", [["alpha"], ["beta"]]),
                new("SELECT name FROM items ORDER BY name LIMIT 2 OFFSET 1", [["beta"], ["delta"]]),
                new("SELECT name FROM items ORDER BY name LIMIT 0", []),
                new("SELECT name FROM items ORDER BY name LIMIT @minimum", [["alpha"], ["beta"], ["delta"]],
                    Parameters: new Dictionary<string, object?> { ["minimum"] = 3 }),
            ],
            [OqlClauses.Offset] =
            [
                new("SELECT META().id FROM items WHERE amount >= 2 ORDER BY amount DESC OFFSET 1 LIMIT 1", [["c"]],
                    ["CREATE INDEX ordered_amount ON items (amount)"]),
                new("SELECT name FROM items ORDER BY name OFFSET 3", [["gamma"]]),
                new("SELECT name FROM items ORDER BY name OFFSET 1 LIMIT 2", [["beta"], ["delta"]]),
            ],
            [OqlClauses.CreateCollection] =
            [
                new("CREATE COLLECTION extra", [["extra", "Adhoc"], ["items", "Adhoc"]],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
                new("CREATE COLLECTION IF NOT EXISTS extra", [["extra", "Adhoc"], ["items", "Adhoc"]],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
            ],
            [OqlClauses.DropCollection] =
            [
                new("DROP COLLECTION items", [],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
                new("DROP COLLECTION IF EXISTS items", [],
                    VerificationStatement: "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"),
            ],
            [OqlClauses.CreateIndex] =
            [
                new("CREATE INDEX inclusive ON items (amount INCLUDE MISSING)", [["amount INCLUDE MISSING", true]],
                    VerificationStatement: "SELECT PATH, INCLUDE_MISSING FROM COHESION_SCHEMA.INDEXES"),
                new("CREATE INDEX by_score ON items (details.score)", [["by_score", "details.score", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES"),
                new("CREATE UNIQUE INDEX by_name ON items (name)", [["by_name", "name", "name", null, true]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, PATHS[0], PATHS[1], IS_UNIQUE FROM COHESION_SCHEMA.INDEXES ORDER BY INDEX_NAME",
                    Missing: [(0, 3)]),
                new("CREATE INDEX by_cat_amount ON items (category, amount)", [["by_cat_amount", "category", "category", "amount", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, PATHS[0], PATHS[1], IS_UNIQUE FROM COHESION_SCHEMA.INDEXES ORDER BY INDEX_NAME"),
                new("CREATE INDEX IF NOT EXISTS by_score ON items (details.score)", [["by_score", "details.score", "details.score", null, false]],
                    ["CREATE INDEX by_score ON items (details.score)"],
                    "SELECT INDEX_NAME, PATH, PATHS[0], PATHS[1], IS_UNIQUE FROM COHESION_SCHEMA.INDEXES ORDER BY INDEX_NAME", Missing: [(0, 3)]),
                new("CREATE INDEX by_tag ON items (tags[0])", [["by_tag", "tags[0]", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES"),
                new("CREATE INDEX by_elements ON items (DISTINCT ARRAY t FOR t IN tags END)",
                    [["by_elements", "DISTINCT ARRAY v FOR v IN \"tags\" END", false]],
                    VerificationStatement: "SELECT INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES"),
            ],
            [OqlClauses.DropIndex] =
            [
                new("DROP INDEX IF EXISTS absent ON items", [], VerificationStatement: "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES"),
                new("DROP INDEX by_score ON items", [], ["CREATE INDEX by_score ON items (details.score)"],
                    "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES"),
            ],
        };

    /// <summary>Fails on an unmapped capability and executes every mapped representative with result assertions.</summary>
    [Fact(DisplayName = "Cohesion Test [Documents] - Profile: Every advertised OQL clause executes")]
    public async Task Profile_AdvertisedClauses_AllExecuteAsync()
    {
        // Arrange: the profile drives enumeration; the case table cannot omit a new capability.
        var profile = OqlLanguageProfile.Instance;
        VerifyCoverage(profile);
        foreach (string clause in profile.Clauses)
        {
            foreach (var execution in _cases[clause])
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cancellationToken = timeout.Token;
                string context = $"OQL clause '{clause}': {execution.Statement}";
                var parsed = new OqlQueryParser().Parse(execution.Statement).ShouldBeOfType<OqlQueryStatement>();
                parsed.Diagnostics.ShouldBeEmpty(context);
                ContainsClause(parsed.OqlExpression, clause).ShouldBeTrue(context);

                var options = new DocumentDatabaseEngineOptions();
                if (execution.StatementMemoryBudget is long budget) { options.StatementMemoryBudget = budget; }
                await using var engine = DocumentDatabaseEngine.Create("document-engine", options);
                var database = await engine.CreateDatabaseAsync("conformance", cancellationToken);
                await using var session = await database.CreateSessionAsync(cancellationToken);
                var collection = await session.CreateCollectionAsync("items", cancellationToken);
                await SeedAsync(collection, session, cancellationToken);
                foreach (string setup in execution.Setup ?? [])
                {
                    (await session.ExecuteAsync(setup, cancellationToken: cancellationToken)).Status.ShouldBe(DatabaseResultStatus.Success, context);
                }

                // Act: text takes the production parse, planner, transaction and executor path.
                var result = await session.ExecuteAsync(execution.Statement, execution.Parameters, cancellationToken);

                // Assert: DDL must change catalog state; queries must return the exact values and order.
                result.Status.ShouldBe(DatabaseResultStatus.Success, context);
                if (clause == OqlClauses.Returning) { result.AffectedCount.ShouldBe(execution.ExpectedAffectedCount, context); }
                if (execution.VerificationStatement is string verification)
                {
                    result.AffectedCount.ShouldBe(execution.ExpectedAffectedCount, context);
                    result = await session.ExecuteAsync(verification, cancellationToken: cancellationToken);
                }
                await VerifyRowsAsync(result, execution.ExpectedRows, context, cancellationToken, execution.Missing);
            }
        }
    }

    /// <summary>Proves that advertising a clause without an execution case fails the coverage guard.</summary>
    [Fact(DisplayName = "Cohesion Test [Documents] - Profile: Added clause without execution evidence fails")]
    public void Profile_AddedClauseWithoutCase_FailsCoverage()
    {
        var profile = OqlLanguageProfile.Instance;
        var extended = new QueryLanguageProfile(profile.Language, profile.Keywords.ToArray(), profile.Functions.ToArray(),
            [.. profile.Clauses, "UNMEASURED CLAUSE"]);

        var failure = Should.Throw<ShouldAssertException>(() => VerifyCoverage(extended));

        failure.Message.ShouldContain("UNMEASURED CLAUSE", Case.Sensitive);
    }

    /// <summary>Distinguishes intentional semantic validation from an unimplemented executor path.</summary>
    /// <param name="statement">The syntactically accepted but semantically invalid statement.</param>
    /// <param name="message">The exact intended planning error.</param>
    [Theory(DisplayName = "Cohesion Test [Documents] - Profile: Invalid aggregate contexts return intended errors")]
    [InlineData("SELECT amount, SUM(amount) FROM items", "An OQL grouped query may only read grouping expressions outside aggregate calls.")]
    [InlineData("SELECT name FROM items HAVING name = 'alpha'", "OQL HAVING requires grouping or an aggregate.")]
    [InlineData("SELECT name FROM items WHERE COUNT(*) > 1", "The expression is not valid in this OQL planning context.")]
    [InlineData("SELECT SUM(COUNT(*)) FROM items", "The expression is not valid in this OQL planning context.")]
    public async Task Execute_InvalidAggregateContext_ReturnsIntendedErrorAsync(string statement, string message)
    {
        new OqlQueryParser().Parse(statement).Diagnostics.ShouldBeEmpty();
        await using var engine = DocumentDatabaseEngine.Create("document-engine", new());
        var database = await engine.CreateDatabaseAsync("conformance", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        var exception = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        exception.Message.ShouldBe(message);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Conformance: index catalog user errors have distinct codes and preserve the session")]
    [InlineData("duplicate", "CD03024")]
    [InlineData("missing", "CD01025")]
    [InlineData("key-size", "CD02026")]
    [InlineData("definition-size", "CD03026")]
    public async Task Execute_IndexUserError_ShouldReturnDistinctCodeAndKeepSession(string scenario, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("index-errors", new());
        var database = await engine.CreateDatabaseAsync("conformance", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 2000) + "\"}"), cancellationToken: CancellationToken.None);
        if (scenario == "duplicate") { await session.ExecuteAsync("CREATE INDEX ix ON items (b)", cancellationToken: CancellationToken.None); }
        string statement = scenario switch
        {
            "duplicate" => "CREATE INDEX ix ON items (b)",
            "missing" => "DROP INDEX absent ON items",
            "definition-size" => "CREATE INDEX ix ON items (" + string.Join(',',
                Enumerable.Range(0, 32).Select(i => new string('p', 260) + i)) + ")",
            _ => "CREATE INDEX ix ON items (a)",
        };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.InnerException.ShouldBeOfType<Catalog.DocumentCatalogException>();
        (await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    private static readonly IReadOnlyDictionary<string, FunctionCase> _functionCases =
        new Dictionary<string, FunctionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["META"] = new("META().id", [["a"], ["b"], ["c"], ["d"]]),
            ["LOWER"] = ScalarCase("LOWER('AbC')", "abc"),
            ["UPPER"] = ScalarCase("UPPER('AbC')", "ABC"),
            ["LENGTH"] = ScalarCase("LENGTH('😀a')", 2L),
            ["SUBSTR"] = ScalarCase("SUBSTR('😀ab', 1, 1)", "a"),
            ["POSITION"] = ScalarCase("POSITION('abc', 'z')", -1L),
            ["CONTAINS"] = ScalarCase("CONTAINS('abc', 'b')", true),
            ["STARTS_WITH"] = ScalarCase("STARTS_WITH('abc', 'a')", true),
            ["ENDS_WITH"] = ScalarCase("ENDS_WITH('abc', 'c')", true),
            ["REPLACE"] = ScalarCase("REPLACE('abc', 'b', 'x')", "axc"),
            ["TRIM"] = ScalarCase("TRIM(' abc ')", "abc"),
            ["CONCAT"] = ScalarCase("CONCAT('a', 'b')", "ab"),
            ["ABS"] = ScalarCase("ABS(-2)", 2m),
            ["ROUND"] = ScalarCase("ROUND(2.5)", 3m),
            ["TRUNC"] = ScalarCase("TRUNC(2.567, 2)", 2.56m),
            ["FLOOR"] = ScalarCase("FLOOR(2.5)", 2m),
            ["CEIL"] = ScalarCase("CEIL(2.5)", 3m),
            ["POWER"] = ScalarCase("POWER(2, 3)", 8m),
            ["SQRT"] = ScalarCase("SQRT(4)", 2m),
            ["NOW_STR"] = ScalarCase("STR_TO_MILLIS(NOW_STR()) = NOW_MILLIS()", true),
            ["NOW_MILLIS"] = ScalarCase("NOW_MILLIS() = NOW_MILLIS()", true),
            ["STR_TO_MILLIS"] = ScalarCase("STR_TO_MILLIS('1970-01-01T00:00:00Z')", 0L),
            ["MILLIS_TO_STR"] = ScalarCase("MILLIS_TO_STR(0)", "1970-01-01T00:00:00.000Z"),
            ["TYPE"] = ScalarCase("TYPE(tags)", "array"),
            ["IS_ARRAY"] = ScalarCase("IS_ARRAY(tags)", true),
            ["IS_OBJECT"] = ScalarCase("IS_OBJECT(tags)", false),
            ["IS_STRING"] = ScalarCase("IS_STRING(name)", true),
            ["IS_NUMBER"] = ScalarCase("IS_NUMBER(1)", true),
            ["IS_BOOLEAN"] = ScalarCase("IS_BOOLEAN(TRUE)", true),
            ["ARRAY_LENGTH"] = ScalarCase("ARRAY_LENGTH([1,2])", 2L),
            ["ARRAY_CONTAINS"] = new("ARRAY_CONTAINS(tags, 'blue')", [[true], [false], [true], [false]]),
            ["COALESCE"] = ScalarCase("COALESCE(MISSING, NULL, 1)", 1m),
            ["IFMISSING"] = ScalarCase("IFMISSING(absent, 'x')", "x"),
            ["IFNULL"] = new("IFNULL(amount, 0)", [[2m], [6m], [4m], [0m]]),
            ["IFMISSINGORNULL"] = ScalarCase("IFMISSINGORNULL(absent, NULL, 1)", 1m),
            ["COUNT"] = new("COUNT(*)", [[4L]]),
            ["SUM"] = new("SUM(amount)", [[12m]]),
            ["AVG"] = new("AVG(amount)", [[4m]]),
            ["ARRAY_AGG"] = new("ARRAY_AGG(DISTINCT 1)", [[Json("[1]")]]),
            ["BOOL_AND"] = new("BOOL_AND(amount > 3)", [[false]]),
            ["BOOL_OR"] = new("BOOL_OR(amount > 3)", [[true]]),
            ["EVERY"] = new("EVERY(amount > 0)", [[true]]),
            ["STDDEV"] = new("STDDEV(amount)", [[2m]]),
            ["VARIANCE"] = new("VARIANCE(amount)", [[4m]]),
            ["MIN"] = new("MIN(amount)", [[2m]]),
            ["MAX"] = new("MAX(amount)", [[6m]]),
        };

    private static FunctionCase ScalarCase(string expression, object? value)
        => new(expression, Enumerable.Range(0, 4).Select(_ => new object?[] { value }).ToArray());

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: every declaration has live execution evidence")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCaseAsync()
    {
        // Arrange
        RequireFunctionCoverage(OqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys);
        await using var engine = DocumentDatabaseEngine.Create("document-functions", new());
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await SeedAsync(collection, session, CancellationToken.None);

        // Act / Assert
        foreach (string name in OqlLanguageProfile.Instance.Functions.ToArray())
        {
            var execution = _functionCases[name];
            var result = await session.ExecuteAsync($"SELECT {execution.Expression} FROM items", cancellationToken: CancellationToken.None);
            await VerifyRowsAsync(result, execution.ExpectedRows, name, CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: coverage rejects missing evidence and retired cases")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(int mismatch)
    {
        // Arrange
        var declarations = OqlLanguageProfile.Instance.Functions.ToArray();
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            mismatch == 0 ? declarations.Append("FUTURE") : mismatch == 2 ? declarations.Where(name => name != "COUNT") : declarations,
            mismatch == 1 ? _functionCases.Keys.Append("RETIRED") : _functionCases.Keys));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases)
    {
        declarations.Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    private static JsonElement Json(string text)
    {
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private sealed record FunctionCase(string Expression, object?[][] ExpectedRows);

    private static void VerifyCoverage(QueryLanguageProfile profile)
    {
        profile.Clauses.ShouldNotBeEmpty();
        foreach (string clause in profile.Clauses)
        {
            _cases.TryGetValue(clause, out var executions).ShouldBeTrue($"Advertised OQL clause '{clause}' has no execution case.");
            executions.ShouldNotBeNull().ShouldNotBeEmpty($"Advertised OQL clause '{clause}' has no execution case.");
        }
        _cases.Keys.Except(profile.Clauses, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("Retired capabilities must not remain positive conformance cases.");
    }

    private static bool ContainsClause(OqlExpression expression, string clause) => clause switch
    {
        OqlClauses.Returning => expression is OqlInsertExpression { Returning: not null } or OqlUpsertExpression { Returning: not null }
            or OqlUpdateExpression { Returning: not null } or OqlDeleteExpression { Returning: not null },
        OqlClauses.Begin => expression is OqlTransactionExpression { Command: OqlTransactionCommand.Begin },
        OqlClauses.Commit => expression is OqlTransactionExpression { Command: OqlTransactionCommand.Commit },
        OqlClauses.Rollback => expression is OqlTransactionExpression { Command: OqlTransactionCommand.Rollback },
        OqlClauses.Explain => expression is OqlExplainExpression,
        OqlClauses.Insert => expression is OqlInsertExpression,
        OqlClauses.Upsert => expression is OqlUpsertExpression,
        OqlClauses.Delete => expression is OqlDeleteExpression,
        OqlClauses.Update => expression is OqlUpdateExpression,
        OqlClauses.Unset => expression is OqlUpdateExpression { Unset.Count: > 0 },
        OqlClauses.Values => expression is OqlInsertExpression { Values.Count: > 0 },
        OqlClauses.Select => expression is OqlSelectExpression { Projections.Count: > 0 },
        OqlClauses.Subquery => Assimalign.Cohesion.Database.Documents.Internal.DocumentPlanner.HasSubquery(expression),
        OqlClauses.Exists => expression is OqlSelectExpression exists && exists.Projections.Any(p => p.Expression is OqlUnaryExpression { Operator: "EXISTS" }),
        OqlClauses.Distinct => expression is OqlSelectExpression { IsDistinct: true },
        OqlClauses.SelectValue => expression is OqlSelectExpression { IsValue: true },
        OqlClauses.ObjectConstructor => expression is OqlSelectExpression objectQuery && objectQuery.Projections.Any(p => p.Expression is OqlObjectExpression),
        OqlClauses.ArrayConstructor => expression is OqlSelectExpression arrayQuery && arrayQuery.Projections.Any(p => p.Expression is OqlArrayExpression),
        OqlClauses.ArrayComprehension => expression is OqlSelectExpression arrays && arrays.Projections.Any(p => p.Expression is OqlComprehensionExpression { IsFirst: false }),
        OqlClauses.FirstComprehension => expression is OqlSelectExpression first && first.Projections.Any(p => p.Expression is OqlComprehensionExpression { IsFirst: true }),
        OqlClauses.From => expression is OqlSelectExpression { Collection.Length: > 0 },
        OqlClauses.DerivedFrom => expression is OqlSelectExpression { Source: OqlSubqueryExpression },
        OqlClauses.ExpressionFrom => expression is OqlSelectExpression { Source: not null and not OqlSubqueryExpression },
        OqlClauses.Unnest => expression is OqlSelectExpression unnestQuery && unnestQuery.Unnest.Any(binding => !binding.IsLeft),
        OqlClauses.LeftUnnest => expression is OqlSelectExpression leftQuery && leftQuery.Unnest.Any(binding => binding.IsLeft),
        OqlClauses.Any => expression is OqlSelectExpression { Predicate: OqlQuantifiedExpression { IsEvery: false } },
        OqlClauses.Every => expression is OqlSelectExpression { Predicate: OqlQuantifiedExpression { IsEvery: true } },
        OqlClauses.Where => expression is OqlSelectExpression { Predicate: not null },
        OqlClauses.In => expression is OqlSelectExpression { Predicate: OqlInExpression },
        OqlClauses.Between => expression is OqlSelectExpression { Predicate: OqlBetweenExpression },
        OqlClauses.Like => expression is OqlSelectExpression { Predicate: OqlLikeExpression },
        OqlClauses.Case => expression is OqlSelectExpression select && select.Projections.Any(projection => ContainsCase(projection.Expression)),
        OqlClauses.GroupBy => expression is OqlSelectExpression { GroupBy.Count: > 0 },
        OqlClauses.GroupAs => expression is OqlSelectExpression { GroupAs: not null },
        OqlClauses.Having => expression is OqlSelectExpression { Having: not null },
        OqlClauses.OrderBy => expression is OqlSelectExpression { OrderBy.Count: > 0 },
        OqlClauses.Limit => expression is OqlSelectExpression { Limit: not null },
        OqlClauses.Offset => expression is OqlSelectExpression { Offset: not null },
        OqlClauses.CreateCollection => expression is OqlCreateCollectionExpression,
        OqlClauses.DropCollection => expression is OqlDropCollectionExpression,
        OqlClauses.CreateIndex => expression is OqlCreateIndexExpression,
        OqlClauses.DropIndex => expression is OqlDropIndexExpression,
        _ => false,
    };

    private static bool ContainsCase(OqlExpression expression) => expression is OqlCaseExpression ||
        expression is OqlCallExpression call && call.Arguments.Any(ContainsCase);

    private static async Task SeedAsync(DocumentCollection collection, DocumentDatabaseSession session, CancellationToken cancellationToken)
    {
        (string Id, string Json)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2,"details":{"score":1},"tags":["blue"]}"""),
            ("b", """{"name":"beta","category":"a","amount":6,"details":{"score":3},"tags":["green"]}"""),
            ("c", """{"name":"gamma","category":"b","amount":4,"details":{"score":2},"tags":["blue"]}"""),
            ("d", """{"name":"delta","category":"a","amount":null,"tags":[]}"""),
        ];
        foreach (var (id, json) in documents)
        {
            await collection.PutAsync(session, id, Encoding.UTF8.GetBytes(json), cancellationToken: cancellationToken);
        }
    }

    private static async Task VerifyRowsAsync(DatabaseResult result, object?[][] expected, string context, CancellationToken cancellationToken,
        (int Row, int Column)[]? missing = null)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>(context);
        var actual = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(cancellationToken))
        {
            for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
            {
                // MISSING is an optional row capability (#1126): a row that cannot report it, such as
                // an EXPLAIN description row (#1104), has every field present.
                bool isMissing = row.SupportsMissing && row.IsMissing(ordinal);
                isMissing.ShouldBe(missing?.Contains((actual.Count, ordinal)) ?? false, context);
            }
            actual.Add(Enumerable.Range(0, rows.Columns.Count).Select(row.GetValue).ToArray());
        }
        actual.Count.ShouldBe(expected.Length, context);
        for (int index = 0; index < expected.Length; index++)
        {
            actual[index].Length.ShouldBe(expected[index].Length, context);
            for (int ordinal = 0; ordinal < expected[index].Length; ordinal++)
            {
                if (expected[index][ordinal] is JsonElement json)
                {
                    JsonElement.DeepEquals(actual[index][ordinal].ShouldBeOfType<JsonElement>(), json).ShouldBeTrue(context);
                }
                else { actual[index][ordinal].ShouldBe(expected[index][ordinal], context); }
            }
        }
    }

    private sealed record ExecutionCase(string Statement, object?[][] ExpectedRows,
        string[]? Setup = null, string? VerificationStatement = null, (int Row, int Column)[]? Missing = null,
        long ExpectedAffectedCount = 0, IReadOnlyDictionary<string, object?>? Parameters = null, long? StatementMemoryBudget = null);

    private static ExecutionCase UpsertCase()
    {
        using var replacement = JsonDocument.Parse("""{"name":"alpha2","category":"a"}""");
        using var added = JsonDocument.Parse("""{"name":"zeta","category":"z"}""");
        return new("UPSERT INTO items (KEY, VALUE) VALUES ('a', $replacement), ('z', $new)",
            [["a", "alpha2", null], ["b", "beta", 6m], ["c", "gamma", 4m], ["d", "delta", null], ["z", "zeta", null]],
            VerificationStatement: "SELECT META(d).id AS id, d.name AS name, d.amount AS amount FROM items d ORDER BY id",
            Missing: [(0, 2), (4, 2)], ExpectedAffectedCount: 2,
            Parameters: new Dictionary<string, object?> { ["replacement"] = replacement.RootElement.Clone(), ["new"] = added.RootElement.Clone() });
    }

    private static ExecutionCase InsertCase()
    {
        using var e = JsonDocument.Parse("""{"name":"epsilon","category":"z"}""");
        using var f = JsonDocument.Parse("""{"name":"phi","category":"z"}""");
        return new("INSERT INTO items (KEY, VALUE) VALUES ('e', $e), ('f', $f)", [["e", "epsilon"], ["f", "phi"]],
            VerificationStatement: "SELECT META(d).id AS id, d.name AS name FROM items d WHERE d.category = 'z' ORDER BY id",
            ExpectedAffectedCount: 2, Parameters: new Dictionary<string, object?> { ["e"] = e.RootElement.Clone(), ["f"] = f.RootElement.Clone() });
    }
}
