using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     An editable copy of one supporting file of a skill: its path in the skill's folder and its text. Every edit
///     raises <see cref="Edited" />.
/// </summary>
public class SkillFileDraftDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="SkillFileDraftDataContext" /> class.
    /// </summary>
    /// <param name="path">The file's path, relative to the skill's folder.</param>
    /// <param name="text">The file's text.</param>
    public SkillFileDraftDataContext(string path, string text)
    {
        this.Path = path;
        this.Text = text;
        this.PropertyChanged += (_, _) => this.Edited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Raised when any field changes.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>
    ///     Gets or sets the file's path, relative to the skill's folder, e.g. <c>reference.md</c>.
    /// </summary>
    public string Path
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Gets or sets the file's text.
    /// </summary>
    public string Text
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    ///     Returns a file path not used yet.
    /// </summary>
    /// <param name="taken">The paths already used.</param>
    /// <returns>The path.</returns>
    public static string NewPath(IEnumerable<string> taken)
    {
        var paths = taken.ToHashSet(StringComparer.Ordinal);
        var number = 1;
        while (paths.Contains($"file-{number}.md"))
        {
            number++;
        }

        return $"file-{number}.md";
    }
}
