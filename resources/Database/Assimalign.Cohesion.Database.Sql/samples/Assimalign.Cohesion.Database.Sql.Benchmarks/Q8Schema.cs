using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Benchmarks;

/// <summary>
/// Q8's schema: 50 tables, each with a key, three more columns, an index and, from the second on, a
/// reference to the table before it, declared on an engine builder over a file-backed root. The
/// typed schema maps one CLR row type per table, so the types are declared one by one (NativeAOT
/// cannot construct them at run time). With <c>checks</c>, every table also declares a CHECK over the
/// registered function <c>has_text</c> (Q8C, E2 only).
/// </summary>
internal static class Q8Schema
{
    /// <summary>The number of tables.</summary>
    internal const int Tables = 50;

    /// <summary>Creates a builder that declares the schema's database over a root.</summary>
    /// <param name="root">The engine's root path.</param>
    /// <param name="checks">Whether every table declares a CHECK over a registered function.</param>
    /// <returns>The builder.</returns>
    internal static SqlDatabaseEngineBuilder CreateBuilder(string root, bool checks)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-benchmark-q8");
        builder.Options.RootPath = root;
#if !SQL_BENCHMARK_BASELINE
        if (checks)
        {
            builder.Functions.Add(SqlScalarFunction.Create("has_text", static (string text) => text.Length > 0, SqlFunctionVolatility.Immutable));
        }
#endif
        builder.AddDatabase("q8", database => database.Schema(schema =>
        {
            schema.Table<R00>("t00", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); Check(table, checks, "t00"); });
            schema.Table<R01>("t01", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R00>(row => row.ParentId); Check(table, checks, "t01"); });
            schema.Table<R02>("t02", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R01>(row => row.ParentId); Check(table, checks, "t02"); });
            schema.Table<R03>("t03", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R02>(row => row.ParentId); Check(table, checks, "t03"); });
            schema.Table<R04>("t04", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R03>(row => row.ParentId); Check(table, checks, "t04"); });
            schema.Table<R05>("t05", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R04>(row => row.ParentId); Check(table, checks, "t05"); });
            schema.Table<R06>("t06", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R05>(row => row.ParentId); Check(table, checks, "t06"); });
            schema.Table<R07>("t07", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R06>(row => row.ParentId); Check(table, checks, "t07"); });
            schema.Table<R08>("t08", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R07>(row => row.ParentId); Check(table, checks, "t08"); });
            schema.Table<R09>("t09", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R08>(row => row.ParentId); Check(table, checks, "t09"); });
            schema.Table<R10>("t10", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R09>(row => row.ParentId); Check(table, checks, "t10"); });
            schema.Table<R11>("t11", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R10>(row => row.ParentId); Check(table, checks, "t11"); });
            schema.Table<R12>("t12", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R11>(row => row.ParentId); Check(table, checks, "t12"); });
            schema.Table<R13>("t13", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R12>(row => row.ParentId); Check(table, checks, "t13"); });
            schema.Table<R14>("t14", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R13>(row => row.ParentId); Check(table, checks, "t14"); });
            schema.Table<R15>("t15", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R14>(row => row.ParentId); Check(table, checks, "t15"); });
            schema.Table<R16>("t16", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R15>(row => row.ParentId); Check(table, checks, "t16"); });
            schema.Table<R17>("t17", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R16>(row => row.ParentId); Check(table, checks, "t17"); });
            schema.Table<R18>("t18", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R17>(row => row.ParentId); Check(table, checks, "t18"); });
            schema.Table<R19>("t19", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R18>(row => row.ParentId); Check(table, checks, "t19"); });
            schema.Table<R20>("t20", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R19>(row => row.ParentId); Check(table, checks, "t20"); });
            schema.Table<R21>("t21", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R20>(row => row.ParentId); Check(table, checks, "t21"); });
            schema.Table<R22>("t22", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R21>(row => row.ParentId); Check(table, checks, "t22"); });
            schema.Table<R23>("t23", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R22>(row => row.ParentId); Check(table, checks, "t23"); });
            schema.Table<R24>("t24", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R23>(row => row.ParentId); Check(table, checks, "t24"); });
            schema.Table<R25>("t25", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R24>(row => row.ParentId); Check(table, checks, "t25"); });
            schema.Table<R26>("t26", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R25>(row => row.ParentId); Check(table, checks, "t26"); });
            schema.Table<R27>("t27", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R26>(row => row.ParentId); Check(table, checks, "t27"); });
            schema.Table<R28>("t28", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R27>(row => row.ParentId); Check(table, checks, "t28"); });
            schema.Table<R29>("t29", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R28>(row => row.ParentId); Check(table, checks, "t29"); });
            schema.Table<R30>("t30", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R29>(row => row.ParentId); Check(table, checks, "t30"); });
            schema.Table<R31>("t31", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R30>(row => row.ParentId); Check(table, checks, "t31"); });
            schema.Table<R32>("t32", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R31>(row => row.ParentId); Check(table, checks, "t32"); });
            schema.Table<R33>("t33", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R32>(row => row.ParentId); Check(table, checks, "t33"); });
            schema.Table<R34>("t34", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R33>(row => row.ParentId); Check(table, checks, "t34"); });
            schema.Table<R35>("t35", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R34>(row => row.ParentId); Check(table, checks, "t35"); });
            schema.Table<R36>("t36", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R35>(row => row.ParentId); Check(table, checks, "t36"); });
            schema.Table<R37>("t37", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R36>(row => row.ParentId); Check(table, checks, "t37"); });
            schema.Table<R38>("t38", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R37>(row => row.ParentId); Check(table, checks, "t38"); });
            schema.Table<R39>("t39", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R38>(row => row.ParentId); Check(table, checks, "t39"); });
            schema.Table<R40>("t40", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R39>(row => row.ParentId); Check(table, checks, "t40"); });
            schema.Table<R41>("t41", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R40>(row => row.ParentId); Check(table, checks, "t41"); });
            schema.Table<R42>("t42", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R41>(row => row.ParentId); Check(table, checks, "t42"); });
            schema.Table<R43>("t43", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R42>(row => row.ParentId); Check(table, checks, "t43"); });
            schema.Table<R44>("t44", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R43>(row => row.ParentId); Check(table, checks, "t44"); });
            schema.Table<R45>("t45", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R44>(row => row.ParentId); Check(table, checks, "t45"); });
            schema.Table<R46>("t46", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R45>(row => row.ParentId); Check(table, checks, "t46"); });
            schema.Table<R47>("t47", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R46>(row => row.ParentId); Check(table, checks, "t47"); });
            schema.Table<R48>("t48", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R47>(row => row.ParentId); Check(table, checks, "t48"); });
            schema.Table<R49>("t49", table => { table.Key(row => row.Id); table.Column(row => row.Name); table.Column(row => row.Value); table.Column(row => row.ParentId); table.Index(row => row.Name); table.References<R48>(row => row.ParentId); Check(table, checks, "t49"); });
        }));
        return builder;
    }

    // Q8C's CHECK on every table; the selectors stay inline in each declaration, which the schema
    // builder reads as direct members of the row type.
    private static void Check<TRow>(SqlTableBuilder<TRow> table, bool checks, string name)
    {
#if !SQL_BENCHMARK_BASELINE
        if (checks)
        {
            table.Check($"ck_{name}", "has_text(Name) AND Value >= 0");
        }
#endif
    }

    private sealed record R00(long Id, string Name, long Value, long ParentId);
    private sealed record R01(long Id, string Name, long Value, long ParentId);
    private sealed record R02(long Id, string Name, long Value, long ParentId);
    private sealed record R03(long Id, string Name, long Value, long ParentId);
    private sealed record R04(long Id, string Name, long Value, long ParentId);
    private sealed record R05(long Id, string Name, long Value, long ParentId);
    private sealed record R06(long Id, string Name, long Value, long ParentId);
    private sealed record R07(long Id, string Name, long Value, long ParentId);
    private sealed record R08(long Id, string Name, long Value, long ParentId);
    private sealed record R09(long Id, string Name, long Value, long ParentId);
    private sealed record R10(long Id, string Name, long Value, long ParentId);
    private sealed record R11(long Id, string Name, long Value, long ParentId);
    private sealed record R12(long Id, string Name, long Value, long ParentId);
    private sealed record R13(long Id, string Name, long Value, long ParentId);
    private sealed record R14(long Id, string Name, long Value, long ParentId);
    private sealed record R15(long Id, string Name, long Value, long ParentId);
    private sealed record R16(long Id, string Name, long Value, long ParentId);
    private sealed record R17(long Id, string Name, long Value, long ParentId);
    private sealed record R18(long Id, string Name, long Value, long ParentId);
    private sealed record R19(long Id, string Name, long Value, long ParentId);
    private sealed record R20(long Id, string Name, long Value, long ParentId);
    private sealed record R21(long Id, string Name, long Value, long ParentId);
    private sealed record R22(long Id, string Name, long Value, long ParentId);
    private sealed record R23(long Id, string Name, long Value, long ParentId);
    private sealed record R24(long Id, string Name, long Value, long ParentId);
    private sealed record R25(long Id, string Name, long Value, long ParentId);
    private sealed record R26(long Id, string Name, long Value, long ParentId);
    private sealed record R27(long Id, string Name, long Value, long ParentId);
    private sealed record R28(long Id, string Name, long Value, long ParentId);
    private sealed record R29(long Id, string Name, long Value, long ParentId);
    private sealed record R30(long Id, string Name, long Value, long ParentId);
    private sealed record R31(long Id, string Name, long Value, long ParentId);
    private sealed record R32(long Id, string Name, long Value, long ParentId);
    private sealed record R33(long Id, string Name, long Value, long ParentId);
    private sealed record R34(long Id, string Name, long Value, long ParentId);
    private sealed record R35(long Id, string Name, long Value, long ParentId);
    private sealed record R36(long Id, string Name, long Value, long ParentId);
    private sealed record R37(long Id, string Name, long Value, long ParentId);
    private sealed record R38(long Id, string Name, long Value, long ParentId);
    private sealed record R39(long Id, string Name, long Value, long ParentId);
    private sealed record R40(long Id, string Name, long Value, long ParentId);
    private sealed record R41(long Id, string Name, long Value, long ParentId);
    private sealed record R42(long Id, string Name, long Value, long ParentId);
    private sealed record R43(long Id, string Name, long Value, long ParentId);
    private sealed record R44(long Id, string Name, long Value, long ParentId);
    private sealed record R45(long Id, string Name, long Value, long ParentId);
    private sealed record R46(long Id, string Name, long Value, long ParentId);
    private sealed record R47(long Id, string Name, long Value, long ParentId);
    private sealed record R48(long Id, string Name, long Value, long ParentId);
    private sealed record R49(long Id, string Name, long Value, long ParentId);
}
