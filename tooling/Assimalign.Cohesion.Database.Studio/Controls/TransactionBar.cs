using System;
using System.Threading.Tasks;

using Microsoft.Maui.Controls;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Begin / Commit / Rollback with an indicator. Embedded sessions use the session API with the chosen
/// isolation; SQL over the wire sends BEGIN/COMMIT/ROLLBACK statements; other wire models have none.
/// </summary>
internal sealed class TransactionBar : ContentView
{
    private static readonly IsolationLevel[] _levels = [IsolationLevel.ReadCommitted, IsolationLevel.Snapshot, IsolationLevel.Serializable];

    private readonly Picker _isolation = Ui.Picker(["ReadCommitted", "Snapshot", "Serializable"], 130, selected: 1);
    private readonly Label _indicator = Ui.Text("no transaction", 12, bold: true, color: Ui.Muted);
    private readonly View _controls;
    private readonly Label _unsupported = Ui.Text("transactions: embedded only", 12, color: Ui.Muted);
    private ModelWorkspace? _workspace;

    public TransactionBar(Action<string> onMessage, Action<Exception> onError)
    {
        _controls = Ui.Bar(
            Ui.Text("Tx:", bold: true),
            _isolation,
            Ui.Button("Begin", async () =>
            {
                await RequireWorkspace().BeginTransactionAsync(_levels[Math.Max(0, _isolation.SelectedIndex)]);
                Refresh();
                onMessage($"Transaction started: {RequireWorkspace().TransactionText}");
            }, onError),
            Ui.Button("Commit", async () =>
            {
                await RequireWorkspace().CommitAsync();
                Refresh();
                onMessage("Transaction committed.");
            }, onError),
            Ui.Button("Rollback", async () =>
            {
                await RequireWorkspace().RollbackAsync();
                Refresh();
                onMessage("Transaction rolled back.");
            }, onError),
            _indicator);

        Content = new Grid { Children = { _controls, _unsupported } };
        Bind(null);
    }

    public void Bind(ModelWorkspace? workspace)
    {
        _workspace = workspace;
        bool supported = workspace?.SupportsSessionTransactions ?? false;
        _controls.IsVisible = supported;
        _unsupported.IsVisible = !supported;
        _isolation.IsEnabled = workspace?.Mode == ConnectionMode.Embedded;
        Refresh();
    }

    public void Refresh()
    {
        string? text = _workspace?.TransactionText;
        _indicator.Text = text is null ? "no transaction" : $"ACTIVE: {text}";
        _indicator.TextColor = text is null ? Ui.Muted : Ui.Bad;
    }

    private ModelWorkspace RequireWorkspace() => _workspace ?? throw new InvalidOperationException("No workspace.");
}
