using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

internal static class MigrationAlterColumnCases
{
    internal static IEnumerable<(string Name, CompiledSchemaColumn Previous, CompiledSchemaColumn Desired)> All()
    {
        CompiledSchemaColumn integer = new("Value", DatabaseType.Int32, true);
        (string Name, CompiledSchemaColumn Previous, CompiledSchemaColumn Desired)[] types =
        [
            ("integer", integer, integer with { Type = DatabaseType.Int64 }),
            ("varchar-widen", new("Value", DatabaseType.String, true, MaxLength: 4), new("Value", DatabaseType.String, true, MaxLength: 8)),
            ("varchar-narrow", new("Value", DatabaseType.String, true, MaxLength: 8), new("Value", DatabaseType.String, true, MaxLength: 4)),
            ("varchar-unbound", new("Value", DatabaseType.String, true, MaxLength: 4), new("Value", DatabaseType.String, true)),
            ("binary", new("Value", DatabaseType.Binary, true, MaxLength: 8), new("Value", DatabaseType.Binary, true, MaxLength: 4)),
            ("character", new("Value", DatabaseType.Character, true, MaxLength: 4), new("Value", DatabaseType.Character, true, MaxLength: 8)),
            ("precision", new("Value", DatabaseType.Decimal, true, Precision: 8, Scale: 2), new("Value", DatabaseType.Decimal, true, Precision: 7, Scale: 2)),
            ("scale", new("Value", DatabaseType.Decimal, true, Precision: 8, Scale: 3), new("Value", DatabaseType.Decimal, true, Precision: 8, Scale: 2)),
            ("float-alias", new("Value", DatabaseType.Float32, true, CustomType: "FLOAT(24)"), new("Value", DatabaseType.Float64, true, CustomType: "FLOAT(53)")),
            ("numeric-alias", new("Value", DatabaseType.Decimal, true, CustomType: "NUMERIC(8, 3)"), new("Value", DatabaseType.Decimal, true, CustomType: "DECIMAL(8, 2)")),
            ("domain", integer, integer with { CustomType = "quantity" }),
            ("domain-exit", integer with { CustomType = "quantity" }, integer),
            ("domain-switch", integer with { CustomType = "quantity" }, integer with { CustomType = "other_quantity" }),
            ("domain-case", integer with { CustomType = "quantity" }, integer with { CustomType = "Quantity" }),
            ("integer-alias", integer with { CustomType = "INTEGER" }, integer with { CustomType = "INT" }),
            ("domain-facets", new("Value", DatabaseType.Decimal, true, Precision: 8, Scale: 3, CustomType: "amount"), new("Value", DatabaseType.Decimal, true, Precision: 8, Scale: 2, CustomType: "amount")),
        ];
        foreach (var pair in types)
        {
            string literal = pair.Previous.Type is DatabaseType.String or DatabaseType.Character ? "'1'" :
                pair.Previous.Type == DatabaseType.Binary ? "'\\x01'" : "1";
            // Every combination of TYPE, DEFAULT and nullability, including old-default removal.
            for (int mask = 0; mask < 8; mask++)
            {
                yield return ($"{pair.Name}-{mask}",
                    pair.Previous with { DefaultLiteral = (mask & 4) == 0 ? null : literal },
                    pair.Desired with { DefaultLiteral = (mask & 1) == 0 ? null : literal, IsNullable = (mask & 2) == 0 });
                yield return ($"{pair.Name}-required-{mask}",
                    pair.Previous with { IsNullable = false, DefaultLiteral = (mask & 4) == 0 ? null : literal },
                    pair.Desired with { DefaultLiteral = (mask & 1) == 0 ? null : literal, IsNullable = (mask & 2) == 0 });
            }
            (string? Previous, string? Desired)[] explicitNulls = [(null, "NULL"), ("NULL", null), ("NULL", "NULL"), (literal, "NULL"), ("NULL", literal)];
            for (int index = 0; index < explicitNulls.Length; index++)
            {
                for (int nullability = 0; nullability < 4; nullability++)
                {
                    yield return ($"{pair.Name}-null-{index}-{nullability}",
                        pair.Previous with { DefaultLiteral = explicitNulls[index].Previous, IsNullable = (nullability & 1) == 0 },
                        pair.Desired with { DefaultLiteral = explicitNulls[index].Desired, IsNullable = (nullability & 2) == 0 });
                }
            }
        }
        string?[] defaults = [null, "NULL", "1", "+1", "2"];
        for (int source = 0; source < defaults.Length; source++)
        {
            for (int target = 0; target < defaults.Length; target++)
            {
                for (int nullable = 0; nullable < 2; nullable++)
                {
                    yield return ($"metadata-{source}-{target}-{nullable}", integer with { DefaultLiteral = defaults[source] },
                        integer with { DefaultLiteral = defaults[target], IsNullable = nullable == 0 });
                    yield return ($"metadata-required-{source}-{target}-{nullable}", integer with { IsNullable = false, DefaultLiteral = defaults[source] },
                        integer with { DefaultLiteral = defaults[target], IsNullable = nullable == 0 });
                }
            }
        }
        foreach (DatabaseType type in Enum.GetValues<DatabaseType>())
        {
            if (type == DatabaseType.Null) { continue; }
            yield return ($"storage-{type}", new("Value", type, true), new("Value", type, false));
            yield return ($"storage-nullable-{type}", new("Value", type, false), new("Value", type, true));
        }
        yield return ("keyword-domain", integer, integer with { CustomType = "select" });
        yield return ("reserved-domain", integer, integer with { CustomType = "LOCALTIME" });
        yield return ("pseudo-domain", integer, integer with { CustomType = "ANYELEMENT" });
        yield return ("storage-mismatch", integer, integer with { CustomType = "BIGINT" });
        yield return ("nonliteral-default", integer, integer with { DefaultLiteral = "1 + 2" });
    }

    internal static SqlCompiledSchema Schema(CompiledSchemaColumn column, bool destructive = true)
        => new(SqlCompiledSchema.CurrentFormat, "orders", destructive, [],
            [new("Orders", "Tests:Order", [new("Id", DatabaseType.Int64, false), column], new("PK_Orders", ["Id"]), [], [])], []);

    internal static SqlSchemaMigrationPlan Plan(CompiledSchemaColumn previous, CompiledSchemaColumn desired)
        => new(null, "target", [new(SqlSchemaMigrationOperationKind.AlterColumn, SqlSchemaMigrationSafety.Destructive,
            desired.Name, "Orders", column: desired, previousColumn: previous)]);
}
