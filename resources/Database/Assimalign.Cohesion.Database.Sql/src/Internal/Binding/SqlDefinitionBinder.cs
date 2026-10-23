using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Declaration admission, dependency identity and reopen binding for SQL definitions.</summary>
internal static class SqlDefinitionBinder
{
    internal const string DefinitionCode = "CS03301";
    internal const string DependencyCode = "CS03302";
    internal const string NativeCode = "CS03303";

    internal static SqlPlanningException Refuse(string message, Location? location = null)
        => new(DefinitionCode, message, location);

    private static SqlPlanningException MissingFunction(string name, Location? location)
        => new(SqlEvaluationException.UnregisteredFunctionCode, string.Format(CultureInfo.CurrentCulture, ErrorMessages.SqlFunctionMissing, name), location);

    // Reopened definitions retain canonical fragments, not their original declaration offsets.
    private static Location FragmentLocation(string text)
        => Location.Create(1, 1 + text.Count(character => character == '\n'), 0, text.Length);
    internal static string Signature(string name, IEnumerable<SqlType> types)
        => SqlObjectIdentity.Signature(name, types.Select(TypeIdentity));
    internal static string Key(SqlFunction function)
    {
        string signature = Signature(function.Name, function.Parameters);
        return "F:" + (function.VariadicParameter is { } variadic
            ? signature[..^1] + (function.Parameters.Count == 0 ? string.Empty : ",") + "VARIADIC:" +
                TypeIdentity(variadic) + ")"
            : signature);
    }

    internal static string DescribeIdentity(string identity)
    {
        int start = identity.LastIndexOf('(');
        var types = identity[(start + 1)..^1].Split(',');
        for (int index = 0; index < types.Length; index++)
        {
            string token = types[index];
            bool variadic = token.StartsWith("VARIADIC:", StringComparison.Ordinal);
            if (int.TryParse(variadic ? token[9..] : token, NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            {
                types[index] = (variadic ? "VARIADIC " : string.Empty) + SqlType.NameOf((DatabaseType)code);
            }
        }
        return identity[3..(start - 1)].Replace("\"\"", "\"") + "(" + string.Join(", ", types) + ")";
    }
    private static string TypeIdentity(SqlType type)
        => type.IsPseudo ? SqlObjectIdentity.Quote(type.Name) : ((int)type.Storage.Type).ToString(CultureInfo.InvariantCulture);

    internal static string TypeName(SqlType type)
        => type.IsDomain ? SqlObjectIdentity.Quote(type.Name) : type.Name;

    internal static string DomainKey(string name) => SqlObjectIdentity.Domain(name.ToUpperInvariant());

    internal static SqlType ResolveType(string name, SqlFunctionEnvironment environment, Location? location = null)
        => environment.Types.Resolve(name, out string? error) is { } type && error is null
            ? type : throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderResolveTypeSqlPlanning, name, error ?? ErrorMessages.UnknownTypeDetail), location ?? FragmentLocation(name));

    internal static SqlCatalogFunction Declare(SqlCreateFunctionExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        try { return DeclareFunction(create, environment, collation, schema); }
        catch (ArgumentException failure) { throw Refuse(failure.Message, create.Location); }
        catch (SqlPlanningException failure) { throw failure.At(create.Location); }
    }

