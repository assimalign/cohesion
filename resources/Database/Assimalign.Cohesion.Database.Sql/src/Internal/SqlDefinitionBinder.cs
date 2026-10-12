using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Declaration admission, dependency identity and reopen binding for SQL definitions.</summary>
internal static class SqlDefinitionBinder
{
    internal const string DefinitionCode = "COHSQLD001";
    internal const string DependencyCode = "COHSQLD002";
    internal const string NativeCode = "COHSQLD003";

    internal static DatabaseException Refuse(string message) => new($"{DefinitionCode}: {message}");
    internal static string Signature(string name, IEnumerable<SqlType> types)
        => name.ToUpperInvariant() + "(" + string.Join(",", types.Select(type => ((int)type.Storage.Type).ToString(CultureInfo.InvariantCulture))) + ")";
    internal static string Key(SqlFunction function) => "F:" + Signature(function.Name, function.Parameters);
    internal static string DomainKey(string name) => "D:" + name.ToUpperInvariant();

    internal static SqlType ResolveType(string name, SqlFunctionEnvironment environment)
        => environment.Types.Resolve(name, out string? error) is { } type && error is null
            ? type : throw Refuse($"Type '{name}' does not resolve: {error ?? "unknown type"}.");

    internal static SqlCatalogFunction Declare(SqlCreateFunctionExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        try { return DeclareFunction(create, environment, collation, schema); }
        catch (ArgumentException failure) { throw Refuse(failure.Message); }
    }

    private static SqlCatalogFunction DeclareFunction(SqlCreateFunctionExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        var types = create.Parameters.Select(parameter => ResolveType(parameter.DataType, environment)).ToArray();
        var result = ResolveType(create.ReturnType, environment);
        string signature = Signature(create.Name, types);
        if (create.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Length)
        {
            throw Refuse($"Function '{create.Name}' repeats a parameter name.");
        }
        SqlFunctionVolatility volatility = Volatility(create.Volatility);
        var placeholder = new SqlExpressionFunction(create.Name, types, result, volatility, create.IsStrict, new SqlBoundConstant(null));
        var collection = MergeFunctions(environment.Catalog, placeholder);
        var scopeEnvironment = new SqlFunctionEnvironment(collection, environment.Database, environment.Types);
        var columns = create.Parameters.Select((parameter, index) => new SqlCatalogColumn(parameter.Name, types[index].Storage)).ToArray();
        var scope = new SqlExpressionEvaluator(columns, null, defaultCollation: collation, functions: scopeEnvironment);
        SqlPersistedExpression.Bind(create.Body, scope);
        SqlPlanner.ValidateExpression(create.Body, scope);
        var dependencies = Dependencies(create.Body, scope, volatility);
        foreach (var type in types.Append(result).Where(type => type.IsDomain))
        {
            dependencies.Add(DomainKey(type.Name));
        }
        if (dependencies.Contains("F:" + signature))
        {
            throw Refuse($"Recursive SQL function '{create.Name}' is not supported.");
        }
        string body = SqlPersistedExpression.Canonicalize(create.Body, $"Function '{create.Name}'");
        return new SqlCatalogFunction(create.Name, signature, create.Parameters.Select(parameter => parameter.Name).ToArray(),
            types.Select(type => type.Name).ToArray(), result.Name,
            create.Volatility, create.IsStrict, body, dependencies.Order(StringComparer.Ordinal).ToArray(),
            schema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, schema);
    }

    internal static SqlCatalogDomain Declare(SqlCreateDomainExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        try { return DeclareDomain(create, environment, collation, schema); }
        catch (ArgumentException failure) { throw Refuse(failure.Message); }
    }

