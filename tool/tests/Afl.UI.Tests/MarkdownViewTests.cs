using System.Globalization;
using Afl.UI.DataContexts;
using Afl.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Svg;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdown.Avalonia.Full;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for <see cref="MarkdownView" />, which shows markdown formatted, raw or in an editor.
/// </summary>
public class MarkdownViewTests
{
    /// <summary>
    ///     A data context with a markdown text to bind to.
    /// </summary>
    private sealed class Holder
    {
        /// <summary>
        ///     Gets or sets the markdown.
        /// </summary>
        public string? Text { get; set; }
    }

    /// <summary>
    ///     Finds a named part of the view.
    /// </summary>
    /// <typeparam name="T">The part's type.</typeparam>
    /// <param name="view">The view.</param>
    /// <param name="name">The part's name.</param>
    /// <returns>The part.</returns>
    private static T Part<T>(MarkdownView view, string name)
        where T : Control
    {
        return view.FindControl<T>(name) ?? throw new InvalidOperationException($"No part named {name}.");
    }

    [AvaloniaFact]
    public void ReadOnly_ShowsFormattedAndTogglesToRaw()
    {
        var view = new MarkdownView { Markdown = "# Plan\n\n1. Add a toggle" };
        var formatted = Part<MarkdownScrollViewer>(view, "Formatted");
        var toggle = Part<ToggleButton>(view, "ModeToggle");

        Assert.True(formatted.IsVisible);
        Assert.Equal("# Plan\n\n1. Add a toggle", formatted.Markdown);
        Assert.Equal("Raw", toggle.Content);
        Assert.False(Part<TextBox>(view, "Editor").IsVisible);

        toggle.IsChecked = true;
        Assert.False(formatted.IsVisible);
        Assert.True(Part<ScrollViewer>(view, "RawScroller").IsVisible);
        Assert.Equal("# Plan\n\n1. Add a toggle", Part<SelectableTextBlock>(view, "Raw").Text);
    }

    [AvaloniaFact]
    public void ReadOnly_HidesTheBarWhenThereIsNothingToShow()
    {
        var view = new MarkdownView();
        Assert.False(Part<StackPanel>(view, "Bar").IsVisible);

        view.Markdown = "Done.";
        Assert.True(Part<StackPanel>(view, "Bar").IsVisible);
        Assert.True(Part<Button>(view, "ExpandButton").IsVisible);
    }

    [AvaloniaFact]
    public void Editable_WritesWhatIsTypedBackToTheBinding()
    {
        var holder = new Holder { Text = "Check: {{steps.fix.output}}" };
        var view = new MarkdownView { IsEditable = true, DataContext = holder };
        view.Bind(MarkdownView.MarkdownProperty, new Binding(nameof(Holder.Text)));
        var editor = Part<TextBox>(view, "Editor");
        var toggle = Part<ToggleButton>(view, "ModeToggle");

        Assert.True(editor.IsVisible);
        Assert.Equal("Preview", toggle.Content);
        Assert.Equal("Check: {{steps.fix.output}}", editor.Text);

        editor.Text = "# Check\n\nRun the tests.";
        Assert.Equal("# Check\n\nRun the tests.", view.Markdown);
        Assert.Equal("# Check\n\nRun the tests.", holder.Text);

        toggle.IsChecked = true;
        Assert.False(editor.IsVisible);
        Assert.Equal("# Check\n\nRun the tests.", Part<MarkdownScrollViewer>(view, "Formatted").Markdown);
    }

    /// <summary>
    ///     A small flowchart, as the shape and architecture workflows ask for.
    /// </summary>
    private const string Flowchart = "## Shape\n\n```mermaid\nflowchart LR\n    A[Ticket] --> B{Clear?}\n    B -->|yes| C[Plan]\n    B -->|no| D[Ask]\n    D --> B\n```\n\nThen build it.";

    /// <summary>
    ///     Shows a view in a window and lets it lay out and draw.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>The window.</returns>
    private static Window Show(MarkdownView view)
    {
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    [AvaloniaFact]
    public void MermaidBlock_IsDrawnAsADiagram()
    {
        var view = new MarkdownView { Markdown = Flowchart, MaxPreviewHeight = double.PositiveInfinity };
        var window = Show(view);

        var diagram = Assert.Single(view.GetVisualDescendants().OfType<Image>(), i => i.Source is SvgImage);
        Assert.True(diagram.Bounds.Width > 100 && diagram.Bounds.Height > 50);

        var folder = Environment.GetEnvironmentVariable("AFL_UI_SCREENSHOTS");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            window.CaptureRenderedFrame()?.Save(Path.Combine(folder, "markdown-mermaid.png"), new PngBitmapEncoderOptions());
        }
    }

    [AvaloniaFact]
    public void MermaidBlock_ThatCantBeDrawn_ShowsTheCodeAndWhy()
    {
        var view = new MarkdownView { Markdown = "```mermaid\nthis is not a diagram\n```", MaxPreviewHeight = double.PositiveInfinity };
        Show(view);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Image>(), i => i.Source is SvgImage);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("This mermaid diagram couldn't be drawn") == true);
    }

    [Theory]
    [InlineData("docs/PLAN.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("out/report.json", false)]
    [InlineData(null, false)]
    public void IsMarkdownPath_MatchesMarkdownFiles(string? path, bool expected)
    {
        Assert.Equal(expected, UiConverters.IsMarkdownPath.Convert(path, typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
