using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Measures each advertised GQL clause against a running graph engine.</summary>
public sealed class GqlProfileExecutionTests
{
    private static readonly Guid _personLabelId = new("13333333-3333-3333-3333-333333333333");
    private static readonly Guid _robotLabelId = new("24444444-4444-4444-4444-444444444444");
    // Node identities follow insertion order: Alice, Bob, Isolated, Robo, then the unlabeled Ghost.
    private const string seed = "INSERT (a:Person {name: 'Alice', age: 42, active: TRUE})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob', age: 17, active: FALSE}), " +
        "(:Person {name: 'Isolated', age: 30}), (:Robot {name: 'Robo'})-[:SERVES]->({name: 'Ghost'})";

    // The profile drives execution. A profile addition without data here fails before any query runs.
    private static readonly IReadOnlyDictionary<string, ExecutionCase[]> _cases =
        new Dictionary<string, ExecutionCase[]>(StringComparer.OrdinalIgnoreCase)
        {
            [GqlClauses.Explain] =
            [
                new("EXPLAIN MATCH (a:Person) RETURN a",
                    [[0, null, "Match", null, null, "filter=False; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, "mode=Trail", null, null],
                     [2, 1, "LabelScan", "Person", null, "node=0; property=; value=NULL", 3L, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
                new("EXPLAIN MATCH (a:Person|Robot) RETURN a",
                    [[0, null, "Match", null, null, "filter=False; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, "mode=Trail", null, null],
                     [2, 1, "LabelScan", "Person|Robot", null, "node=0; property=; value=NULL; combine=union", 4L, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
                new("EXPLAIN MATCH (a) WHERE a IS LABELED Person RETURN a",
                    [[0, null, "Match", null, null, "filter=True; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, "mode=Trail", null, null],
                     [2, 1, "LabelScan", "Person", null, "node=0; property=; value=NULL", 3L, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
                new("EXPLAIN MATCH (a:Person)-[:KNOWS]->(b) RETURN a",
                    [[0, null, "Match", null, null, "filter=False; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, "mode=Trail", null, null],
                     [2, 1, "RelationshipTypeScan", "KNOWS", null, "edge=0; direction=Outgoing; postings=(type_id,relationship); combine=union; physical=visited_postings; viable=accepted_hops; physical_limit=1000000", 1L, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
                new("EXPLAIN MATCH (a)-[:KNOWS|SERVES]->(b) RETURN a",
                    [[0, null, "Match", null, null, "filter=False; matches_nothing=False", null, null],
                     [1, 0, "ExpandPath", null, null, "mode=Trail", null, null],
                     [2, 1, "RelationshipTypeScan", "KNOWS|SERVES", null, "edge=0; direction=Outgoing; postings=(type_id,relationship); combine=union; physical=visited_postings; viable=accepted_hops; physical_limit=1000000", 2L, null],
                     [3, 0, "Project", "a", null, "kind=Node; expression=a", null, null]]),
            ],
            [GqlClauses.PathSelector] =
            [
                new("MATCH ANY (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN a.name,b.name", [["Alice","Bob"]]),
                new("MATCH ALL (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN a.name,b.name", [["Alice","Bob"]]),
                new("MATCH p = ANY SHORTEST (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN path_length(p)", [[1L]]),
                new("MATCH p = ALL SHORTEST (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN path_length(p)", [[1L]]),
                new("MATCH p = SHORTEST 2 (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN path_length(p)", [[1L]]),
                new("MATCH p = SHORTEST 2 GROUPS (a:Person {name:'Alice'})->*(b:Person {name:'Bob'}) RETURN path_length(p)", [[1L]]),
            ],
            [GqlClauses.PathMode] =
            [
                new("MATCH WALK (a:Person {name:'Alice'})->{1,2}(b) RETURN b.name", [["Bob"]]),
                new("MATCH TRAIL (a:Person {name:'Alice'})->{1,2}(b) RETURN b.name", [["Bob"]]),
                new("MATCH SIMPLE (a:Person {name:'Alice'})->{1,2}(b) RETURN b.name", [["Bob"]]),
                new("MATCH ACYCLIC (a:Person {name:'Alice'})->{1,2}(b) RETURN b.name", [["Bob"]]),
            ],
            [GqlClauses.QuantifiedPath] =
            [
                new("MATCH (a)->{1,3}(b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
                new("MATCH (a)-[*1..3]->(b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
                new("MATCH (a) ((x)->(y)){1,3} (b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
                new("MATCH (a) ((x:Person|Robot)->(y)){1,3} (b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
            ],
            [GqlClauses.Match] =
            [
                new("MATCH (a:Person|Robot) RETURN a.name", [["Alice"], ["Bob"], ["Isolated"], ["Robo"]]),
                new("MATCH (a:Person&Robot) RETURN a.name", []),
                new("MATCH (a:Person) MATCH (a)-[:KNOWS]->(b) RETURN a.name, b.name", [["Alice", "Bob"]]),
                new("MATCH (a:Person)-[:KNOWS|SERVES]-(b) RETURN a.name, b.name", [["Alice", "Bob"], ["Bob", "Alice"]]),
                new("MATCH (a)-[r:KNOWS]->(b) MATCH (a)-[s:KNOWS]->(b) RETURN r.weight, s.weight", [[2L, 2L]]),
                new("MATCH (a:Person)-[:KNOWS]->(b) WHERE b.age > 50 RETURN a.name", []),
                new("MATCH (a:Person {name: $name}) RETURN a.name", [["Alice"]],
                    Parameters: new Dictionary<string, object?> { ["name"] = "Alice" }),
                new("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Bob", 2L]]),
                new("MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (b:Person {name: 'Bob'})-[r:KNOWS]-(a) RETURN a.name", [["Alice"]]),
                // ISO/IEC 39075 left-or-right and the abbreviated edges (#1139).
                new("MATCH (b:Person {name: 'Bob'})<-[r:KNOWS]->(a) RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person {name: 'Alice'})->(b) RETURN b.name", [["Bob"]]),
                new("MATCH (b:Person {name: 'Bob'})<-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (b:Person {name: 'Bob'})-(a) RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person {name: 'Alice'})<->(b) RETURN b.name", [["Bob"]]),
            ],
            [GqlClauses.OptionalMatch] =
            [
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a.name, b.name",
                    [["Alice", "Bob"], ["Bob", null], ["Isolated", null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) WHERE b.age > 50 RETURN a.name, b.name",
                    [["Alice", null], ["Bob", null], ["Isolated", null]]),
                new("OPTIONAL MATCH (n:Person {name: 'Nobody'}) RETURN n.name", [[null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) OPTIONAL MATCH (a)<-[:KNOWS]-(c) RETURN a.name, b.name, c.name",
                    [["Alice", "Bob", null], ["Bob", null, "Alice"], ["Isolated", null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) OPTIONAL MATCH (b)-[:KNOWS]->(c) RETURN a.name, b.name, c.name",
                    [["Alice", "Bob", null], ["Bob", null, null], ["Isolated", null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) MATCH (b)-[:KNOWS]->(c) RETURN a.name", []),
                new("MATCH (a:Person)-[r:KNOWS]->(b) OPTIONAL MATCH (a)-[s:KNOWS]->(b) RETURN r.weight, s.weight", [[2L, 2L]]),
                new("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b), (b)-[s:KNOWS]->(c) RETURN a.name, b.name, r.weight, c.name",
                    [["Alice", null, null, null]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a) WHERE FALSE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b {name: $name}) RETURN a.name, b.name",
                    [["Alice", "Bob"], ["Bob", null], ["Isolated", null]], Parameters: new Dictionary<string, object?> { ["name"] = "Bob" }),
                new("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a.name, b IS NULL, b:Person, b.age + 1",
                    [["Alice", false, true, 18L], ["Bob", true, null, null], ["Isolated", true, null, null]]),
                Mutation("MATCH (a:Person) OPTIONAL MATCH (a)-[r:KNOWS]->(b) DELETE r", 1,
                    new ExecutionCase("MATCH ()-[r:KNOWS]->() RETURN r.weight", [])),
            ],
            [GqlClauses.Exists] =
            [
                new("MATCH (a:Person) WHERE NOT EXISTS { MATCH (a)-[:KNOWS]->() } RETURN a.name ORDER BY a.name", [["Bob"],["Isolated"]]),
                new("MATCH (a:Person) RETURN a.name, EXISTS { (a)-[:KNOWS]->(b) WHERE b.age < $age } ORDER BY a.name", [["Alice",true],["Bob",false],["Isolated",false]], Parameters: new Dictionary<string, object?> { ["age"] = 18L }),
                new("OPTIONAL MATCH (a:MissingExists) RETURN EXISTS { (a)->() }, NOT EXISTS { (a)->() }", [[false,true]]),
                new("MATCH (a:Person) WHERE any(x IN [a] WHERE EXISTS { (x)->() }) RETURN a.name", [["Alice"]]),
            ],
            [GqlClauses.Case] =
            [
                new("MATCH (a:Person {name: 'Alice'}) RETURN CASE WHEN a.age = 42 THEN a.name ELSE 'other' END", [["Alice"]]),
                new("RETURN CASE 1 WHEN 1.0 THEN 'match' ELSE 'other' END", [["match"]]),
                new("RETURN COALESCE(NULL, 5), NULLIF(42, 42)", [[5L, null]]),
            ],
            [GqlClauses.Reduce] =
            [
                new("RETURN reduce(a=0,x IN [1,2,3] | a+x)", [[6L]]),
                new("RETURN all(x IN [1] WHERE reduce(a=1,y IN [1] | TRUE))", [[true]]),
                new("RETURN reduce(a=42,x IN [] | 1/0), reduce(a=42,x IN NULL | a+x)", [[42L, null]]),
                new("MATCH (a:Person) WHERE reduce(s=0,x IN [a.age,1] | s+x) > 40 RETURN a.name", [["Alice"]]),
            ],
            [GqlClauses.ListPredicate] =
            [
                new("LET x=TRUE MATCH (n:Person {name:'Alice'}) RETURN x AS old ORDER BY all(x IN [n] WHERE old)", [[true]]),
                new("LET x=[TRUE] MATCH (n:Person {name:'Alice'}) RETURN x AS old ORDER BY all(x IN [n] WHERE all(y IN old WHERE y))", [[new object?[] { true }]]),
                new("RETURN all(x IN [] WHERE TRUE), any(x IN [] WHERE TRUE), none(x IN [] WHERE TRUE), single(x IN [] WHERE TRUE)", [[true, false, true, false]]),
                new("RETURN all(x IN [TRUE,NULL] WHERE x), any(x IN [FALSE,NULL] WHERE x), none(x IN [FALSE,NULL] WHERE x), single(x IN [TRUE,NULL] WHERE x)", [[null, null, null, null]]),
                new("MATCH (a:Person) WHERE any(x IN [a.age] WHERE x > 40) RETURN a.name", [["Alice"]]),
            ],
            [GqlClauses.PatternComprehension] =
            [
                new("MATCH (a:Person {name:'Alice'}) RETURN [(a)-[:KNOWS]->(b) | b.name]", [[new object?[] { "Bob" }]]),
                new("RETURN [p=(:Person {name:'Alice'})-[:KNOWS]->(b) WHERE b.age < 18 | size(nodes(p))]", [[new object?[] { 2L }]]),
                new("OPTIONAL MATCH (a:MissingPattern) RETURN [(a)->(b) | b.name]", [[Array.Empty<object?>()]]),
                new("RETURN [p=(a)->{1,2}(b) | path_length(p)]", [[new object?[] { 1L, 1L }]]),
                new("RETURN [p=(a)<-+(b) | path_length(p)]", [[new object?[] { 1L, 1L }]]),
                new("RETURN [p=(a)<->*(b) WHERE path_length(p)>0 | path_length(p)]", [[new object?[] { 1L, 1L, 1L, 1L }]]),
            ],
            [GqlClauses.ListComprehension] =
            [
                new("RETURN [x IN [1,2,3] WHERE x > 1 | x * 2]", [[new object?[] { 4L, 6L }]]),
                new("RETURN [x IN NULL | x], [x IN [] WHERE NULL]", [[null, Array.Empty<object?>()]]),
            ],
            [GqlClauses.ListSlice] =
            [
                new("RETURN [1,2,3][-2..], [1,2,3][..-1], [1,2,3][1..2]",
                    [[new object?[] { 2L, 3L }, new object?[] { 1L, 2L }, new object?[] { 2L }]]),
            ],
            [GqlClauses.Set] =
            [
                Mutation("MATCH (a:Person {name:[x IN ['Alice'] | x][0]}) SET a.age=[x IN [a.age] | x+1][0]", 1,
                    new ExecutionCase("MATCH (a:Person {name:'Alice'}) RETURN a.age", [[43L]])),
                Mutation("MATCH (a:Person {name:'Alice'}) SET a.age=43", 1,
                    new ExecutionCase("MATCH (a:Person {name:'Alice'}) RETURN a.age", [[43L]])),
                Mutation("MATCH (a:Person {name:'Alice'}) SET a:Admin", 1,
                    new ExecutionCase("MATCH (a:Admin) RETURN a.name", [["Alice"]])),
                Mutation("MATCH ()-[r:KNOWS]->() SET r.weight=9", 1,
                    new ExecutionCase("MATCH ()-[r:KNOWS]->() RETURN r.weight", [[9L]])),
            ],
            [GqlClauses.Remove] =
            [
                Mutation("MATCH (a:Person {name:'Alice'}) REMOVE a.age", 1,
                    new ExecutionCase("MATCH (a:Person {name:'Alice'}) RETURN a.age", [[null]])),
                Mutation("MATCH (a:Robot) REMOVE a:Robot", 1,
                    new ExecutionCase("MATCH (a:Robot) RETURN a.name", [])),
                Mutation("MATCH ()-[r:KNOWS]->() REMOVE r.weight", 1,
                    new ExecutionCase("MATCH ()-[r:KNOWS]->() RETURN r.weight", [[null]])),
            ],
            [GqlClauses.SetOperation] = [new("RETURN 1 AS x UNION RETURN 1 AS x", [[1L]])],
            [GqlClauses.UnionDistinct] = [new("RETURN 1 AS x UNION DISTINCT RETURN 1.0 AS x UNION RETURN 2 AS x", [[1L], [2L]])],
            [GqlClauses.UnionAll] = [new("RETURN 1 AS x UNION ALL RETURN 1 AS x", [[1L], [1L]])],
            [GqlClauses.Return] =
            [
                new("RETURN [10,20,30][-1], {k:{j:1}}.k.j, [1] IN [[1]], [NULL] = [NULL]", [[30L, 1L, true, null]]),
                new("MATCH (a:Person) WHERE a.age IN $ages RETURN a.name", [["Alice"], ["Bob"]],
                    Parameters: new Dictionary<string, object?> { ["ages"] = new List<long> { 17, 42 } }),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.name AS person, a.age, a.absent", [["Alice", 42L, null]]),
                new("MATCH (a:Person), (b:Person) RETURN DISTINCT a.active", [[true], [false], [null]], Columns: ["a.active"]),
                new("MATCH (a:Person), (b:Person) RETURN ALL a.active",
                    [[true], [true], [true], [false], [false], [false], [null], [null], [null]]),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.age + 1 AS next, a.name || '!'", [[43L, "Alice!"]],
                    Columns: ["next", "a.name || '!'"]),
                new("RETURN 1 + 1 AS two", [[2L]], Columns: ["two"]),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a IS LABELED Person, a.active IS TRUE, a.absent IS NULL",
                    [[true, true, true]]),
                new("MATCH (a:Person) RETURN $p AS p", [[5], [5], [5]], Parameters: new Dictionary<string, object?> { ["p"] = 5 }),
                new("MATCH (a:Person {name: 'Alice'}) RETURN a.age + $d AS next", [[43L]],
                    Parameters: new Dictionary<string, object?> { ["d"] = 1 }, Columns: ["next"]),
                new("RETURN $p AS p", [[5]], Parameters: new Dictionary<string, object?> { ["p"] = 5 }),
                new("MATCH (a) RETURN $p", [[5], [5], [5], [5], [5]],
                    Parameters: new Dictionary<string, object?> { ["p"] = 5 }, Columns: ["$p"]),
            ],
            [GqlClauses.Next] =
            [
                new("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x*10 AS x", [[10L], [20L]]),
                new("LET x = $value RETURN x NEXT RETURN x + 1 AS y", [[6L]], Parameters: new Dictionary<string, object?> { ["value"] = 5L }),
            ],
            [GqlClauses.With] =
            [
                new("MATCH (a:Person) WITH a WHERE a.age > $age RETURN a.name ORDER BY a.name", [["Alice"], ["Isolated"]], Parameters: new Dictionary<string, object?> { ["age"] = 18L }),
                new("MATCH (a:Person) WITH count(a) AS c RETURN c", [[3L]]),
                new("MATCH (a:Person) WITH DISTINCT a.active AS act RETURN act", [[true], [false], [null]]),
                new("MATCH (a)-[r:KNOWS]->(b) WITH * RETURN b.name", [["Bob"]]),
            ],
            [GqlClauses.Let] =
            [
                new("LET x = $value RETURN x", [[5L]], Parameters: new Dictionary<string, object?> { ["value"] = 5L }),
            ],
            [GqlClauses.Filter] =
            [
                new("MATCH (a:Person) FILTER a.age > $age RETURN a.name", [["Alice"], ["Isolated"]], Parameters: new Dictionary<string, object?> { ["age"] = 18L }),
            ],
            [GqlClauses.For] =
            [
                new("FOR x IN $xs RETURN x", [[1L], [2L]], Parameters: new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, 2L } }),
            ],
            [GqlClauses.Unwind] =
            [
                new("UNWIND $xs AS x RETURN x", [[1L], [2L]], Parameters: new Dictionary<string, object?> { ["xs"] = new List<long> { 1, 2 } }),
            ],
            [GqlClauses.GroupBy] =
            [
                new("MATCH (a:Person) RETURN a.active, count(*) AS n GROUP BY a.active ORDER BY n DESC",
                    [[true, 1L], [false, 1L], [null, 1L]]),
            ],
            [GqlClauses.OrderBy] =
            [
                new("LET x=TRUE MATCH (n:Person {name:'Alice'}) RETURN x AS old ORDER BY [x IN [n] WHERE old | 1][0]", [[true]]),
                new("LET x=[TRUE] MATCH (n:Person {name:'Alice'}) RETURN x AS old ORDER BY [x IN [n] WHERE all(y IN old WHERE y) | 1][0]", [[new object?[] { true }]]),
                new("MATCH (a:Person) RETURN a.name ORDER BY a.age", [["Bob"], ["Isolated"], ["Alice"]]),
            ],
            [GqlClauses.Offset] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name OFFSET 1", [["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Skip] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name SKIP 1", [["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Limit] =
            [
                new("MATCH (a:Person) RETURN a.name ORDER BY a.name LIMIT 1", [["Alice"]]),
                new("MATCH (a)-[:KNOWS|SERVES]->() RETURN a.name LIMIT 1", [["Alice"]], Physical: 2, Postings: 2),
            ],
            [GqlClauses.Create] =
            [
                new("CREATE (:Person {name: $name})", [], 1,
                    [new("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])],
                    new Dictionary<string, object?> { ["name"] = "Cara" }),
                Mutation("CREATE (:Person {name: 'Cara'})", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])),
                Mutation("MATCH (a:Person {name: 'Bob'}), (b:Person {name: 'Isolated'}) CREATE (a)-[:KNOWS {weight: 3}]->(b)", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Bob'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Isolated", 3L]])),
            ],
            [GqlClauses.Insert] =
            [
                new("INSERT (n:ComprehensionProperty {k:[x IN [1] | x+10][0]}) RETURN n.k", [[11L]], 1),
                Mutation("INSERT (: \"L\"\"X\" {\"p\"\"q\": 7})-[:\"T\"\"Y\"]->()", 3,
                    new ExecutionCase("MATCH (\"a\"\"b\":\"L\"\"X\")-[:\"T\"\"Y\"]->() RETURN \"a\"\"b\".\"p\"\"q\" AS \"c\"\"d\"", [[7L]])),
                new("INSERT (:Person {name: $name})", [], 1,
                    [new("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])],
                    new Dictionary<string, object?> { ["name"] = "Cara" }),
                Mutation("INSERT (:Person {name: 'Cara'})", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Cara'}) RETURN a.name", [["Cara"]])),
                Mutation("MATCH (a:Person {name: 'Bob'}), (b:Person {name: 'Isolated'}) INSERT (a)<-[:KNOWS {weight: 3}]-(b)", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Isolated'})-[r:KNOWS]->(b) RETURN b.name, r.weight", [["Bob", 3L]])),
            ],
            [GqlClauses.Merge] =
            [
                Mutation("UNWIND [1,2,1] AS k MERGE (n:ComprehensionMerge {k:[x IN [k] | x*2][0]}) ON CREATE SET n.c=[x IN [k] | x+10][0] ON MATCH SET n.m=[x IN [k] | x+20][0]", 2,
                    new ExecutionCase("MATCH (n:ComprehensionMerge) RETURN n.k,n.c,n.m ORDER BY n.k", [[2L,11L,21L],[4L,12L,null]])),
                new("MERGE (n:Person {name:'Alice'}) ON MATCH SET n.age = n.age + 1 RETURN n.age", [[43L]], 1),
                new("MERGE (n:Person {name:'Alice'}) ON MATCH SET n.age=n.age+1 ON MATCH SET n.age=n.age+2 RETURN n.age", [[45L]], 1),
                new("MERGE (n:MergeRepeat) ON CREATE SET n.x=1 ON CREATE SET n.x=n.x+2 RETURN n.x", [[3L]], 1),
                new("MERGE (n:MergeLoop {id:1})-[:SELF]->(n) RETURN n.id", [[1L]], 2),
                Mutation("MERGE (n:Person {name:'New'}) ON CREATE SET n.age = 9", 1,
                    new ExecutionCase("MATCH (n:Person {name:'New'}) RETURN n.age", [[9L]])),
                new("FOR x IN [1,1,1] MERGE (n:MergeRow {id:x}) ON CREATE SET n.hits=1 ON MATCH SET n.hits=n.hits+1 RETURN n.hits", [[3L],[3L],[3L]], 1),
                Mutation("MERGE (:Person {name:'Alice'})-[:NEW_EDGE]-(:Person {name:'Bob'})", 3,
                    new ExecutionCase("MATCH (n:Person {name:'Alice'}) RETURN count(*)", [[2L]])),
            ],
            [GqlClauses.Delete] =
            [
                Mutation("MATCH (a:Person {name: 'Isolated'}) DELETE a", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Isolated'}) RETURN a.name", [])),
                Mutation("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) DELETE a, r", 2,
                    new ExecutionCase("MATCH (a:Person {name: 'Alice'}) RETURN a.name", []),
                    new ExecutionCase("MATCH (a)-[r:KNOWS]->(b) RETURN r.weight", []),
                    new ExecutionCase("MATCH (b:Person {name: 'Bob'}) RETURN b.name", [["Bob"]])),
            ],
            [GqlClauses.DetachDelete] =
            [
                Mutation("MATCH (a:Person {name: 'Alice'}) DETACH DELETE a", 1,
                    new ExecutionCase("MATCH (a:Person {name: 'Alice'}) RETURN a.name", []),
                    new ExecutionCase("MATCH (a)-[r:KNOWS]->(b) RETURN r.weight", []),
                    new ExecutionCase("MATCH (b:Person {name: 'Bob'}) RETURN b.name", [["Bob"]])),
            ],
            [GqlClauses.Where] =
            [
                new("MATCH (a:Person) WHERE a.age = @age RETURN a.name", [["Alice"]],
                    Parameters: new Dictionary<string, object?> { ["age"] = 42 }),
                new("MATCH (a:Person) WHERE (a.age >= 18 AND a.age <= 42) AND a.age > 17 AND a.age < 43 AND a.name <> 'Isolated' AND a.active = TRUE RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.absent = NULL RETURN a.name", []),
                new("MATCH (a:Person) WHERE UNKNOWN RETURN a.name", []),
                new("MATCH (a:Person) WHERE UNKNOWN IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.age = 17 OR a.age = 42 RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE a.age > 20 XOR a.age = 30 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE TRUE OR TRUE XOR TRUE RETURN a.name", []),
                new("MATCH (a:Person) WHERE TRUE XOR TRUE OR TRUE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT a.active RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.active RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.active IS NULL RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.age IS NOT NULL RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS TRUE RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT TRUE RETURN a.name", [["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS FALSE RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT FALSE RETURN a.name", [["Alice"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS UNKNOWN RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.active IS NOT UNKNOWN RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE a.age IN [17, 42] RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE 1 IN [1, NULL] RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (1 IN [2, NULL]) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name STARTS WITH 'Al' RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.name ENDS WITH 'ob' RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.name CONTAINS 'sol' RETURN a.name", [["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name || 'x' = 'Alicex' RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.name + 'x' = 'Bobx' RETURN a.name", [["Bob"]]),
                new("MATCH (a:Person) WHERE a.age + 1 = 43 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age - 2 = 40 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age * 2 = 84 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / 5 = 8 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE -a.age / 5 = -8 AND -a.age % 5 = -2 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / -5 = -8 AND a.age % -5 = 2 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age / 5.0 = 8.4 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE a.age % 5 = 2 RETURN a.name", [["Alice"], ["Bob"]]),
                new("MATCH (a:Person) WHERE -9223372036854775808 % -1 = 0 RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT (a.absent = 1) RETURN a.name", []),
                new("MATCH (a:Person) WHERE a.absent = 1 OR a.age = 42 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE (2 IN [1, NULL]) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (NULL IN []) IS FALSE RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE NOT (a.name < 1) RETURN a.name", []),
                new("MATCH (a:Person) WHERE a.name <> 1 RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (1 STARTS WITH '1') IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE ('1' ENDS WITH 1) IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE (NULL CONTAINS '1') IS UNKNOWN RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a.name STARTS WITH 'al' RETURN a.name", []),
                new("MATCH (a:Person) WHERE NOT a:Robot OR a:Missing RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
                new("MATCH (a:Person) WHERE a:Missing OR a.age = 42 RETURN a.name", [["Alice"]]),
                new("MATCH (a:Person) WHERE NOT a:Missing RETURN a.name", [["Alice"], ["Bob"], ["Isolated"]]),
            ],
            [GqlClauses.Show] =
            [
                new("SHOW LABELS", [["audit", _personLabelId, "Person"], ["audit", _robotLabelId, "Robot"]]),
            ],
            [GqlClauses.LabelExpression] =
            [
                new("MATCH (n:Person|Robot) RETURN n.name", [["Alice"], ["Bob"], ["Isolated"], ["Robo"]]),
                new("MATCH (n:!Person) RETURN n.name", [["Robo"], ["Ghost"]]),
                new("MATCH (n:!%) RETURN n.name", [["Ghost"]]),
                new("MATCH (n IS %&!Robot {active: TRUE}) RETURN n.name", [["Alice"]]),
                new("MATCH (a)-[r:KNOWS|SERVES]->(b) RETURN a.name, b.name", [["Alice", "Bob"], ["Robo", "Ghost"]]),
                new("MATCH (a)-[r:!KNOWS]->(b) RETURN a.name", [["Robo"]]),
                new("MATCH (n) WHERE n IS LABELED Robot|!% RETURN n.name", [["Robo"], ["Ghost"]]),
                new("MATCH (n) WHERE n:Person AND n IS NOT LABELED Robot AND n.age > 20 RETURN n.name", [["Alice"], ["Isolated"]]),
                new("MATCH (n) WHERE n IS LABELED Person&Person AND n.age > 20 RETURN n.name", [["Alice"], ["Isolated"]]),
                new("MATCH (a)->(b) WHERE b:Person RETURN a.name, b.name", [["Alice", "Bob"]]),
            ],
        };

    /// <summary>Every profile clause must have data, use that clause, and produce its expected engine result.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Every advertised GQL clause executes against a real engine")]
    public async Task Profile_AdvertisedClauses_AllHavePassingExecutionCasesAsync()
    {
        EnsureCoverage(GqlLanguageProfile.Instance.Clauses);
        foreach (string clause in GqlLanguageProfile.Instance.Clauses)
        {
            foreach (var executionCase in _cases[clause])
            {
                // Isolate each case's parsing, seeding and execution deadline: repeated engine
                // setup must not consume the following case's budget (timings in DESIGN.md).
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var token = cancellation.Token;
                var statement = new GqlQueryParser().Parse(executionCase.Statement).ShouldBeOfType<GqlQueryStatement>();
                statement.Diagnostics.ShouldBeEmpty($"{clause}: {executionCase.Statement}");
                UsesClause(clause, statement.GqlExpression).ShouldBeTrue($"The {clause} case must exercise its clause: {executionCase.Statement}");
                await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
                var database = await engine.CreateDatabaseAsync("audit", token);
                await using var session = await database.CreateSessionAsync(token);
                await GraphSchema.Open(database, session).SaveLabelAsync(new(_personLabelId, "Person"), token);
                await GraphSchema.Open(database, session).SaveLabelAsync(new(_robotLabelId, "Robot"), token);
                await session.ExecuteAsync(seed, cancellationToken: token);

                await ExecuteAndVerifyAsync(session, executionCase, clause, token);
            }
        }
    }

    /// <summary>Demonstrates that a newly advertised clause cannot silently escape the execution matrix.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - A profile addition without execution data fails conformance")]
    public void Profile_AdditionalUnmappedClause_FailsCoverage()
    {
        var clauses = GqlLanguageProfile.Instance.Clauses.Append("FUTURE GQL CLAUSE");
        var failure = Should.Throw<ShouldAssertException>(() => EnsureCoverage(clauses));
        failure.Message.ShouldContain("FUTURE GQL CLAUSE", Case.Sensitive);
    }

    /// <summary>The supported subset rejects broader GQL forms at parse time, before they reach planning.</summary>
    [Theory(DisplayName = "Cohesion Test [Graph] - Unsupported forms fail at parse time against a live engine")]
    [InlineData("MATCH (a) RETURN a {.name}")]
    [InlineData("INSERT (:Person {tags: [1, 2]})")]
    [InlineData("MATCH (a) DELETE a RETURN a")]
    [InlineData("SHOW DATABASES")]
    [InlineData("MATCH (a)~[r]~(b) RETURN a")]
    [InlineData("MATCH (a)<~(b) RETURN a")]
    public async Task Profile_UnsupportedForms_ReportCapabilityDiagnosticAsync(string source)
    {
        new GqlQueryParser().Parse(source).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
        await using var engine = GraphDatabaseEngine.Create("graph-engine", new());
        var database = await engine.CreateDatabaseAsync("boundary", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        error.Message.ShouldContain("CK00001", Case.Sensitive);
    }

    private static readonly IReadOnlyDictionary<string, ExecutionCase> _functionCases =
        new Dictionary<string, ExecutionCase>(StringComparer.OrdinalIgnoreCase)
        {
            ["element_id"] = new("MATCH (a:Person {name:'Alice'}) RETURN element_id(a)=element_id(a)", [[true]]),
            ["elementId"] = new("MATCH (a:Person {name:'Alice'}) RETURN elementId(a)=element_id(a)", [[true]]),
            ["labels"] = new("MATCH (a:Person {name:'Alice'}) RETURN labels(a)", [[new object?[] { "Person" }]]),
            ["type"] = new("MATCH ()-[r:KNOWS]->() RETURN type(r)", [["KNOWS"]]),
            ["properties"] = new("MATCH (a:Person {name:'Alice'}) RETURN properties(a).age", [[42L]]),
            ["keys"] = new("MATCH (a:Person {name:'Alice'}) RETURN keys(a)", [[new object?[] { "active", "age", "name" }]]),
            ["startNode"] = new("MATCH ()-[r:KNOWS]->() RETURN startNode(r).name", [["Alice"]]),
            ["endNode"] = new("MATCH ()-[r:KNOWS]->() RETURN endNode(r).name", [["Bob"]]),
            ["path_length"] = new("MATCH p=()-[:KNOWS]->() RETURN path_length(p)", [[1L]]),
            ["length"] = new("MATCH p=()-[:KNOWS]->() RETURN length(p)", [[1L]]),
            ["nodes"] = new("MATCH p=()-[:KNOWS]->() RETURN nodes(p)[0].name", [["Alice"]]),
            ["edges"] = new("MATCH p=()-[:KNOWS]->() RETURN type(edges(p)[0])", [["KNOWS"]]),
            ["relationships"] = new("MATCH p=()-[:KNOWS]->() RETURN type(relationships(p)[0])", [["KNOWS"]]),
            ["lower"] = new("RETURN lower('ALICE')", [["alice"]]),
            ["toLower"] = new("RETURN toLower('ALICE')", [["alice"]]),
            ["upper"] = new("RETURN upper('alice')", [["ALICE"]]),
            ["toUpper"] = new("RETURN toUpper('alice')", [["ALICE"]]),
            ["trim"] = new("RETURN trim(' x ')", [["x"]]),
            ["substring"] = new("RETURN substring('A😀B',1,1)", [["😀"]]),
            ["replace"] = new("RETURN replace('a-b','-','+')", [["a+b"]]),
            ["char_length"] = new("RETURN char_length('A😀B')", [[3L]]),
            ["character_length"] = new("RETURN character_length('A😀B')", [[3L]]),
            ["size"] = new("RETURN size([1,2])", [[2L]]),
            ["abs"] = new("RETURN abs(-42)", [[42L]]),
            ["ceil"] = new("RETURN ceil(2.1)", [[3d]]),
            ["ceiling"] = new("RETURN ceiling(2.1)", [[3d]]),
            ["floor"] = new("RETURN floor(2.9)", [[2d]]),
            ["round"] = new("RETURN round(-2.5)", [[-3d]]),
            ["sqrt"] = new("RETURN sqrt(9)", [[3d]]),
            ["ln"] = new("RETURN ln(1)", [[0d]]),
            ["log"] = new("RETURN log(10,100)", [[2d]]),
            ["mod"] = new("MATCH (a:Person {name:'Alice'}) RETURN mod(a.age,5)", [[2L]]),
            ["toString"] = new("RETURN toString(42)", [["42"]]),
            ["toInteger"] = new("RETURN toInteger(2.5)", [[2L]]),
            ["toFloat"] = new("RETURN toFloat('2.5')", [[2.5d]]),
            ["toBoolean"] = new("RETURN toBoolean('true')", [[true]]),
            ["current_date"] = new("RETURN CURRENT_DATE=CURRENT_DATE", [[true]]),
            ["current_timestamp"] = new("RETURN CURRENT_TIMESTAMP=CURRENT_TIMESTAMP", [[true]]),
            ["reduce"] = new("RETURN reduce(a=0,x IN [1,2,3] | a+x)", [[6L]]),
            ["cast"] = new("RETURN CAST('42' AS INTEGER)", [[42]]),
            ["count"] = new("MATCH (a:Person) RETURN count(a)", [[3L]]),
            ["sum"] = new("MATCH (a:Person) RETURN sum(a.age)", [[89L]]),
            ["avg"] = new("MATCH (a:Person) RETURN avg(a.age)", [[89d / 3]]),
            ["min"] = new("MATCH (a:Person) RETURN min(a.age)", [[17L]]),
            ["max"] = new("MATCH (a:Person) RETURN max(a.age)", [[42L]]),
            ["collect_list"] = new("MATCH (a:Person) RETURN collect_list(a.active)", [[new object?[] { true, false }]]),
            ["collect"] = new("MATCH (a:Person) RETURN collect(a.active)", [[new object?[] { true, false }]]),
        };

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: every declared function has passing live evidence")]
    public async Task Profile_DeclaredFunctions_ShouldExecuteEveryCaseAsync()
    {
        // Arrange
        RequireFunctionCoverage(GqlLanguageProfile.Instance.Functions.ToArray(), _functionCases.Keys);
        await using var engine = GraphDatabaseEngine.Create("graph-functions", new());
        var database = await engine.CreateDatabaseAsync("functions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string name in GqlLanguageProfile.Instance.Functions.ToArray())
        {
            await ExecuteAndVerifyAsync(session, _functionCases[name], name, CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: coverage rejects an unmeasured declaration or a retired case")]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_FunctionCoverageMismatch_ShouldFail(bool addedDeclaration)
    {
        // Arrange
        var declarations = GqlLanguageProfile.Instance.Functions.ToArray();
        var cases = _functionCases.Keys;
        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireFunctionCoverage(
            addedDeclaration ? declarations.Append("FUTURE") : declarations,
            addedDeclaration ? cases : cases.Append("RETIRED")));
    }

    private static void RequireFunctionCoverage(IEnumerable<string> declarations, IEnumerable<string> cases)
    {
        declarations.Except(cases, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        cases.Except(declarations, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
    }

    private static void EnsureCoverage(IEnumerable<string> clauses)
    {
        foreach (string clause in clauses)
        {
            _cases.ContainsKey(clause).ShouldBeTrue($"Advertised GQL clause '{clause}' has no execution case.");
            _cases[clause].ShouldNotBeEmpty($"Advertised GQL clause '{clause}' has no execution case.");
        }
    }

    private static bool UsesClause(string clause, GqlQueryExpression query) => clause switch
    {
        GqlClauses.SetOperation or GqlClauses.UnionDistinct => query.Composite is { Operator: GqlCompositeOperator.Union },
        GqlClauses.UnionAll => query.Composite is { Operator: GqlCompositeOperator.UnionAll },
        GqlClauses.Merge => query.Stages.Any(stage => stage is GqlMergeStage),
        GqlClauses.Set => query.Stages.Any(stage => stage is GqlSetStage),
        GqlClauses.Remove => query.Stages.Any(stage => stage is GqlRemoveStage),
        GqlClauses.QuantifiedPath => query.MatchClauses.SelectMany(clause => clause.Paths).SelectMany(PatternPaths).Any(path => path.Relationships.Any(edge => edge.Quantifier is not null) || path.Elements?.Any(element => element is GqlParenthesizedPathPattern { Quantifier: not null }) == true),
        GqlClauses.PathSelector => query.MatchClauses.SelectMany(clause => clause.Paths).Any(path => path.Selector is not null),
        GqlClauses.PathMode => query.MatchClauses.SelectMany(clause => clause.Paths).Any(path => path.Mode is not null),
        GqlClauses.Explain => query.IsExplain,
        GqlClauses.Match => query.MatchClauses.Any(clause => !clause.Optional),
        GqlClauses.OptionalMatch => query.MatchClauses.Any(clause => clause.Optional),
        GqlClauses.Exists => query.Text?.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) == true,
        GqlClauses.Case => query.Projections.Any(item => item.Expression is GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression),
        GqlClauses.Reduce => query.Text?.Contains("reduce(", StringComparison.OrdinalIgnoreCase) == true,
        GqlClauses.ListPredicate => query.Projections.Any(projection => projection.Expression is GqlListPredicateExpression) ||
            query.OrderBy.Any(order => order.Expression is GqlListPredicateExpression) ||
            query.Text?.Contains("any(", StringComparison.OrdinalIgnoreCase) == true,
        GqlClauses.PatternComprehension => query.Projections.Any(item => item.Expression is GqlPatternComprehensionExpression),
        GqlClauses.ListComprehension => query.Projections.Any(item => item.Expression is GqlListComprehensionExpression),
        GqlClauses.ListSlice => query.Projections.Any(item => item.Expression is GqlListSliceExpression),
        GqlClauses.Let => query.Stages.Any(stage => stage is GqlLetStage),
        GqlClauses.Filter => query.Stages.Any(stage => stage is GqlFilterStage),
        GqlClauses.For => query.Stages.Any(stage => stage is GqlForStage { IsUnwind: false }),
        GqlClauses.Unwind => query.Stages.Any(stage => stage is GqlForStage { IsUnwind: true }),
        GqlClauses.Next => query.Stages.Any(stage => stage is GqlNextStage),
        GqlClauses.With => query.Stages.Any(stage => stage is GqlReturnStage { IsWith: true }),
        GqlClauses.Return => query.Projections.Count != 0 || query.ReturnStar,
        GqlClauses.GroupBy => query.GroupBy is not null,
        GqlClauses.OrderBy => query.OrderBy.Count != 0,
        GqlClauses.Offset => query.Offset is not null && HasKeyword(query.Text ?? string.Empty, "OFFSET"),
        GqlClauses.Skip => query.Offset is not null && HasKeyword(query.Text ?? string.Empty, "SKIP"),
        GqlClauses.Limit => query.Limit is not null,
        GqlClauses.Create => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "CREATE"),
        GqlClauses.Insert => query.Creates.Count != 0 && HasKeyword(query.Text ?? string.Empty, "INSERT"),
        GqlClauses.Delete => query.DeleteVariables.Count != 0 && !query.DetachDelete,
        GqlClauses.DetachDelete => query.DeleteVariables.Count != 0 && query.DetachDelete,
        GqlClauses.Where => query.MatchClauses.Any(clause => clause.Predicate is not null),
        GqlClauses.Show => query.CatalogSurface is not null,
        GqlClauses.LabelExpression => query.MatchClauses.SelectMany(clause => clause.Paths).Concat(query.Creates).Any(UsesLabelOperators) || query.MatchClauses.Any(clause => HasLabeledPredicate(clause.Predicate)),
        _ => false,
    };

    // A label expression beyond the conjunction :A:B the profile always ran: an operator, the
    // wildcard or a type alternative on a pattern element.
    private static bool UsesLabelOperators(GqlPathPattern path)
        => PatternPaths(path).Any(current => current.Nodes.Any(node => node.LabelExpression is { } labels && !IsNameConjunction(labels)) ||
           current.Relationships.Any(edge => edge.LabelExpression is { } labels && labels is not GqlLabelName));

    private static IEnumerable<GqlPathPattern> PatternPaths(GqlPathPattern root)
    {
        var pending = new Stack<GqlPathPattern>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            yield return path;
            foreach (var element in path.Elements ?? [])
            { if (element is GqlParenthesizedPathPattern group) { pending.Push(group.Pattern); } }
        }
    }

    private static bool IsNameConjunction(GqlLabelExpression expression) => expression switch
    {
        GqlLabelName => true,
        GqlLabelConjunction conjunction => conjunction.Operands.All(IsNameConjunction),
        _ => false,
    };

    private static bool HasLabeledPredicate(GqlExpression? expression) => expression switch
    {
        GqlLabeledPredicate => true,
        GqlLogicalExpression logical => logical.Operands.Any(HasLabeledPredicate),
        GqlBinaryExpression binary => HasLabeledPredicate(binary.Left) || HasLabeledPredicate(binary.Right),
        _ => false,
    };

    private static bool HasKeyword(string source, string keyword)
    {
        var lexer = new TokenLexer(source, GqlLanguageProfile.Instance.ToLexerOptions());
        foreach (var token in lexer)
        {
            if (token.Type == TokenType.Keyword && token.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    private static ExecutionCase Mutation(string statement, long affectedCount, params ExecutionCase[] verification)
        => new(statement, [], affectedCount, verification);

    private static async Task ExecuteAndVerifyAsync(GraphDatabaseSession session, ExecutionCase executionCase,
        string clause, CancellationToken cancellationToken)
    {
        string context = $"{clause}: {executionCase.Statement}";
        long postings = session.Database.Store.RelationshipTypeEntriesVisited;
        var result = await session.ExecuteAsync(executionCase.Statement, executionCase.Parameters, cancellationToken);
        result.Status.ShouldBe(DatabaseResultStatus.Success, context);
        if (executionCase.AffectedCount is { } affected)
        {
            result.AffectedCount.ShouldBe(affected, context);
        }
        else
        {
            (result is DatabaseResultSet).ShouldBeTrue(context);
            await using var rowset = (DatabaseResultSet)result;
            if (executionCase.Columns is { } columns) { rowset.Columns.Select(column => column.Name).ShouldBe(columns, context); }
            var rows = new List<object?[]>();
            await foreach (var row in rowset.GetRowsAsync(cancellationToken))
            {
                rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
            }
            rows.Count.ShouldBe(executionCase.Rows.Length, context);
            for (int index = 0; index < rows.Count; index++) { rows[index].ShouldBe(executionCase.Rows[index], context); }
        }
        if (executionCase.Physical is { } physical) { session.LastStatementMetrics!.Physical.ShouldBe(physical, context); }
        if (executionCase.Postings is { } reads) { (session.Database.Store.RelationshipTypeEntriesVisited - postings).ShouldBe(reads, context); }
        foreach (var verification in executionCase.Verification ?? [])
        {
            await ExecuteAndVerifyAsync(session, verification, clause, cancellationToken);
        }
    }

    private sealed record ExecutionCase(string Statement, object?[][] Rows, long? AffectedCount = null,
        ExecutionCase[]? Verification = null, IReadOnlyDictionary<string, object?>? Parameters = null, string[]? Columns = null,
        long? Physical = null, long? Postings = null);
}
