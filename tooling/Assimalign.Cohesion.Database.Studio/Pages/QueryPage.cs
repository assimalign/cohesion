using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.UI.Input;

using Windows.System;
using Windows.UI.Core;

using Assimalign.Cohesion.Database.Documents;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A history entry: the script that ran and a one-line summary.</summary>
internal sealed record HistoryEntry(string Title, string Script);

/// <summary>
/// The SQL / OQL / GQL workspace: database bar, editor with Run (F5 / Ctrl+Enter, whole text or
/// selection), result grid, messages with every diagnostic, catalog explorer, history, samples and
/// explicit transactions. The Documents page also carries collection/document tools because OQL has
/// no data-mutation syntax.
/// </summary>
internal sealed class QueryPage : ContentPage
{
    private readonly StudioState _state;
    private readonly StudioModel _model;
    private readonly DatabaseBar _databaseBar;
    private readonly TransactionBar _transactionBar;
    private readonly Editor _editor;
    private readonly Picker _samples = Ui.Picker([], 280);
    private readonly Picker _graphMode = Ui.Picker(["Auto (paths for MATCH ... RETURN x)", "Rows (Execute)", "Paths (ExecutePaths)"], 250);
    private readonly CheckBox _stopOnError;
    private readonly Button _run;
    private readonly Button _cancel;
    private readonly ObservableCollection<CatalogLine> _catalog = [];
    private readonly Label _catalogStatus = Ui.Text(string.Empty, 11, color: Ui.Muted);
    private readonly Picker _resultPicker = Ui.Picker([], 420);
    private readonly Label _resultInfo = Ui.Text(string.Empty, 12, color: Ui.Muted);
    private readonly ResultTableView _table = new();
    private readonly MessageList _messages = new();
    private readonly ObservableCollection<HistoryEntry> _history = [];
    private List<StatementOutcome> _shown = [];
    private LanguageWorkspace? _workspace;
    private Microsoft.UI.Xaml.Controls.TextBox? _nativeEditor;
    private CancellationTokenSource? _cancellation;
    private bool _running;

    public QueryPage(StudioState state, StudioModel model)
    {
        _state = state;
        _model = model;

        _databaseBar = new DatabaseBar(ConfirmAsync, _messages.Error);
        _databaseBar.DatabaseChanged += async (_, _) =>
        {
            _transactionBar!.Refresh();
            await RefreshCatalogAsync();
        };
        _transactionBar = new TransactionBar(text => _messages.Add(text, MessageKind.Success), _messages.Error);

        _editor = new Editor
        {
            FontFamily = Ui.Mono,
            FontSize = 13,
            AutoSize = EditorAutoSizeOption.Disabled,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Text = Intro(model),
        };
        _editor.HandlerChanged += (_, _) => HookNativeEditor();

        _run = Ui.Button("Run  (F5 / Ctrl+Enter)", RunAsync, _messages.Error);
        _run.BackgroundColor = Ui.Accent;
        _run.TextColor = Colors.White;
        _cancel = Ui.Button("Cancel", () =>
        {
            _cancellation?.Cancel();
            return Task.CompletedTask;
        }, _messages.Error);
        _cancel.IsEnabled = false;

        _samples.SelectedIndexChanged += (_, _) =>
        {
            if (_workspace is not null && _samples.SelectedIndex >= 0 && _samples.SelectedIndex < _workspace.Samples.Count)
            {
                _editor.Text = _workspace.Samples[_samples.SelectedIndex].Text;
            }
        };

        _graphMode.IsVisible = model == StudioModel.Graph;
        _resultPicker.SelectedIndexChanged += (_, _) => ShowResult(_resultPicker.SelectedIndex);
        _table.RowTapped += async (_, text) =>
        {
            if (await DisplayAlertAsync("Row", text, "Copy", "Close"))
            {
                await Clipboard.Default.SetTextAsync(text);
            }
        };
        _messages.Locate += (_, item) => SelectInEditor(item.Offset ?? 0, item.Length ?? 0);

        View toolbar = Ui.Bar(
            _run,
            _cancel,
            Ui.CheckBox("stop on error", true, out _stopOnError),
            Ui.Text("Samples:", bold: true),
            _samples,
            _graphMode,
            _transactionBar);

        View resultHeader = Ui.Bar(
            Ui.Heading("Results"),
            _resultPicker,
            _resultInfo,
            Ui.Button("Copy TSV", async () =>
            {
                int index = _resultPicker.SelectedIndex;
                if (index >= 0 && index < _shown.Count && _shown[index].Table is { } table)
                {
                    await Clipboard.Default.SetTextAsync(table.ToTsv());
                }
            }, _messages.Error));

        View center = Ui.Rows(
            (new GridLength(2, GridUnitType.Star), new Border { Content = _editor, Stroke = Ui.Border, StrokeThickness = 1 }),
            (GridLength.Auto, resultHeader),
            (new GridLength(3, GridUnitType.Star), _table),
            (new GridLength(1.8, GridUnitType.Star), _messages));

        View left = model == StudioModel.Documents
            ? Ui.Rows((new GridLength(1, GridUnitType.Star), BuildCatalogPanel()), (GridLength.Auto, BuildDocumentTools()))
            : BuildCatalogPanel();

        var root = new Grid
        {
            Padding = new Thickness(8),
            RowSpacing = 4,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
        };

        Content = root;
        root.Add(_databaseBar, 0, 0);
        root.Add(toolbar, 0, 1);
        root.Add(Ui.Columns(
            (new GridLength(290), left),
            (GridLength.Star, center),
            (new GridLength(220), BuildHistoryPanel())), 0, 2);

        _state.WorkspacesChanged += (_, _) => Ui.OnMain(() => _ = BindAsync());
        _ = BindAsync();
    }