    private static SqlCatalogDomain DeclareDomain(SqlCreateDomainExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        if (environment.Types.Find(create.Name) is not null)
        {
            throw Refuse($"Type '{create.Name}' already exists; native and SQL domains share one namespace.");
        }
        var type = ResolveType(create.BaseType, environment);
        if (type.IsDomain || type.IsPseudo)
        {
            throw Refuse($"Domain '{create.Name}' must have a built-in base type.");
        }
        // The public factory owns identifier and type admission for both registration paths.
        _ = SqlType.CreateDomain(create.Name, type);
        string? literal = null;
        if (create.DefaultValue is { } value)
        {
            if (!SqlPersistedExpression.IsDefaultLiteral(value))
            {
                throw Refuse($"Domain '{create.Name}' requires a literal DEFAULT.");
            }
            literal = SqlPersistedExpression.Canonicalize(value, $"DEFAULT of domain '{create.Name}'");
            if (value is not SqlLiteralExpression { LiteralType: SqlLiteralType.Null })
            {
                var column = new SqlCatalogColumn(create.Name, type.Storage, !create.IsNotNull, literal);
                _ = SqlPlanExecutor.ResolveDefault(column, SqlPersistedExpression.LoadDefaultValue(literal, create.Name));
            }
        }
        string? check = null;
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (create.Check is { } predicate)
        {
            var column = new SqlCatalogColumn("VALUE", type.Storage);
            var table = new SqlCatalogTable(0, "dbo", create.Name, [column]);
            SqlPlanExecutor.ValidateCheck(predicate, table, collation, environment);
            var scope = new SqlExpressionEvaluator([column], null, defaultCollation: collation, functions: environment);
            dependencies = Dependencies(predicate, scope, SqlFunctionVolatility.Immutable);
            check = SqlPersistedExpression.Canonicalize(predicate, $"CHECK of domain '{create.Name}'");
        }
        return new SqlCatalogDomain(create.Name, type.Name, literal, create.IsNotNull, create.ConstraintName, check,
            dependencies.Order(StringComparer.Ordinal).ToArray(), schema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, schema);
    }

    internal static SqlFunctionEnvironment Load(SqlCatalog catalog, SqlFunctionEnvironment native)
    {
        var functions = catalog.Functions.ToDictionary(function => "F:" + function.Signature, StringComparer.OrdinalIgnoreCase);
        var domains = catalog.Domains.ToDictionary(domain => DomainKey(domain.Name), StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environment = native;
        foreach (string key in functions.Keys.Concat(domains.Keys))
        {
            Visit(key);
        }
        return environment;

        void Visit(string key)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (done.Contains(key) || !functions.ContainsKey(key) && !domains.ContainsKey(key))
            {
                return;
            }
            if (!active.Add(key))
            {
                throw Refuse($"Recursive SQL definition dependency at '{key}'.");
            }
            functions.TryGetValue(key, out var function);
            foreach (string dependency in function is not null ? function.Dependencies : domains[key].Dependencies)
            {
                Visit(dependency);
            }
            if (function is not null)
            {
                var bound = Bind(function, environment, catalog.DefaultCollation);
                environment = new SqlFunctionEnvironment(MergeFunctions(environment.Catalog, bound), native.Database, environment.Types);
            }
            else
            {
                var domain = domains[key];
                if (environment.Types.Find(domain.Name) is not null)
                {
                    throw Refuse($"SQL domain '{domain.Name}' conflicts with an engine-registered type.");
                }
                var type = Bind(domain, environment, catalog.DefaultCollation);
                environment = new SqlFunctionEnvironment(environment.Catalog, native.Database,
                    new SqlTypeCatalog(environment.Types.Append(type), environment.Types.Casts));
            }
            active.Remove(key);
            done.Add(key);
        }
    }

