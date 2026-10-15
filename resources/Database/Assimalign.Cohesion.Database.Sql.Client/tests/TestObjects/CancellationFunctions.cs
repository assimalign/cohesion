using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

internal sealed class CancellationFunctions
{
    internal OperationCanceledException Failure { get; } = new("application cancellation", new CancellationToken(true));
    internal Action<CancellationToken>? BeforeThrow { get; set; }
    internal int Calls { get; private set; }
    internal CancellationToken LastToken { get; private set; }

    internal void Throw(CancellationToken token)
    {
        Calls++;
        LastToken = token;
        BeforeThrow?.Invoke(token);
        throw Failure;
    }

    internal void Register(SqlDatabaseEngineBuilder builder)
    {
        foreach (int arity in new[] { 0, 1, 2, 5 })
        {
            builder.Functions.Add(new Scalar(this, $"cancel_scalar{arity}", arity));
            builder.Functions.Add(new Aggregate(this, $"cancel_add{arity}", arity, "add"));
        }
        builder.Functions.Add(new Scalar(this, "cancel_fixed", 1, immutable: true));
        builder.Functions.Add(new Aggregate(this, "cancel_seed", 1, "seed"));
        builder.Functions.Add(new Aggregate(this, "cancel_finish", 1, "finish"));
        builder.Functions.Add(new Scalar(this, "cancel_check", 1, check: true, immutable: true));
        // A domain's check need not be registered as a standalone function.
        builder.Types.Add(SqlType.CreateDomain("engine_domain", SqlType.Integer,
            new Scalar(this, "cancel_engine_check", 1, check: true, immutable: true)));
    }

    internal static async Task PrepareAsync(SqlDatabaseSession session)
    {
        foreach (string sql in new[]
        {
            "CREATE TABLE t (id INT)",
            "INSERT INTO t VALUES (1)",
            "CREATE DOMAIN sql_domain AS INT CHECK (cancel_check(VALUE))",
            "CREATE FUNCTION wrapper(p INT) RETURNS INT RETURN cancel_scalar1(p)",
            "CREATE FUNCTION engine_argument(p engine_domain) RETURNS INT STRICT RETURN p",
            "CREATE FUNCTION sql_argument(p sql_domain) RETURNS INT STRICT RETURN p",
            "CREATE FUNCTION engine_result(p INT) RETURNS engine_domain RETURN p",
            "CREATE FUNCTION sql_result(p INT) RETURNS sql_domain RETURN p",
            "CREATE TABLE engine_values (id INT, v engine_domain DEFAULT 1)",
            "CREATE TABLE sql_values (id INT, v sql_domain DEFAULT 1)",
            "INSERT INTO engine_values VALUES (0, 0)",
            "INSERT INTO sql_values VALUES (0, 0)",
            "CREATE TABLE table_check (v INT CHECK (cancel_check(v)))",
            "CREATE TABLE function_default (id INT, v INT DEFAULT cancel_scalar1(1))",
        })
        {
            await session.ExecuteAsync(sql, null, CancellationToken.None);
        }
    }

    internal static IEnumerable<object[]> Cases()
    {
        foreach (int arity in new[] { 0, 1, 2, 5 })
        {
            string arguments = string.Join(", ", Enumerable.Repeat("id", arity));
            yield return [$"SELECT cancel_scalar{arity}({arguments}) FROM t", $"cancel_scalar{arity}"];
            yield return [$"SELECT cancel_add{arity}({(arity == 0 ? "*" : arguments)}) FROM t", $"cancel_add{arity}"];
        }
        yield return ["SELECT cancel_fixed(1) FROM t", "cancel_fixed"];
        yield return ["SELECT cancel_seed(id) FROM t", "cancel_seed"];
        yield return ["SELECT cancel_finish(id) FROM t", "cancel_finish"];
        yield return ["SELECT cancel_finish(id) FROM t WHERE id = 0", "cancel_finish"];
        yield return ["SELECT wrapper(id) FROM t", "cancel_scalar1"];
        yield return ["INSERT INTO table_check VALUES (1)", "cancel_check"];
        yield return ["INSERT INTO function_default (id) VALUES (1)", "cancel_scalar1"];
        foreach (string kind in new[] { "engine", "sql" })
        {
            string name = kind == "engine" ? "cancel_engine_check" : "cancel_check";
            yield return [$"SELECT CAST(id AS {kind}_domain) FROM t", name];
            yield return [$"SELECT {kind}_argument(id) FROM t", name];
            yield return [$"SELECT {kind}_argument(NULL) FROM t", name];
            yield return [$"SELECT {kind}_result(id) FROM t", name];
            yield return [$"INSERT INTO {kind}_values VALUES (1, 1)", name];
            yield return [$"INSERT INTO {kind}_values (id) VALUES (1)", name];
            yield return [$"INSERT INTO {kind}_values VALUES (1, NULL)", name];
            yield return [$"UPDATE {kind}_values SET v = 1 WHERE id = 0", name];
            yield return [$"ALTER TABLE t ADD COLUMN extra {kind}_domain DEFAULT 1", name];
        }
    }

    private sealed class Scalar : SqlScalarFunction
    {
        private readonly CancellationFunctions _owner;
        private readonly bool _check;

        internal Scalar(CancellationFunctions owner, string name, int arity, bool check = false, bool immutable = false)
            : base(name, Enumerable.Repeat(SqlType.Integer, arity).ToArray(), check ? SqlType.Boolean : SqlType.Integer,
                immutable ? SqlFunctionVolatility.Immutable : SqlFunctionVolatility.Volatile,
                SqlNullBehavior.CalledOnNullInput)
        {
            _owner = owner;
            _check = check;
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        {
            if (_check && !arguments.IsNull(0) && arguments.GetInt32(0) == 0)
            {
                return SqlValue.FromBoolean(true);
            }
            _owner.Throw(arguments.Context.CancellationToken);
            return default;
        }
    }

    private sealed class Aggregate : SqlAggregateFunction
    {
        private readonly CancellationFunctions _owner;
        private readonly string _phase;

        internal Aggregate(CancellationFunctions owner, string name, int arity, string phase)
            : base(name, Enumerable.Repeat(SqlType.Integer, arity).ToArray(), SqlType.Integer)
        {
            _owner = owner;
            _phase = phase;
        }

        protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        {
            if (_phase == "seed")
            {
                _owner.Throw(context.CancellationToken);
            }
            return new Accumulator(_owner, _phase, context.CancellationToken);
        }
    }

    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private readonly CancellationFunctions _owner;
        private readonly string _phase;
        private readonly CancellationToken _token;

        internal Accumulator(CancellationFunctions owner, string phase, CancellationToken token)
        {
            _owner = owner;
            _phase = phase;
            _token = token;
        }

        protected override void AddCore(scoped in SqlArguments arguments)
        {
            if (_phase == "add")
            {
                _owner.Throw(arguments.Context.CancellationToken);
            }
        }

        protected override SqlValue FinishCore()
        {
            if (_phase == "finish")
            {
                _owner.Throw(_token);
            }
            return SqlValue.FromInt32(1);
        }
    }
}
