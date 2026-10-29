using Afl.UI.DataContexts;
using Niddy.Avalonia.PageSystem;

namespace Afl.UI.Pages;

/// <summary>
///     A library page editing one kind of document, made by <typeparamref name="TEditor" />. A new kind of document is a
///     subclass of this with its page registration; the layout and behaviour are <see cref="AbstractLibraryPage" />'s.
/// </summary>
/// <typeparam name="TEditor">The editor of the page's documents.</typeparam>
public abstract class AbstractLibraryPage<TEditor> : AbstractLibraryPage
    where TEditor : AbstractLibraryEditor, new()
{
    /// <inheritdoc />
    protected override AbstractLibraryEditor CreateEditor()
    {
        return new TEditor();
    }
}

/// <summary>
///     The Workflows page: every workflow in every scope, and the workflow builder.
/// </summary>
[PageRegistration(DisplayName = "Workflows", SortKey = "3", Shortcut = "Primary+3")]
[PageIcon(PageIcons.Workflows)]
public class WorkflowsPage : AbstractLibraryPage<WorkflowLibraryEditor>;

/// <summary>
///     The Common steps page: the reusable steps workflows <c>use</c>, and their editor.
/// </summary>
[PageRegistration(DisplayName = "Common steps", SortKey = "4", Shortcut = "Primary+4")]
[PageIcon(PageIcons.CommonSteps)]
public class CommonStepsPage : AbstractLibraryPage<CommonStepLibraryEditor>;

/// <summary>
///     The Skills page: the skills AI steps are given, and their editor.
/// </summary>
[PageRegistration(DisplayName = "Skills", SortKey = "5", Shortcut = "Primary+5")]
[PageIcon(PageIcons.Skills)]
public class SkillsPage : AbstractLibraryPage<SkillLibraryEditor>;
