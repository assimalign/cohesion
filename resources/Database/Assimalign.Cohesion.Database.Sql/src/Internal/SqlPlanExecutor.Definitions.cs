using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<QueryResult> ExecuteDefinitionAsync(SqlDefinitionPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var environment = _definitions.Functions;
        switch (plan.Expression)
        {
            case SqlCreateFunctionExpression create:
            {
                var definition = SqlDefinitionBinder.Declare(create, environment, _catalog.DefaultCollation, statement.ProvisioningSchema);
                EnsureAcyclic("F:" + definition.Signature, definition.Dependencies);
                await SelfCommitAsync(statement, _catalog.CreateFunctionAsync(definition, cancellationToken)).ConfigureAwait(false);
                break;
            }
            case SqlCreateDomainExpression create:
            {
                var definition = SqlDefinitionBinder.Declare(create, environment, _catalog.DefaultCollation, statement.ProvisioningSchema);
                EnsureAcyclic(SqlDefinitionBinder.DomainKey(definition.Name), definition.Dependencies);
                await SelfCommitAsync(statement, _catalog.CreateDomainAsync(definition, cancellationToken)).ConfigureAwait(false);
                break;
            }
            case SqlDropFunctionExpression drop:
            {
                string signature = SqlDefinitionBinder.Signature(drop.Name,
                    drop.ParameterTypes.Select(type => SqlDefinitionBinder.ResolveType(type, environment)));
                var definition = _catalog.Functions.FirstOrDefault(function => function.Signature.Equals(signature, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                {
                    if (environment.Catalog.GetOverloads(drop.Name).Any(function => function is not SqlExpressionFunction))
                    {
                        throw new DatabaseException($"{SqlDefinitionBinder.NativeCode}: Function '{signature}' is engine-registered and cannot be dropped through SQL.");
                    }
                    throw SqlDefinitionBinder.Refuse($"Function '{signature}' does not exist.");
                }
                CheckDefinitionOwner(definition.Name, definition.Owner, definition.OwningSchema, statement);
                EnsureNoDependents("F:" + signature);
                await SelfCommitAsync(statement, _catalog.DropFunctionAsync(signature, cancellationToken)).ConfigureAwait(false);
                break;
            }
            case SqlDropDomainExpression drop:
            {
                var definition = _catalog.Domains.FirstOrDefault(domain => domain.Name.Equals(drop.Name, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                {
                    if (environment.Types.Find(drop.Name) is not null)
                    {
                        throw new DatabaseException($"{SqlDefinitionBinder.NativeCode}: Type '{drop.Name}' is engine-registered and cannot be dropped through SQL.");
                    }
                    throw SqlDefinitionBinder.Refuse($"Domain '{drop.Name}' does not exist.");
                }
                CheckDefinitionOwner(definition.Name, definition.Owner, definition.OwningSchema, statement);
                EnsureNoDependents(SqlDefinitionBinder.DomainKey(drop.Name));
                await SelfCommitAsync(statement, _catalog.DropDomainAsync(drop.Name, cancellationToken)).ConfigureAwait(false);
                break;
            }
        }
        _definitions.RefreshDefinitions();
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private static void CheckDefinitionOwner(string name, DatabaseObjectOwner owner, string? schema, SqlStatementContext statement)
    {
        if (owner == DatabaseObjectOwner.Schema && !string.Equals(schema, statement.ProvisioningSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseObjectLockedException(name, schema!, "DROP");
        }
        if (owner == DatabaseObjectOwner.Adhoc && statement.ProvisioningSchema is not null)
        {
            throw SqlDefinitionBinder.Refuse($"Schema '{statement.ProvisioningSchema}' cannot adopt or drop ad-hoc definition '{name}'.");
        }
    }

    private void EnsureAcyclic(string key, IReadOnlyList<string> dependencies)
    {
        var graph = _catalog.Functions.ToDictionary(function => "F:" + function.Signature, function => function.Dependencies, StringComparer.OrdinalIgnoreCase);
        foreach (var domain in _catalog.Domains)
        {
            graph.Add(SqlDefinitionBinder.DomainKey(domain.Name), domain.Dependencies);
        }
        graph[key] = dependencies;
        var pending = new Stack<string>(dependencies);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out string? next))
        {
            if (key.Equals(next, StringComparison.OrdinalIgnoreCase))
            {
                throw SqlDefinitionBinder.Refuse($"Recursive SQL definition dependency at '{key}'.");
            }
            if (seen.Add(next) && graph.TryGetValue(next, out var children))
            {
                foreach (string child in children)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private void EnsureNoDependents(string key)
    {
        foreach (var function in _catalog.Functions)
        {
            if (function.Dependencies.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Dependent($"function '{function.Name}'");
            }
        }
        foreach (var domain in _catalog.Domains)
        {
            if (domain.Dependencies.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Dependent($"domain '{domain.Name}'");
            }
        }
        foreach (var table in _catalog.Tables)
        {
            var scope = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: _definitions.Functions);
            foreach (var column in table.Columns)
            {
                if (column.DomainName is { } name && key.Equals(SqlDefinitionBinder.DomainKey(name), StringComparison.OrdinalIgnoreCase))
                {
                    Dependent($"column '{table.Schema}.{table.Name}.{column.Name}'");
                }
                if (column.DefaultLiteral is { } text)
                {
                    Check(text, $"DEFAULT of '{table.Schema}.{table.Name}.{column.Name}'", scope);
                }
            }
            foreach (var check in table.Constraints.Where(constraint => constraint.CheckExpression is not null))
            {
                Check(check.CheckExpression!, $"CHECK '{check.Name}' on '{table.Schema}.{table.Name}'", scope);
            }
        }

        void Check(string text, string subject, SqlExpressionEvaluator scope)
        {
            var expression = SqlPersistedExpression.Load(text, subject);
            Visit(expression);
            void Visit(SqlExpression node)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                if (node is SqlFunctionCallExpression call && !call.FunctionName.Equals("COALESCE", StringComparison.OrdinalIgnoreCase))
                {
                    SqlFunction? resolved = null;
                    try { resolved = scope.ResolveFunction(call); }
                    catch (DatabaseException) { /* Missing native overload: retain a conservative name dependency. */ }
                    if (resolved is not null ? SqlDefinitionBinder.Key(resolved).Equals(key, StringComparison.OrdinalIgnoreCase)
                        : key.StartsWith("F:" + call.FunctionName.ToUpperInvariant() + "(", StringComparison.Ordinal)) { Dependent(subject); }
                }
                if (node is SqlCastExpression cast && key.Equals(SqlDefinitionBinder.DomainKey(cast.TargetType), StringComparison.OrdinalIgnoreCase)) { Dependent(subject); }
                foreach (var child in SqlPlanner.Children(node)) { Visit(child); }
            }
        }
        void Dependent(string subject) => throw new DatabaseException($"{SqlDefinitionBinder.DependencyCode}: Cannot drop '{key}': {subject} depends on it.");
    }
}
