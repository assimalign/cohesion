using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

internal static class OqlWindowCompatibilityMatrix
{
    // Complete clause tails keep the cross product useful; base decides each site's grammar.
    private static readonly string[] _followers =
    [
        "UNNEST c.b AS b", "NEST d ON META(d).id = c.k", "JOIN d ON META(d).id = c.k",
        "LEFT UNNEST c.b AS b", "LEFT OUTER UNNEST c.b AS b", "LEFT JOIN d ON META(d).id = c.k",
        "LEFT OUTER JOIN d ON META(d).id = c.k", "INNER JOIN d ON META(d).id = c.k",
        "INNER UNNEST c.b AS b", "UNION SELECT * FROM d", "INTERSECT SELECT * FROM d", "EXCEPT SELECT * FROM d",
        "RETURNING *", "LETTING n = 1", "WINDOW w AS ()", "SELECT * FROM d", "FROM d", "WHERE TRUE",
        "GROUP BY c.k", "GROUP AS members", "HAVING TRUE", "ORDER BY c.k", "LIMIT 1", "OFFSET 0",
        "AS n", "ON KEY s.k", "USING d AS s", "WHEN MATCHED THEN DELETE", "SET c.k = 1", "UNSET c.k",
        "VALUES ('k',{})", ", c.k", ")", ";", ""
    ];

    internal static IEnumerable<string> Statements()
    {
        foreach (string word in OqlWindowFreeCorpus.Words)
        {
            for (int site = 0; site < 24; site++)
            {
                foreach (string follower in _followers)
                {
                    string tail = follower == ")" ? "" : follower;
                    string text = site switch
                    {
                        0 => $"SELECT * FROM c UNNEST COALESCE(c.a, []) {word} {tail}",
                        1 => $"SELECT * FROM c UNNEST c.a {word} {tail}",
                        2 => $"SELECT * FROM c UNNEST [1] {word} {tail}",
                        3 => $"SELECT * FROM c UNNEST (SELECT RAW x FROM d AS x) {word} {tail}",
                        4 => $"SELECT 1 AS {word} FROM c {tail}",
                        5 => $"SELECT * FROM c UNNEST COALESCE(c.a, []) AS {word} {tail}",
                        6 => $"SELECT * FROM c NEST d AS {word} ON META({word}).id = c.k {tail}",
                        7 => $"SELECT {word} FROM c GROUP BY LOWER(c.name) {word} {tail}",
                        8 => $"SELECT * FROM c GROUP BY c.k GROUP AS {word} {tail}",
                        9 => $"SELECT c.{word} FROM c {tail}",
                        10 => $"SELECT * FROM {word} {tail}",
                        11 => $"SELECT ANY {word} IN c.a SATISFIES {word}.k END FROM c {tail}",
                        12 => $"MERGE INTO c {word} {tail} USING d AS s ON KEY s.k WHEN MATCHED THEN DELETE",
                        13 => $"MERGE INTO c AS t USING d {word} {tail} ON KEY {word}.k WHEN MATCHED THEN DELETE",
                        14 => $"SELECT * FROM (SELECT * FROM c) {word} {tail}",
                        15 => $"INSERT INTO target (KEY k) SELECT 'k' AS k FROM c UNNEST COALESCE(c.a, []) {word} {tail}",
                        16 => $"UPSERT INTO target (KEY k) SELECT 'k' AS k FROM c UNNEST COALESCE(c.a, []) {word} {tail}",
                        17 => $"INSERT INTO target (KEY k) SELECT 'k' AS k FROM c GROUP BY LOWER(c.name) {word} {tail}",
                        18 => $"UPSERT INTO target (KEY k) SELECT 'k' AS k FROM c GROUP BY LOWER(c.name) {word} {tail}",
                        19 => $"SELECT MAX({word}.rank) AS m FROM c UNNEST COALESCE(c.a, []) {word} {tail}",
                        20 => $"SELECT * FROM c NEST {word} ON META({word}).id = c.k {tail}",
                        21 => $"SELECT * FROM c NEST d {word} ON META({word}).id = c.k {tail}",
                        22 => $"SELECT * FROM c JOIN d {word} ON META({word}).id = c.k {tail}",
                        _ => $"SELECT ARRAY {word}.k FOR {word} IN c.a END FROM c {tail}",
                    };
                    yield return follower == ")" ? $"SELECT * FROM ({text}) AS enclosed" : text;
                }
            }
        }
    }
}