    private static string Intro(StudioModel model) => model switch
    {
        StudioModel.Sql => "/* SQL workspace. Pick a Samples entry, or type statements separated by ';'.\n   F5 / Ctrl+Enter runs the selection, or everything when nothing is selected. */\n",
        StudioModel.Documents => "/* OQL workspace (embedded only: Documents has no wire server/client yet).\n   OQL has no insert/update syntax: use Collection tools (left) to create collections and put documents,\n   or 'Seed sample data', then run the samples. F5 / Ctrl+Enter runs the selection or everything. */\n",
        StudioModel.Graph => "/* GQL workspace. INSERT/CREATE, MATCH ... RETURN, DELETE, SHOW LABELS | RELATIONSHIP TYPES | PROPERTY KEYS | INDEXES | OBJECT OWNERSHIP.\n   'Auto' sends MATCH ... RETURN <one variable> through ExecutePaths and renders paths as text. */\n",
        _ => string.Empty,
    };

    private async Task BindAsync()
    {
        try
        {
            _workspace = _state.Get<LanguageWorkspace>(_model);
            _transactionBar.Bind(_workspace);
            _catalog.Clear();
            _catalogStatus.Text = string.Empty;
            _samples.ItemsSource = _workspace?.Samples.Select(sample => sample.Name).ToList() ?? [];
            _samples.SelectedIndex = -1;
            await _databaseBar.BindAsync(_workspace, _state.Engines is null ? "Engines are starting (see the Workspace page)..." : $"{_model.DisplayName}: {_state.GetStatus(_model)}");
        }
        catch (Exception exception)
        {
            // A failed bind (for example an unreachable external server) is reported, not fatal.
            _messages.Error(exception);
        }
    }

