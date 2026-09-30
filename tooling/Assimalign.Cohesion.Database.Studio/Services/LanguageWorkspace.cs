using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>How a GQL statement is dispatched.</summary>
internal enum GraphResultMode
{
    /// <summary>Paths when the statement is a MATCH returning one bare variable, rows otherwise.</summary>
    Auto,
    Rows,
    Paths,
}

internal sealed record ExecuteOptions(bool StopOnError = true, GraphResultMode GraphMode = GraphResultMode.Auto);

/// <summary>A line in the catalog explorer.</summary>
internal sealed record CatalogLine(string Text, int Depth = 0, string? InsertText = null, bool IsHeader = false);

/// <summary>A canned script for the Samples picker.</summary>
internal sealed record SampleScript(string Name, string Text);

/// <summary>Shared machinery for the text-language workspaces (SQL, OQL, GQL).</summary>
internal abstract class LanguageWorkspace : ModelWorkspace
{
    protected LanguageWorkspace(StudioModel model, ConnectionMode mode, StudioEngines engines, EndPoint? wireEndPoint)
        : base(model, mode, engines, wireEndPoint)
    {
    }

    public abstract string LanguageName { get; }

    public abstract IReadOnlyList<SampleScript> Samples { get; }

    public virtual bool SupportsGraphResultMode => false;

    /// <summary>
    /// Runs <paramref name="editorText"/>[<paramref name="start"/>..+<paramref name="length"/>] statement by
    /// statement. Engine failures and exceptions become outcomes; this method only throws when no
    /// database is selected.
    /// </summary>
    public Task<List<StatementOutcome>> ExecuteScriptAsync(string editorText, int start, int length, ExecuteOptions options, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(async token =>
        {
            RequireDatabase();

            // WinUI's TextBox stores line breaks as a lone '\r'. Lexers end '--' comments at '\n', so
            // map every '\r' to '\n': length-preserving, so offsets still match the editor text.
            string script = editorText.Substring(start, length).Replace('\r', '\n');
            var outcomes = new List<StatementOutcome>();
            bool failed = false;

            foreach (ScriptStatement statement in ScriptSplitter.Split(script))
            {
                var outcome = new StatementOutcome { Statement = statement.Text, StatementOffset = start + statement.Offset };
                outcomes.Add(outcome);

                if (failed && options.StopOnError)
                {
                    outcome.Skipped = true;
                    outcome.Status = "Skipped";
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    await ExecuteStatementAsync(outcome, options, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    outcome.Failed = true;
                    outcome.Status = "Cancelled";
                    outcome.Error = "Cancelled by the user.";
                }
                catch (Exception exception)
                {
                    // Any engine/client failure is data for the Messages pane, never a crash.
                    outcome.Failed = true;
                    outcome.Status = "Error";
                    outcome.Error = ErrorText.Describe(exception);
                }

                outcome.ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                AddParserDiagnostics(outcome, editorText);
                failed |= outcome.Failed;
            }

            return outcomes;
        }, cancellationToken);

    /// <summary>Builds the catalog explorer tree for the current database.</summary>
    public Task<List<CatalogLine>> GetCatalogAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(token =>
        {
            RequireDatabase();
            return BuildCatalogAsync(token);
        }, cancellationToken);

    protected abstract Task ExecuteStatementAsync(StatementOutcome outcome, ExecuteOptions options, CancellationToken cancellationToken);

    protected abstract Task<List<CatalogLine>> BuildCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Parses locally with the language package so every diagnostic (not only the first error) is visible.</summary>
    protected abstract QueryStatement ParseLocally(string statement);

    /// <summary>Runs one catalog query through the same path as user statements and returns its table.</summary>
    protected async Task<TabularResult> QueryTableAsync(string statement, CancellationToken cancellationToken)
    {
        var outcome = new StatementOutcome { Statement = statement };
        await ExecuteStatementAsync(outcome, new ExecuteOptions(GraphMode: GraphResultMode.Rows), cancellationToken).ConfigureAwait(false);
        if (outcome.Failed || outcome.Table is null)
        {
            string detail = outcome.Error ?? string.Join("; ", outcome.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));
            throw new InvalidOperationException($"Catalog query failed ({statement}): {(detail.Length == 0 ? "no result set" : detail)}");
        }

        return outcome.Table;
    }

    protected static Task FillFromQueryResultAsync(StatementOutcome outcome, QueryResult result, CancellationToken cancellationToken)
        => QueryResultReader.FillAsync(outcome, result, cancellationToken);

    protected static TabularResult ToTable(IEnumerable<(string Name, string Type)> columns, IEnumerable<IReadOnlyList<object?>> rows)
        => QueryResultReader.ToTable(columns, rows);

    private void AddParserDiagnostics(StatementOutcome outcome, string editorText)
    {
        QueryStatement parsed;
        try
        {
            parsed = ParseLocally(outcome.Statement);
        }
        catch (Exception exception)
        {
            // A parser crash is itself a finding worth showing next to the statement.
            outcome.Diagnostics.Add(new DiagnosticInfo("parser", "Error", "(parser threw)", ErrorText.Describe(exception), null, null, string.Empty));
            return;
        }

        foreach (Diagnostic diagnostic in parsed.Diagnostics)
        {
            DiagnosticInfo info = QueryResultReader.ToInfo("parser", diagnostic, outcome.StatementOffset, editorText);
            bool duplicate = outcome.Diagnostics.Any(existing =>
                existing.Code == info.Code && existing.AbsoluteStart == info.AbsoluteStart && existing.Message == info.Message);
            if (!duplicate)
            {
                outcome.Diagnostics.Add(info);
            }
        }

        // Engine diagnostics were added before the editor text was known; give them positions too.
        for (int i = 0; i < outcome.Diagnostics.Count; i++)
        {
            DiagnosticInfo existing = outcome.Diagnostics[i];
            if (existing.AbsoluteStart is { } absolute && existing.Position.Length == 0)
            {
                outcome.Diagnostics[i] = existing with { Position = QueryResultReader.LineColumn(editorText, absolute) };
            }
        }
    }
}