    private static SqlCatalogFunction DeclareFunction(SqlCreateFunctionExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        var types = create.Parameters.Select(parameter => ResolveType(parameter.DataType, environment, create.Location)).ToArray();
        var result = ResolveType(create.ReturnType, environment, create.Location);
        string signature = Signature(create.Name, types);
        if (create.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Length)
        {
            throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDeclareFunctionSqlPlanning, create.Name), create.Location);
        }
        SqlFunctionVolatility volatility = Volatility(create.Volatility, create.Location);
        var placeholder = new SqlExpressionFunction(create.Name, types, result, volatility, create.IsStrict, new SqlBoundConstant(null));
        var collection = MergeFunctions(environment.Catalog, placeholder, create.Location);
        var scopeEnvironment = new SqlFunctionEnvironment(collection, environment.Database, environment.Types);
        var columns = create.Parameters.Select((parameter, index) => new SqlCatalogColumn(parameter.Name, types[index].Storage,
            domainName: types[index].IsDomain ? types[index].Name : null)).ToArray();
        var scope = new SqlExpressionEvaluator(columns, null, defaultCollation: collation, functions: scopeEnvironment,
            bindingContext: SqlBindingContext.Function);
        SqlPersistedExpression.Bind(create.Body, scope, declaring: true);
        SqlPlanner.ValidateExpression(create.Body, scope);
        var dependencies = Dependencies(create.Body, scope, volatility);
        foreach (var type in types.Append(result).Where(type => type.IsDomain))
        {
            if (UnresolvedDomainReason(type) is { } reason)
            {
                throw new SqlPlanningException(SqlEvaluationException.UnregisteredFunctionCode, reason, create.Location);
            }
            dependencies.Add(DomainKey(type.Name));
        }
        if (dependencies.Contains("F:" + signature))
        {
            throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDeclareFunctionSqlPlanning2, create.Name), create.Body.Location ?? create.Location);
        }
        string body = SqlPersistedExpression.Canonicalize(create.Body, $"Function '{create.Name}'");
        return new SqlCatalogFunction(create.Name, signature, create.Parameters.Select(parameter => parameter.Name).ToArray(),
            types.Select(TypeName).ToArray(), TypeName(result),
            create.Volatility, create.IsStrict, body, dependencies.Order(StringComparer.Ordinal).ToArray(),
            schema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, schema, FunctionCalls(create.Body, scope));
    }

    internal static SqlCatalogDomain Declare(SqlCreateDomainExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        try { return DeclareDomain(create, environment, collation, schema); }
        catch (ArgumentException failure) { throw Refuse(failure.Message, create.Location); }
        catch (SqlPlanningException failure) { throw failure.At(create.Location); }
    }

    private static SqlCatalogDomain DeclareDomain(SqlCreateDomainExpression create, SqlFunctionEnvironment environment, Collation collation, string? schema)
    {
        if (environment.Types.Find(create.Name) is not null)
        {
            throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDeclareDomainSqlPlanning, create.Name), create.Location);
        }
        var type = ResolveType(create.BaseType, environment, create.Location);
        if (type.IsDomain || type.IsPseudo)
        {
            throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDeclareDomainSqlPlanning2, create.Name), create.Location);
        }
        // The SQL factory preserves type and reserved-name checks for delimited SQL names.
        _ = SqlType.CreateSqlDomain(create.Name, type, null, null, false);
        string? literal = null;
        if (create.DefaultValue is { } value)
        {
            if (!SqlPersistedExpression.IsDefaultLiteral(value))
            {
                throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDeclareDomainSqlPlanning3, create.Name), value.Location ?? create.Location);
            }
            literal = SqlPersistedExpression.Canonicalize(value, $"DEFAULT of domain '{create.Name}'");
            if (value is not SqlLiteralExpression { LiteralType: SqlLiteralType.Null })
            {
                var column = new SqlCatalogColumn(create.Name, type.Storage, !create.IsNotNull, literal);
                _ = SqlPlanExecutor.ResolveDefault(column, SqlPersistedExpression.LoadDefaultValue(literal, create.Name));
            }
        }
        string? check = null;
        IReadOnlyList<string> calls = [];
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (create.Check is { } predicate)
        {
            var column = new SqlCatalogColumn("VALUE", type.Storage);
            var table = new SqlCatalogTable(0, SqlPlanner.DefaultSchema, create.Name, [column]);
            SqlPlanExecutor.ValidateCheck(predicate, table, collation, environment, SqlBindingContext.Domain);
            var scope = new SqlExpressionEvaluator([column], null, defaultCollation: collation, functions: environment,
                bindingContext: SqlBindingContext.Domain);
            dependencies = Dependencies(predicate, scope, SqlFunctionVolatility.Immutable);
            calls = FunctionCalls(predicate, scope);
            check = SqlPersistedExpression.Canonicalize(predicate, $"CHECK of domain '{create.Name}'");
        }
        return new SqlCatalogDomain(create.Name, type.Name, literal, create.IsNotNull, create.ConstraintName, check,
            dependencies.Order(StringComparer.Ordinal).ToArray(), schema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema, schema, calls, []);
    }

    internal static SqlFunctionEnvironment Load(SqlCatalog catalog, SqlFunctionEnvironment native, CancellationToken cancellationToken)
        => Load(catalog.Functions, catalog.Domains, catalog.DefaultCollation, native, cancellationToken);

    internal static SqlFunctionEnvironment Load(SqlCatalogSnapshot catalog, SqlFunctionEnvironment native, CancellationToken cancellationToken)
        => Load(catalog.Functions, catalog.Domains, catalog.DefaultCollation, native, cancellationToken);

    private static SqlFunctionEnvironment Load(IReadOnlyList<SqlCatalogFunction> storedFunctions, IReadOnlyList<SqlCatalogDomain> storedDomains,
        Collation collation, SqlFunctionEnvironment native, CancellationToken cancellationToken)
    {
        var functions = storedFunctions.ToDictionary(function => "F:" + function.Signature, StringComparer.OrdinalIgnoreCase);
        var domains = storedDomains.ToDictionary(domain => DomainKey(domain.Name), StringComparer.OrdinalIgnoreCase);
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
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (done.Contains(key) || !functions.ContainsKey(key) && !domains.ContainsKey(key))
            {
                return;
            }
            if (!active.Add(key))
            {
                throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlDefinitionBinderLoadSqlPlanning, key), functions.TryGetValue(key, out var recursive)
                    ? FragmentLocation(recursive.Body) : FragmentLocation(domains[key].CheckExpression ?? domains[key].BaseType));
            }
            functions.TryGetValue(key, out var function);
            foreach (string dependency in function is not null ? DefinitionDependencies(function) : DefinitionDependencies(domains[key]))
            {
                Visit(dependency);
            }
            if (function is not null)
            {
                var bound = Bind(function, environment, collation, cancellationToken);
                environment = new SqlFunctionEnvironment(MergeFunctions(environment.Catalog, bound), native.Database, environment.Types);
            }
            else
            {
                var domain = domains[key];
                if (environment.Types.Find(domain.Name) is not null)
                {
                    throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlDefinitionBinderLoadSqlPlanning2, domain.Name), FragmentLocation(domain.CheckExpression ?? domain.BaseType));
                }
                var type = Bind(domain, environment, collation, cancellationToken);
                environment = new SqlFunctionEnvironment(environment.Catalog, native.Database,
                    new SqlTypeCatalog(environment.Types.Append(type), environment.Types.Casts));
            }
            active.Remove(key);
            done.Add(key);
        }
    }

    internal static SqlExpressionFunction Bind(SqlCatalogFunction function, SqlFunctionEnvironment environment, Collation collation,
        CancellationToken cancellationToken, bool notNullCheck = false, SqlBindingContext bindingContext = SqlBindingContext.Function)
    {
        var location = FragmentLocation(function.Body);
        var types = function.ParameterTypes.Select(type => ResolveType(type, environment, location)).ToArray();
        var result = ResolveType(function.ReturnType, environment, location);
        if (types.Length != function.ParameterNames.Count || Signature(function.Name, types) != function.Signature)
        {
            throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlDefinitionBinderBindSqlPlanning, function.Name), location);
        }
        var expression = SqlPersistedExpression.Load(function.Body, $"Function '{function.Name}'");
        string? unavailableDomain = types.Append(result).Select(UnresolvedDomainReason).FirstOrDefault(reason => reason is not null);
        string? unresolved = null;
        SqlBoundExpression? body = null;
        // Resolve metadata and dependencies without folding. The complete environment can then
        // be published before any body preparation calls application code; unrelated statements
        // need only this metadata. Each body/collation owns its own retryable binding entry.
        var scope = CreateScope(collation, cancellationToken);
        try
        {
            if (unavailableDomain is not null) { throw new SqlPlanningException(SqlEvaluationException.UnregisteredFunctionCode, unavailableDomain, expression.Location); }
            SqlPersistedExpression.Bind(expression, scope);
            SqlPlanner.ValidateExpression(expression, scope, validateBooleanPositions: false);
            _ = Dependencies(expression, scope, Volatility(function.Volatility, location));
        }
        catch (DatabaseException failure) when (unavailableDomain is not null || failure is SqlFunctionResultMismatchException ||
            failure is SqlEvaluationException { IsBooleanRequired: true } ||
            HasUnavailableCall(expression, scope, Volatility(function.Volatility, location)))
        {
            if (failure is SqlEvaluationException { IsBooleanRequired: true } booleanFailure)
            {
                body = new SqlBoundFailure(() => throw booleanFailure.Duplicate());
            }
            else
            {
                unresolved = $"Function '{function.Name}': {failure.Message}";
                string reason = unresolved;
                body = new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(new SqlUnresolvedDefinition("SQL function", function.Name, reason)));
            }
        }
        return new SqlExpressionFunction(function.Name, types, result, Volatility(function.Volatility, location), function.IsStrict,
            body, unresolved, notNullCheck, bodyCollation: collation, bindBody: body is null ? BindBody : null);

        SqlBoundExpression BindBody(Collation inputCollation, CancellationToken bindingToken)
            => bindingContext == SqlBindingContext.Domain
                ? CreateScope(inputCollation, bindingToken).BindPredicate(expression)
                : CreateScope(inputCollation, bindingToken).Bind(expression);

        SqlExpressionEvaluator CreateScope(Collation inputCollation, CancellationToken bindingToken)
        {
            // A parameter inherits the invocation's collation; literals retain the database default.
            // PostgreSQL: src/backend/executor/functions.c:496-505.
            var columns = function.ParameterNames.Select((name, index) => new SqlCatalogColumn(name, types[index].Storage,
                collation: types[index].Storage.Type is DatabaseType.String or DatabaseType.Character ? inputCollation : null,
                domainName: types[index].IsDomain ? types[index].Name : null)).ToArray();
            var bodyScope = new SqlExpressionEvaluator(columns, null, defaultCollation: collation, functions: environment,
                cancellationToken: bindingToken, bindingContext: bindingContext);
            PinCalls(expression, bodyScope, function.BodyFunctionCalls);
            return bodyScope;
        }
    }

    internal static SqlType Bind(SqlCatalogDomain domain, SqlFunctionEnvironment environment, Collation collation, CancellationToken cancellationToken)
    {
        var type = ResolveType(domain.BaseType, environment, FragmentLocation(domain.CheckExpression ?? domain.BaseType));
        SqlExpressionFunction? check = null;
        if (domain.CheckExpression is not null || domain.IsNotNull)
        {
            var function = new SqlCatalogFunction(domain.Name + "_check", Signature(domain.Name + "_check", [type]), ["VALUE"], [type.Name],
                "BOOLEAN", "IMMUTABLE", false, domain.CheckExpression ?? "TRUE", domain.Dependencies, domain.Owner, domain.OwningSchema,
                domain.CheckFunctionCalls);
            check = Bind(function, environment, collation, cancellationToken, domain.IsNotNull, SqlBindingContext.Domain);
        }
        return SqlType.CreateSqlDomain(domain.Name, type, check, domain.DefaultLiteral, domain.IsNotNull);
    }

    // The stored preorder vector identifies every call, including repeated and nested calls.
    // PostgreSQL stores FuncExpr.funcid and derives dependencies from it
    // (src/backend/catalog/dependency.c:2008-2013). Here normalized signatures are the identities.
    internal static IReadOnlyList<string> FunctionCalls(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        var calls = new List<string>();
        Visit(expression);
        return calls;

        void Visit(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
            {
                calls.Add(Key(scope.ResolveFunction(call) ?? throw MissingFunction(call.FunctionName, call.Location)));
            }
            foreach (var child in SqlPlanner.Children(node)) { Visit(child); }
        }
    }

    internal static void PinCalls(SqlExpression expression, SqlExpressionEvaluator scope, IReadOnlyList<string> identities)
    {
        int position = 0;
        Visit(expression);
        if (position != identities.Count) { throw Refuse(ErrorMessages.DefinitionBinderPinCallsSqlPlanning, expression.Location); }

        void Visit(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
            {
                if (position == identities.Count || !identities[position].StartsWith("F:" + SqlObjectIdentity.Quote(call.FunctionName.ToUpperInvariant()) + "(", StringComparison.Ordinal))
                {
                    throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefinitionBinderPinCallsSqlPlanning2, call.FunctionName), call.Location);
                }
                scope.PinFunction(call, identities[position++]);
            }
            foreach (var child in SqlPlanner.Children(node)) { Visit(child); }
        }
    }

    internal static IEnumerable<string> DefinitionDependencies(SqlCatalogFunction function)
        => function.BodyFunctionCalls.Concat(function.Dependencies.Where(IsDomainDependency));

    internal static IEnumerable<string> DefinitionDependencies(SqlCatalogDomain domain)
        => domain.CheckFunctionCalls.Concat(domain.DefaultFunctionCalls).Concat(domain.Dependencies.Where(IsDomainDependency));

    private static bool IsDomainDependency(string key) => key.StartsWith("D:", StringComparison.Ordinal);

    internal static HashSet<string> Dependencies(SqlExpression expression, SqlExpressionEvaluator scope, SqlFunctionVolatility volatility)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(expression);
        return dependencies;

        void Visit(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
            {
                var function = scope.ResolveFunction(call) ?? throw MissingFunction(call.FunctionName, call.Location);
                if (function.Kind != SqlFunctionKind.Scalar || function.Volatility < volatility)
                {
                    throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BinderDependenciesSqlPlanning, function.Name, volatility), call.Location);
                }
                if (function is SqlExpressionFunction { Unresolved: { } reason })
                {
                    throw new SqlPlanningException(SqlEvaluationException.UnregisteredFunctionCode, string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.SqlFunctionUnresolved, function.Name, reason), call.Location);
                }
                dependencies.Add(Key(function));
            }
            if (node is SqlCastExpression cast && ResolveType(cast.TargetType, scope.Functions, cast.Location) is { IsDomain: true } domain)
            {
                if (UnresolvedDomainReason(domain) is { } reason)
                {
                    throw new SqlPlanningException(SqlEvaluationException.UnregisteredFunctionCode, reason, cast.Location);
                }
                dependencies.Add(DomainKey(domain.Name));
            }
            foreach (var child in SqlPlanner.Children(node))
            {
                Visit(child);
            }
        }
    }

    internal static bool HasUnavailableCall(SqlExpression expression, SqlExpressionEvaluator scope,
        SqlFunctionVolatility? requiredVolatility = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
        {
            try
            {
                var function = scope.ResolveFunction(call);
                // A recorded signature can still exist after its registration weakens. The stored
                // definition then remains readable, but cannot run under its original contract.
                if (function is null or SqlExpressionFunction { Unresolved: not null } ||
                    requiredVolatility is { } volatility && (function.Kind != SqlFunctionKind.Scalar || function.Volatility < volatility))
                {
                    return true;
                }
            }
            catch (DatabaseException) { return true; }
        }
        if (expression is SqlCastExpression cast && scope.Functions.Types.Resolve(cast.TargetType, out string? error) is { } target && error is null &&
            UnresolvedDomainReason(target) is not null)
        {
            return true;
        }
        return SqlPlanner.Children(expression).Any(child => HasUnavailableCall(child, scope, requiredVolatility));
    }

    private static string? UnresolvedDomainReason(SqlType type)
        => type.Check is SqlExpressionFunction { Unresolved: { } reason }
            ? $"Domain '{type.Name}' has an unresolved SQL CHECK ({reason})." : null;

    private static SqlFunctionVolatility Volatility(string value, Location? location = null) => value switch
    {
        "IMMUTABLE" => SqlFunctionVolatility.Immutable,
        "STABLE" => SqlFunctionVolatility.Stable,
        "VOLATILE" => SqlFunctionVolatility.Volatile,
        _ => throw Refuse(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefinitionBinderVolatilitySqlPlanning, value), location ?? FragmentLocation(value)),
    };

    private static SqlFunctionCatalog MergeFunctions(SqlFunctionCatalog existing, SqlFunction function, Location? location = null)
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
            throw new SqlPlanningException(SqlExecutionException.DuplicateFunctionCode, failure.Message,
                location ?? FragmentLocation(function.Name), failure);
        }
    }
}
