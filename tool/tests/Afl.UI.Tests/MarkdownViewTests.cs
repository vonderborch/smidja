using System.Globalization;
using Afl.UI.DataContexts;
using Afl.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
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
