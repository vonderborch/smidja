using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Niddy.Avalonia.Dialogs;

namespace Afl.UI.Views;

/// <summary>
///     Shows markdown, such as an AI's output, a prompt, an issue's body or a skill, formatted, with a toggle to its raw
///     text and Expand to read it in a larger dialog. When <see cref="IsEditable" /> it edits the text instead, with a
///     toggle to preview it formatted.
/// </summary>
public partial class MarkdownView : UserControl
{
    /// <summary>
    ///     The widest the expanded dialog gets, so lines stay readable on a wide screen.
    /// </summary>
    private const double WidestDialog = 1100;

    /// <summary>
    ///     Defines the <see cref="Markdown" /> property.
    /// </summary>
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    ///     Defines the <see cref="IsEditable" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsEditable));

    /// <summary>
    ///     Defines the <see cref="ShowsExpand" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsExpandProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(ShowsExpand), true);

    /// <summary>
    ///     Defines the <see cref="ShowsCopy" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsCopyProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(ShowsCopy));

    /// <summary>
    ///     Defines the <see cref="Title" /> property.
    /// </summary>
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<MarkdownView, string>(nameof(Title), "Preview");

    /// <summary>
    ///     Defines the <see cref="MaxPreviewHeight" /> property.
    /// </summary>
    public static readonly StyledProperty<double> MaxPreviewHeightProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(MaxPreviewHeight), 320);

    /// <summary>
    ///     Defines the <see cref="Placeholder" /> property.
    /// </summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Placeholder));

    /// <summary>
    ///     Whether the editor is being filled from <see cref="Markdown" />, so its change isn't written back.
    /// </summary>
    private bool _syncingEditor;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MarkdownView" /> class.
    /// </summary>
    public MarkdownView()
    {
        this.InitializeComponent();
        this.Editor.PropertyChanged += this.OnEditorChanged;
        this.Refresh();
    }

    /// <summary>
    ///     Gets or sets the markdown shown, or edited.
    /// </summary>
    public string? Markdown
    {
        get => this.GetValue(MarkdownProperty);
        set => this.SetValue(MarkdownProperty, value);
    }

    /// <summary>
    ///     Gets or sets whether the markdown is edited (Edit/Preview) rather than read (Formatted/Raw).
    /// </summary>
    public bool IsEditable
    {
        get => this.GetValue(IsEditableProperty);
        set => this.SetValue(IsEditableProperty, value);
    }

    /// <summary>
    ///     Gets or sets whether Expand is offered, e.g. not when the view is already in the expanded dialog.
    /// </summary>
    public bool ShowsExpand
    {
        get => this.GetValue(ShowsExpandProperty);
        set => this.SetValue(ShowsExpandProperty, value);
    }

    /// <summary>
    ///     Gets or sets whether Copy is offered, which copies the markdown as written.
    /// </summary>
    public bool ShowsCopy
    {
        get => this.GetValue(ShowsCopyProperty);
        set => this.SetValue(ShowsCopyProperty, value);
    }

    /// <summary>
    ///     Gets or sets the Expand dialog's title.
    /// </summary>
    public string Title
    {
        get => this.GetValue(TitleProperty);
        set => this.SetValue(TitleProperty, value);
    }

    /// <summary>
    ///     Gets or sets how tall the formatted or raw text grows before it scrolls.
    /// </summary>
    public double MaxPreviewHeight
    {
        get => this.GetValue(MaxPreviewHeightProperty);
        set => this.SetValue(MaxPreviewHeightProperty, value);
    }

    /// <summary>
    ///     Gets or sets the editor's placeholder text.
    /// </summary>
    public string? Placeholder
    {
        get => this.GetValue(PlaceholderProperty);
        set => this.SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    ///     Gets whether the toggle is on: the raw text when reading, the preview when editing.
    /// </summary>
    private bool IsToggled => this.ModeToggle.IsChecked == true;

    /// <summary>
    ///     Shows markdown in a dialog as big as the window <paramref name="parent" /> is in (a resizable window on desktop),
    ///     formatted, with Raw and Copy, so a long AI result or document gets most of the screen.
    /// </summary>
    /// <param name="parent">A control in the window the dialog opens over.</param>
    /// <param name="title">The title.</param>
    /// <param name="markdown">The markdown to show.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public static async Task OpenDialog(Control parent, string title, string markdown)
    {
        var view = new MarkdownView { Markdown = markdown, ShowsExpand = false, ShowsCopy = true, MaxPreviewHeight = double.PositiveInfinity };
        await Dialog.View.Open(parent, view, title, okButtonText: "Close", cancelButtonText: null, maxWidth: WidestDialog, fill: true);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == IsEditableProperty
            || change.Property == ShowsExpandProperty || change.Property == ShowsCopyProperty
            || change.Property == MaxPreviewHeightProperty || change.Property == PlaceholderProperty)
        {
            this.Refresh();
        }
    }

    /// <summary>
    ///     Shows the part the mode asks for, filled with the current markdown.
    /// </summary>
    private void Refresh()
    {
        var text = this.Markdown ?? string.Empty;
        var showsFormatted = this.IsEditable == this.IsToggled;
        var showsEditor = this.IsEditable && !this.IsToggled;
        var showsRaw = !this.IsEditable && this.IsToggled;

        this.ModeToggle.Content = this.IsEditable ? "Preview" : "Raw";
        ToolTip.SetTip(this.ModeToggle, this.IsEditable ? "Show it formatted" : "Show the text as written");
        this.Bar.IsVisible = this.IsEditable || text.Length > 0;
        this.ExpandButton.IsVisible = this.ShowsExpand && text.Length > 0;
        this.CopyButton.IsVisible = this.ShowsCopy && text.Length > 0;

        this.Formatted.IsVisible = showsFormatted;
        this.Formatted.MaxHeight = this.IsEditable ? double.PositiveInfinity : this.MaxPreviewHeight;
        if (showsFormatted && this.Formatted.Markdown != text)
        {
            this.Formatted.Markdown = text;
        }

        this.RawScroller.IsVisible = showsRaw;
        this.RawScroller.MaxHeight = this.MaxPreviewHeight;
        this.Raw.Text = text;

        this.Editor.IsVisible = showsEditor;
        this.Editor.PlaceholderText = this.Placeholder;
        if (this.Editor.Text != text)
        {
            this._syncingEditor = true;
            this.Editor.Text = text;
            this._syncingEditor = false;
        }
    }

    /// <summary>
    ///     Switches between formatted and raw, or editing and preview.
    /// </summary>
    /// <param name="sender">The toggle.</param>
    /// <param name="e">The change.</param>
    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        this.Refresh();
    }

    /// <summary>
    ///     Writes what was typed back to <see cref="Markdown" />.
    /// </summary>
    /// <remarks>
    ///     Watches the editor's text itself, since <see cref="TextBox.TextChanged" /> is raised later, via the dispatcher.
    /// </remarks>
    /// <param name="sender">The editor.</param>
    /// <param name="e">The change.</param>
    private void OnEditorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && !this._syncingEditor)
        {
            this.SetCurrentValue(MarkdownProperty, this.Editor.Text);
        }
    }

    /// <summary>
    ///     Opens the markdown, formatted, in a larger dialog.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The click.</param>
    private async void OnExpand(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(this.Markdown))
        {
            await OpenDialog(this, this.Title, this.Markdown);
        }
    }

    /// <summary>
    ///     Copies the markdown, as written, to the clipboard.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The click.</param>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(this.Markdown ?? string.Empty);
            this.CopyButton.Content = "Copied";
        }
    }
}
