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
    private async Task<DatabaseResult> ExecuteDefinitionAsync(SqlDefinitionPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var environment = _definitions.Functions;
        switch (plan.Expression)
        {
            case SqlCreateFunctionExpression create:
            {
                var definition = SqlDefinitionBinder.Declare(create, environment, _catalog.DefaultCollation, statement.ProvisioningSchema);
                EnsureAcyclic("F:" + definition.Signature, definition.Dependencies);
                await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.CreateFunctionAsync(definition, cancellationToken) : _catalog.CreateFunctionAsync(definition, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
                break;
            }
            case SqlCreateDomainExpression create:
            {
                var definition = SqlDefinitionBinder.Declare(create, environment, _catalog.DefaultCollation, statement.ProvisioningSchema);
                EnsureAcyclic(SqlDefinitionBinder.DomainKey(definition.Name), definition.Dependencies);
                await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.CreateDomainAsync(definition, cancellationToken) : _catalog.CreateDomainAsync(definition, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
                break;
            }
            case SqlDropFunctionExpression drop:
            {
                var parameterTypes = drop.ParameterTypes.Select(type => SqlDefinitionBinder.ResolveType(type, environment)).ToArray();
                string signature = SqlDefinitionBinder.Signature(drop.Name, parameterTypes);
                var definition = _catalog.Functions.FirstOrDefault(function => function.Signature.Equals(signature, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                {
                    if (environment.Catalog.GetOverloads(drop.Name).Any(function => function is not SqlExpressionFunction &&
                        SqlDefinitionBinder.Key(function).Equals("F:" + signature, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new DatabaseException($"{SqlDefinitionBinder.NativeCode}: Function '{signature}' is engine-registered and cannot be dropped through SQL.");
                    }
                    throw SqlDefinitionBinder.Refuse($"Function '{signature}' does not exist.", drop.Location);
                }
                CheckDefinitionOwner(definition.Name, definition.Owner, definition.OwningSchema, statement);
                EnsureNoDependents("F:" + signature);
                await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropFunctionAsync(signature, cancellationToken) : _catalog.DropFunctionAsync(signature, parameterTypes.Select(SqlDefinitionBinder.TypeName).ToArray(), statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
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
                    throw SqlDefinitionBinder.Refuse($"Domain '{drop.Name}' does not exist.", drop.Location);
                }
                CheckDefinitionOwner(definition.Name, definition.Owner, definition.OwningSchema, statement);
                EnsureNoDependents(SqlDefinitionBinder.DomainKey(drop.Name));
                await SelfCommitAsync(statement, (statement.ScriptReceipt is null ? _catalog.DropDomainAsync(drop.Name, cancellationToken) : _catalog.DropDomainAsync(drop.Name, statement.ScriptReceipt, cancellationToken))).ConfigureAwait(false);
                break;
            }
        }
        _definitions.RefreshDefinitions(cancellationToken);
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
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
        var graph = _catalog.Functions.ToDictionary(function => "F:" + function.Signature, function => SqlDefinitionBinder.DefinitionDependencies(function).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var domain in _catalog.Domains)
        {
            graph.Add(SqlDefinitionBinder.DomainKey(domain.Name), SqlDefinitionBinder.DefinitionDependencies(domain).ToArray());
        }
        graph[key] = dependencies.ToArray();
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
        foreach (var view in _catalog.Views)
        {
            if (view.Dependencies.Contains(key, StringComparer.OrdinalIgnoreCase) || view.FunctionCalls.Contains(key, StringComparer.OrdinalIgnoreCase))
            { throw new SqlPlanningException("COHSQLD002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ViewDropDependent, key, view.Schema, view.Name)); }
        }
        foreach (var function in _catalog.Functions)
        {
            if (SqlDefinitionBinder.DefinitionDependencies(function).Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Dependent($"function '{function.Name}'");
            }
        }
        foreach (var domain in _catalog.Domains)
        {
            if (SqlDefinitionBinder.DefinitionDependencies(domain).Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Dependent($"domain '{domain.Name}'");
            }
        }
        foreach (var table in _catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.DomainName is { } name && key.Equals(SqlDefinitionBinder.DomainKey(name), StringComparison.OrdinalIgnoreCase))
                {
                    Dependent($"column '{table.Schema}.{table.Name}.{column.Name}'");
                }
                if (column.DefaultLiteral is { } text)
                {
                    Check(text, column.DefaultFunctionCalls, $"DEFAULT of '{table.Schema}.{table.Name}.{column.Name}'");
                }
            }
            foreach (var check in table.Constraints.Where(constraint => constraint.CheckExpression is not null))
            {
                Check(check.CheckExpression!, check.CheckFunctionCalls, $"CHECK '{check.Name}' on '{table.Schema}.{table.Name}'");
            }
        }

        void Check(string text, IReadOnlyList<string> calls, string subject)
        {
            if (calls.Contains(key, StringComparer.OrdinalIgnoreCase)) { Dependent(subject); }
            if (!key.StartsWith("D:", StringComparison.Ordinal)) { return; }
            var expression = SqlPersistedExpression.Load(text, subject);
            Visit(expression);
            void Visit(SqlExpression node)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                if (node is SqlCastExpression cast && SqlTypeRules.TrySplit(cast.TargetType, out string name, out _, out _) &&
                    key.Equals(SqlDefinitionBinder.DomainKey(name), StringComparison.OrdinalIgnoreCase)) { Dependent(subject); }
                foreach (var child in SqlPlanner.Children(node)) { Visit(child); }
            }
        }
        void Dependent(string subject) => throw new DatabaseException($"{SqlDefinitionBinder.DependencyCode}: Cannot drop '{key}': {subject} depends on it.");
    }
}
