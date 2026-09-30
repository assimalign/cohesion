using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Assimalign.Cohesion.Database.Studio;

internal enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
    Detail,
}

/// <summary>One line in a Messages pane; lines with an offset select that span in the editor when tapped.</summary>
internal sealed record MessageItem(string Text, MessageKind Kind, int? Offset = null, int? Length = null);

/// <summary>The Messages pane: colored lines, tap-to-locate for diagnostics, copy-all.</summary>
internal sealed class MessageList : ContentView
{
    private readonly ObservableCollection<MessageItem> _items = [];
    private readonly CollectionView _view;

    public MessageList(string title = "Messages")
    {
        _view = new CollectionView
        {
            ItemsSource = _items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label
                {
                    FontFamily = Ui.Mono,
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap,
                    Padding = new Thickness(4, 1),
                };
                label.BindingContextChanged += (_, _) =>
                {
                    if (label.BindingContext is MessageItem item)
                    {
                        label.Text = item.Text;
                        label.TextColor = item.Kind switch
                        {
                            MessageKind.Success => Ui.Good,
                            MessageKind.Warning => Ui.Warn,
                            MessageKind.Error => Ui.Bad,
                            MessageKind.Detail => Ui.Muted,
                            _ => Colors.Black,
                        };
                    }
                };
                return label;
            }),
        };

        _view.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is MessageItem { Offset: not null } item)
            {
                Locate?.Invoke(this, item);
            }

            _view.SelectedItem = null;
        };

        var copy = Ui.Button("Copy", async () =>
        {
            var builder = new StringBuilder();
            foreach (MessageItem item in _items)
            {
                builder.AppendLine(item.Text);
            }

            await Clipboard.Default.SetTextAsync(builder.ToString());
        });
        var clear = Ui.Button("Clear", () =>
        {
            Clear();
            return System.Threading.Tasks.Task.CompletedTask;
        });

        Content = Ui.Rows(
            (GridLength.Auto, Ui.Bar(Ui.Heading(title), copy, clear)),
            (GridLength.Star, new Border { Content = _view, Stroke = Ui.Border, StrokeThickness = 1, BackgroundColor = Colors.White }));
    }

    /// <summary>Raised when a line carrying an editor offset is tapped.</summary>
    public event EventHandler<MessageItem>? Locate;

    public void Clear() => _items.Clear();

    public void Add(string text, MessageKind kind = MessageKind.Info, int? offset = null, int? length = null)
    {
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            _items.Add(new MessageItem(line, kind, offset, length));
        }

        if (_items.Count > 0)
        {
            Dispatcher.Dispatch(() =>
            {
                if (_items.Count > 0)
                {
                    _view.ScrollTo(_items.Count - 1, position: ScrollToPosition.End, animate: false);
                }
            });
        }
    }

    public void Error(Exception exception) => Add(ErrorText.Describe(exception), MessageKind.Error);
}