    private View BuildCatalogPanel()
    {
        var list = new CollectionView
        {
            ItemsSource = _catalog,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation, Padding = new Thickness(2, 1) };
                label.BindingContextChanged += (_, _) =>
                {
                    if (label.BindingContext is CatalogLine line)
                    {
                        label.Text = line.Text;
                        label.Margin = new Thickness(line.Depth * 14, line.IsHeader ? 6 : 0, 0, 0);
                        label.FontAttributes = line.IsHeader ? FontAttributes.Bold : FontAttributes.None;
                        label.FontFamily = line.IsHeader ? null : Ui.Mono;
                        label.TextColor = line.IsHeader ? Ui.Accent : line.InsertText is not null ? Colors.Black : Ui.Muted;
                    }
                };
                return label;
            }),
        };

        list.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is CatalogLine { InsertText: { } insert })
            {
                InsertIntoEditor(insert);
            }

            list.SelectedItem = null;
        };

        return Ui.Panel(Ui.Rows(
            (GridLength.Auto, Ui.Bar(Ui.Heading("Catalog"), Ui.Button("Refresh", RefreshCatalogAsync, _messages.Error))),
            (GridLength.Auto, _catalogStatus),
            (GridLength.Star, list)));
    }

    private View BuildHistoryPanel()
    {
        var list = new CollectionView
        {
            ItemsSource = _history,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontSize = 11, FontFamily = Ui.Mono, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 4, Padding = new Thickness(2, 3) };
                label.BindingContextChanged += (_, _) =>
                {
                    if (label.BindingContext is HistoryEntry entry)
                    {
                        label.Text = entry.Title;
                    }
                };
                return label;
            }),
        };

        list.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is HistoryEntry entry)
            {
                _editor.Text = entry.Script;
            }

            list.SelectedItem = null;
        };

        return Ui.Panel(Ui.Rows(
            (GridLength.Auto, Ui.Bar(Ui.Heading("History"), Ui.Button("Clear", () =>
            {
                _history.Clear();
                return Task.CompletedTask;
            }))),
            (GridLength.Auto, Ui.Text("session only; click to reload", 11, color: Ui.Muted)),
            (GridLength.Star, list)));
    }

    private async Task RefreshCatalogAsync()
    {
        _catalog.Clear();
        if (_workspace?.CurrentDatabase is null)
        {
            _catalogStatus.Text = "select a database";
            return;
        }

        _catalogStatus.Text = "loading...";
        try
        {
            LanguageWorkspace workspace = _workspace;
            List<CatalogLine> lines = await Task.Run(() => workspace.GetCatalogAsync());
            foreach (CatalogLine line in lines)
            {
                _catalog.Add(line);
            }

            _catalogStatus.Text = $"{workspace.CurrentDatabase}: {lines.Count} entries; click a highlighted entry to insert a query";
        }
        catch (Exception exception)
        {
            // Catalog queries are ordinary statements; their failure is a finding, not a crash.
            _catalogStatus.Text = "catalog failed (see Messages)";
            _messages.Add("Catalog refresh failed:", MessageKind.Error);
            _messages.Error(exception);
        }
    }

    private async Task RunAsync()
    {
        if (_running)
        {
            return;
        }

        LanguageWorkspace? workspace = _workspace;
        if (workspace is null)
        {
            _messages.Add($"No {_model.DisplayName} workspace: {_state.GetStatus(_model)}", MessageKind.Error);
            return;
        }

        if (workspace.CurrentDatabase is null)
        {
            _messages.Add("Select or create a database first.", MessageKind.Error);
            return;
        }

        (string text, int start, int length) = GetRunRange();
        if (length == 0 || ScriptSplitter.IsBlank(text.Substring(start, length)))
        {
            _messages.Add("Nothing to run.", MessageKind.Warning);
            return;
        }

        _running = true;
        _run.IsEnabled = false;
        _cancel.IsEnabled = true;
        _cancellation = new CancellationTokenSource();
        var options = new ExecuteOptions(_stopOnError.IsChecked, (GraphResultMode)Math.Max(0, _graphMode.SelectedIndex));
        bool selection = length < text.Length;
        _messages.Clear();
        _messages.Add($"Running {(selection ? "the selection" : "the whole editor")} on '{workspace.CurrentDatabase}' ({workspace.Description})", MessageKind.Detail);

        long started = Stopwatch.GetTimestamp();
        try
        {
            CancellationToken token = _cancellation.Token;
            List<StatementOutcome> outcomes = await Task.Run(() => workspace.ExecuteScriptAsync(text, start, length, options, token));
            Render(outcomes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            AddHistory(text.Substring(start, length), outcomes);

            if (outcomes.Any(o => IsDdl(o.Statement) && !o.Failed))
            {
                await RefreshCatalogAsync();
            }
        }
        catch (Exception exception)
        {
            // ExecuteScriptAsync already turns statement failures into outcomes; this is the rest.
            _messages.Error(exception);
        }
        finally
        {
            _running = false;
            _run.IsEnabled = true;
            _cancel.IsEnabled = false;
            _cancellation.Dispose();
            _cancellation = null;
            _transactionBar.Refresh();
        }
    }

    private static bool IsDdl(string statement)
        => ScriptSplitter.FirstKeyword(statement) is "CREATE" or "DROP" or "ALTER" or "INSERT";

    private void Render(List<StatementOutcome> outcomes, double totalMs)
    {
        _shown = outcomes;
        var titles = new List<string>();
        int preferred = -1;
        for (int i = 0; i < outcomes.Count; i++)
        {
            StatementOutcome outcome = outcomes[i];
            string rows = outcome.Paths is not null ? $"{outcome.Paths.Count} paths" : outcome.Table is not null ? $"{outcome.Table.Rows.Count}{(outcome.Table.Truncated ? "+" : string.Empty)} rows" : "no rows";
            titles.Add($"#{i + 1} {rows}: {outcome.Preview}");
            if (outcome.Table is not null)
            {
                preferred = i;
            }

            MessageKind kind = outcome.Skipped ? MessageKind.Detail : outcome.Failed ? MessageKind.Error : MessageKind.Success;
            string affected = outcome.AffectedCount >= 0 ? $" | affected {outcome.AffectedCount}" : string.Empty;
            string count = outcome.Table is not null ? $" | {rows}" : string.Empty;
            _messages.Add($"#{i + 1} {outcome.Status}{affected}{count} | {outcome.ElapsedMs.ToString("F1", CultureInfo.InvariantCulture)} ms | {outcome.Preview}", kind, outcome.StatementOffset, outcome.Statement.Length);

            if (outcome.Note is not null)
            {
                _messages.Add($"    via {outcome.Note}", MessageKind.Detail);
            }

            foreach (DiagnosticInfo diagnostic in outcome.Diagnostics)
            {
                MessageKind diagnosticKind = diagnostic.Severity switch
                {
                    "Error" => MessageKind.Error,
                    "Warning" => MessageKind.Warning,
                    _ => MessageKind.Detail,
                };
                string where = diagnostic.Position.Length > 0 ? $" at {diagnostic.Position}" : string.Empty;
                _messages.Add($"    [{diagnostic.Source}] {diagnostic.Severity} {diagnostic.Code}{where}: {diagnostic.Message}", diagnosticKind, diagnostic.AbsoluteStart, diagnostic.Length);
            }

            if (outcome.Error is not null)
            {
                _messages.Add(Indent(outcome.Error), MessageKind.Error, outcome.StatementOffset, outcome.Statement.Length);
            }
        }

        int failed = outcomes.Count(o => o.Failed);
        int skipped = outcomes.Count(o => o.Skipped);
        _messages.Add(
            $"Done: {outcomes.Count} statement(s), {outcomes.Count - failed - skipped} ok, {failed} failed, {skipped} skipped, {totalMs.ToString("F1", CultureInfo.InvariantCulture)} ms total. Click a line to locate it in the editor.",
            failed > 0 ? MessageKind.Error : MessageKind.Success);

        _resultPicker.ItemsSource = titles;
        _resultPicker.SelectedIndex = preferred;
        ShowResult(preferred);
    }

    private static string Indent(string text) => "    " + text.ReplaceLineEndings("\n    ");

    private void ShowResult(int index)
    {
        if (index < 0 || index >= _shown.Count)
        {
            _table.Show(null);
            _resultInfo.Text = string.Empty;
            return;
        }

        StatementOutcome outcome = _shown[index];
        _table.Show(outcome.Table);
        _resultInfo.Text = outcome.Table is { } table
            ? $"{table.Rows.Count}{(table.Truncated ? $" (truncated at {StatementOutcome.MaxRows})" : string.Empty)} rows x {table.Columns.Count} columns. Click a row for full values."
            : string.Empty;
    }

    private void AddHistory(string script, List<StatementOutcome> outcomes)
    {
        int failed = outcomes.Count(o => o.Failed);
        string first = outcomes.FirstOrDefault()?.Preview ?? script.Trim();
        string title = $"{DateTime.Now:HH:mm:ss} [{_workspace?.CurrentDatabase}] {(failed > 0 ? "FAILED " : string.Empty)}{outcomes.Count} stmt\n{first}";
        _history.Insert(0, new HistoryEntry(title, script));
    }

    private (string Text, int Start, int Length) GetRunRange()
    {
        if (_nativeEditor is { } native)
        {
            string nativeText = native.Text ?? string.Empty;
            int start = Math.Clamp(native.SelectionStart, 0, nativeText.Length);
            int length = Math.Clamp(native.SelectionLength, 0, nativeText.Length - start);
            return length > 0 ? (nativeText, start, length) : (nativeText, 0, nativeText.Length);
        }

        string text = _editor.Text ?? string.Empty;
        int cursor = Math.Clamp(_editor.CursorPosition, 0, text.Length);
        int selected = Math.Clamp(_editor.SelectionLength, 0, text.Length - cursor);
        return selected > 0 ? (text, cursor, selected) : (text, 0, text.Length);
    }

    private void SelectInEditor(int offset, int length)
    {
        if (_nativeEditor is { } native)
        {
            int textLength = native.Text?.Length ?? 0;
            offset = Math.Clamp(offset, 0, textLength);
            length = Math.Clamp(Math.Max(length, 1), 0, textLength - offset);
            native.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            native.Select(offset, length);
            return;
        }

        _editor.Focus();
        _editor.CursorPosition = offset;
        _editor.SelectionLength = Math.Max(length, 1);
    }

    private void InsertIntoEditor(string insert)
    {
        string text = _editor.Text ?? string.Empty;
        string prefix = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? string.Empty : "\n";
        _editor.Text = text + prefix + insert;
        int start = (_nativeEditor?.Text ?? _editor.Text).Length - insert.Length;
        SelectInEditor(Math.Max(0, start), insert.Length);
    }

    private void HookNativeEditor()
    {
        if (_editor.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.TextBox box || ReferenceEquals(box, _nativeEditor))
        {
            return;
        }

        _nativeEditor = box;
        box.TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap;
        Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(box, Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto);
        box.PreviewKeyDown += (_, args) =>
        {
            bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (args.Key == VirtualKey.F5 || (args.Key == VirtualKey.Enter && control))
            {
                args.Handled = true;
                _ = RunAsync();
            }
            else if (args.Key == VirtualKey.Tab && !control && !shift)
            {
                args.Handled = true;
                int position = box.SelectionStart;
                string current = box.Text ?? string.Empty;
                box.Text = current.Remove(position, Math.Min(box.SelectionLength, current.Length - position)).Insert(position, "    ");
                box.SelectionStart = position + 4;
            }
        };
    }

    private Task<bool> ConfirmAsync(string message) => DisplayAlertAsync("Confirm", message, "Yes", "No");

    // ---------------------------------------------------------------- Documents collection tools

    private View BuildDocumentTools()
    {
        Entry collection = Ui.Entry("collection", 130, "items");
        Entry id = Ui.Entry("document id", 110);
        Entry version = Ui.Entry("expected version (optional)", 150);
        Editor json = new()
        {
            FontFamily = Ui.Mono,
            FontSize = 12,
            HeightRequest = 110,
            AutoSize = EditorAutoSizeOption.Disabled,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Placeholder = "{ \"name\": \"...\" }",
        };

        DocumentWorkspace Docs() => _workspace as DocumentWorkspace
            ?? throw new InvalidOperationException("The Documents workspace is not ready.");

        ulong? Version() => string.IsNullOrWhiteSpace(version.Text)
            ? null
            : ulong.Parse(version.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

        string Required(Entry entry, string what) => string.IsNullOrWhiteSpace(entry.Text)
            ? throw new InvalidOperationException($"Enter the {what}.")
            : entry.Text.Trim();

        return Ui.Panel(new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                Ui.Heading("Collection tools (DocumentCollection)"),
                Ui.Bar(
                    collection,
                    Ui.Button("Create", async () =>
                    {
                        await Docs().CreateCollectionAsync(Required(collection, "collection"));
                        _messages.Add($"Collection '{collection.Text}' created.", MessageKind.Success);
                        await RefreshCatalogAsync();
                    }, _messages.Error),
                    Ui.Button("Drop", async () =>
                    {
                        string name = Required(collection, "collection");
                        if (await ConfirmAsync($"Drop collection '{name}'?"))
                        {
                            await Docs().DropCollectionAsync(name);
                            _messages.Add($"Collection '{name}' dropped.", MessageKind.Success);
                            await RefreshCatalogAsync();
                        }
                    }, _messages.Error),
                    Ui.Button("Seed sample data", async () =>
                    {
                        int count = await Docs().SeedSampleDataAsync();
                        _messages.Add($"Seeded {count} documents into 'items'.", MessageKind.Success);
                        await RefreshCatalogAsync();
                    }, _messages.Error)),
                Ui.Bar(id, version),
                json,
                Ui.Bar(
                    Ui.Button("Put", async () =>
                    {
                        Document saved = await Docs().PutAsync(Required(collection, "collection"), Required(id, "document id"), json.Text ?? string.Empty, Version());
                        version.Text = saved.Version.Value.ToString(CultureInfo.InvariantCulture);
                        _messages.Add($"Put {collection.Text}/{saved.Id}: version {saved.Version}", MessageKind.Success);
                    }, _messages.Error),
                    Ui.Button("Get", async () =>
                    {
                        Document? found = await Docs().GetAsync(Required(collection, "collection"), Required(id, "document id"));
                        if (found is { } document)
                        {
                            json.Text = JsonText.Pretty(document.Content.Span);
                            version.Text = document.Version.Value.ToString(CultureInfo.InvariantCulture);
                            _messages.Add($"Got {collection.Text}/{document.Id}: version {document.Version}, {document.Content.Length} bytes", MessageKind.Success);
                        }
                        else
                        {
                            _messages.Add($"{collection.Text}/{id.Text} not found.", MessageKind.Warning);
                        }
                    }, _messages.Error),
                    Ui.Button("Delete", async () =>
                    {
                        bool deleted = await Docs().DeleteAsync(Required(collection, "collection"), Required(id, "document id"), Version());
                        _messages.Add(deleted ? $"Deleted {collection.Text}/{id.Text}." : $"{collection.Text}/{id.Text} did not exist.", deleted ? MessageKind.Success : MessageKind.Warning);
                    }, _messages.Error)),
            },
        });
    }
}