    internal static SqlExpressionFunction Bind(SqlCatalogFunction function, SqlFunctionEnvironment environment, Collation collation)
    {
        var types = function.ParameterTypes.Select(type => ResolveType(type, environment)).ToArray();
        var result = ResolveType(function.ReturnType, environment);
        if (types.Length != function.ParameterNames.Count || Signature(function.Name, types) != function.Signature)
        {
            throw Refuse($"Malformed persisted signature of '{function.Name}'.");
        }
        var columns = function.ParameterNames.Select((name, index) => new SqlCatalogColumn(name, types[index].Storage)).ToArray();
        var expression = SqlPersistedExpression.Load(function.Body, $"Function '{function.Name}'");
        var scope = new SqlExpressionEvaluator(columns, null, defaultCollation: collation, functions: environment);
        SqlBoundExpression body;
        string? unresolved = null;
        try
        {
            SqlPersistedExpression.Bind(expression, scope);
            SqlPlanner.ValidateExpression(expression, scope);
            _ = Dependencies(expression, scope, Volatility(function.Volatility));
            body = scope.Bind(expression);
        }
        catch (DatabaseException failure) when (failure is SqlFunctionResultMismatchException || HasUnavailableCall(expression, scope))
        {
            unresolved = $"Function '{function.Name}': {failure.Message}";
            string reason = unresolved;
            body = new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(new SqlUnresolvedDefinition("SQL function", function.Name, reason)));
        }
        return new SqlExpressionFunction(function.Name, types, result, Volatility(function.Volatility), function.IsStrict, body, unresolved);
    }

    internal static SqlType Bind(SqlCatalogDomain domain, SqlFunctionEnvironment environment, Collation collation)
    {
        var type = ResolveType(domain.BaseType, environment);
        SqlExpressionFunction? check = null;
        if (domain.CheckExpression is not null || domain.IsNotNull)
        {
            var function = new SqlCatalogFunction(domain.Name + "_check", Signature(domain.Name + "_check", [type]), ["VALUE"], [type.Name],
                "BOOLEAN", "IMMUTABLE", false, domain.CheckExpression ?? "TRUE", domain.Dependencies, domain.Owner, domain.OwningSchema);
            var expression = Bind(function, environment, collation);
            // Keep NOT NULL in the shared domain check path, including CAST(NULL AS domain).
            check = new SqlExpressionFunction(function.Name, [type], SqlType.Boolean, SqlFunctionVolatility.Immutable, false,
                new SqlBoundCall(expression, [new SqlBoundColumn(0)], null, new SqlBoundCollation(collation), environment.Database),
                expression.Unresolved, domain.IsNotNull);
        }
        return SqlType.CreateSqlDomain(domain.Name, type, check, domain.DefaultLiteral, domain.IsNotNull);
    }

    internal static HashSet<string> Dependencies(SqlExpression expression, SqlExpressionEvaluator scope, SqlFunctionVolatility volatility)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(expression);
        return dependencies;

        void Visit(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlFunctionCallExpression call && !call.FunctionName.Equals("COALESCE", StringComparison.OrdinalIgnoreCase))
            {
                var function = scope.ResolveFunction(call) ?? throw Refuse($"Unknown function '{call.FunctionName}'.");
                if (function.Kind != SqlFunctionKind.Scalar || function.Volatility < volatility)
                {
                    throw Refuse($"Function '{function.Name}' is not a scalar of volatility {volatility} or stronger.");
                }
                dependencies.Add(Key(function));
            }
            if (node is SqlCastExpression cast && ResolveType(cast.TargetType, scope.Functions) is { IsDomain: true } domain)
            {
                dependencies.Add(DomainKey(domain.Name));
            }
            foreach (var child in SqlPlanner.Children(node))
            {
                Visit(child);
            }
        }
    }

    internal static bool HasUnavailableCall(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlFunctionCallExpression call && !call.FunctionName.Equals("COALESCE", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (scope.ResolveFunction(call) is null or SqlExpressionFunction { Unresolved: not null })
                {
                    return true;
                }
            }
            catch (DatabaseException) { return true; }
        }
        return SqlPlanner.Children(expression).Any(child => HasUnavailableCall(child, scope));
    }

    private static SqlFunctionVolatility Volatility(string value) => value switch
    {
        "IMMUTABLE" => SqlFunctionVolatility.Immutable,
        "STABLE" => SqlFunctionVolatility.Stable,
        "VOLATILE" => SqlFunctionVolatility.Volatile,
        _ => throw Refuse($"Invalid volatility '{value}'."),
    };

    private static SqlFunctionCatalog MergeFunctions(SqlFunctionCatalog existing, SqlFunction function)
    {
        try
        {
            var collection = new SqlFunctionCollection(null);
            foreach (var native in existing.Where(candidate => !SqlStandardLibrary.Contains(candidate)))
            {
                collection.Add(native);
            }
            collection.Add(function);
            return collection.Freeze();
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            throw Refuse(failure.Message);
        }
    }
}
