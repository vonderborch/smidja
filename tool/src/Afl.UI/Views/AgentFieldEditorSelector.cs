using Afl.Core.Agents;
using Afl.UI.DataContexts;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Afl.UI.Views;

/// <summary>
///     Picks the control an agent field is edited with from its <see cref="AbstractAgentField.Editor" />, so a new agent
///     kind's fields need no XAML: the templates are keyed by editor name in <c>Views/AgentFieldEditors.axaml</c>, and an
///     editor without one gets the <see cref="Fallback" /> (a text box).
/// </summary>
public class AgentFieldEditorSelector : IDataTemplate
{
    /// <summary>
    ///     Gets the templates, by editor name.
    /// </summary>
    [Content]
    public Dictionary<string, IDataTemplate> Templates { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets or sets the editor whose template is used when a field's editor has none.
    /// </summary>
    public string Fallback { get; set; } = AgentFieldEditors.Text;

    /// <inheritdoc />
    public Control? Build(object? param)
    {
        var editor = (param as AgentFieldDataContext)?.Editor;
        var template = editor is not null && this.Templates.TryGetValue(editor, out var found) ? found : this.Templates[this.Fallback];
        return template.Build(param);
    }

    /// <inheritdoc />
    public bool Match(object? data)
    {
        return data is AgentFieldDataContext;
    }
}
