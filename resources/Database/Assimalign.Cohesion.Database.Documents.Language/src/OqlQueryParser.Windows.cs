using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private IReadOnlyList<OqlWindowDefinition> ParseWindowDefinitions()
    {
        List<OqlWindowDefinition> definitions = [];
        do
        {
            var start = Current;
            string name = Identifier();
            Expect("AS");
            Expect(TokenType.LeftParen, "'('");
            var specification = ParseWindowSpecification(Previous);
            definitions.Add(new OqlWindowDefinition(name, specification, Span(start, Previous)));
        } while (!Failed && Take(TokenType.Comma));
        return definitions.AsReadOnly();
    }

    private OqlWindowSpecification ParseWindowSpecification(Lexeme start)
    {
        string? reference = null;
        List<OqlExpression> partitions = [];
        List<OqlOrdering> ordering = [];
        if (!Is("PARTITION") && !Is("ORDER") && !IsWindowFrame() && Current.Type != TokenType.RightParen)
        { reference = Identifier(); }
        if (Take("PARTITION"))
        {
            Expect("BY");
            do { partitions.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
        }
        if (Take("ORDER"))
        {
            Expect("BY");
            do
            {
                var expression = ParseExpression();
                bool descending = Take("DESC");
                if (!descending) { Take("ASC"); }
                var placement = OqlNullPlacement.Default;
                if (Take("NULLS"))
                {
                    if (Take("FIRST")) { placement = OqlNullPlacement.First; }
                    else if (Take("LAST")) { placement = OqlNullPlacement.Last; }
                    else { Error("CD00013", ErrorMessages.NullPlacementExpected, Current); }
                }
                ordering.Add(new OqlOrdering(expression, descending, placement));
            } while (!Failed && Take(TokenType.Comma));
        }
        if (!Failed && IsWindowFrame())
        { _diagnostics.Add(QueryDiagnostics.UnsupportedClause("window " + Current.Text.ToUpperInvariant(), Profile.Language, Span(Current, Current))); }
        if (!Failed) { Expect(TokenType.RightParen, "')'"); }
        return new OqlWindowSpecification(partitions, ordering, reference, Span(start, Previous));
    }

    // WINDOW stays usable as an implicit source/group alias outside declaration heads.
    private bool IsWindowDeclaration() => Is("WINDOW") && _position + 2 < _tokens.Count && IsWord(_tokens[_position + 2], "AS");

    private bool IsWindowFrame() => Is("ROWS") || Is("RANGE") || Is("GROUPS") || Is("EXCLUDE");

    private void ValidateWindowPositions(OqlExpression expression, bool allowed = false)
    {
        if (Failed) { return; }
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is OqlExplainExpression explain) { ValidateWindowPositions(explain.Statement); return; }
        if (expression is OqlSubqueryExpression subquery) { ValidateWindowPositions(subquery.Query); return; }
        if (expression is OqlUpdateExpression update)
        {
            foreach (var assignment in update.Assignments) { ValidateWindowPositions(assignment.Value); }
            if (update.Predicate is not null) { ValidateWindowPositions(update.Predicate); }
            if (update.Returning is not null) { ValidateWindowPositions(update.Returning); }
            return;
        }
        if (expression is OqlDeleteExpression delete)
        {
            if (delete.Predicate is not null) { ValidateWindowPositions(delete.Predicate); }
            if (delete.Returning is not null) { ValidateWindowPositions(delete.Returning); }
            return;
        }
        if (expression is OqlInsertExpression insert)
        {
            if (insert.Select is not null) { ValidateWindowPositions(insert.Select); }
            foreach (var pair in insert.Values) { ValidateWindowPositions(pair.Key); ValidateWindowPositions(pair.Value); }
            if (insert.Returning is not null) { ValidateWindowPositions(insert.Returning); }
            return;
        }
        if (expression is OqlUpsertExpression upsert)
        {
            if (upsert.Select is not null) { ValidateWindowPositions(upsert.Select); }
            foreach (var pair in upsert.Values) { ValidateWindowPositions(pair.Key); ValidateWindowPositions(pair.Value); }
            if (upsert.Returning is not null) { ValidateWindowPositions(upsert.Returning); }
            return;
        }
        if (expression is OqlSelectExpression query)
        {
            foreach (var projection in query.Projections) { ValidateWindowPositions(projection.Expression, true); }
            foreach (var order in query.OrderBy) { ValidateWindowPositions(order.Expression, true); }
            if (query.Source is not null) { ValidateWindowPositions(query.Source); }
            if (query.Predicate is not null) { ValidateWindowPositions(query.Predicate); }
            foreach (var key in query.GroupBy) { ValidateWindowPositions(key); }
            if (query.Having is not null) { ValidateWindowPositions(query.Having); }
            foreach (var binding in query.Bindings) { ValidateWindowPositions(binding.Expression); }
            foreach (var definition in query.Windows) { ValidateWindowPositions(definition.Specification); }
            return;
        }
        if (expression is OqlWindowExpression window)
        {
            if (!allowed || OqlFunctions.Find(window.Call.Name)?.WindowKind is null or OqlWindowFunctionKind.None)
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("window placement, nesting or function", Profile.Language, window.Location!));
                return;
            }
            foreach (var argument in window.Call.Arguments) { ValidateWindowPositions(argument); }
            if (window.Specification is not null) { ValidateWindowPositions(window.Specification); }
            return;
        }
        bool childrenAllowed = allowed && !(expression is OqlCallExpression call && OqlFunctions.Find(call.Name)?.IsAggregate == true) &&
            expression is not (OqlQuantifiedExpression or OqlComprehensionExpression);
        foreach (var child in OqlMergeRenderer.Children(expression)) { ValidateWindowPositions(child, childrenAllowed); }
    }
}
