using Mermaider;
using Mermaider.Models;

namespace Afl.UI.Diagrams;

/// <summary>
///     Draws <c>```mermaid</c> blocks with Mermaider, in .NET (no browser or JavaScript). Its SVG is always sanitized and
///     its parsing and layout are bounded, so an agent's diagram can't run anything or hang the window.
/// </summary>
[DiagramRendererRegistration("mermaid")]
public class MermaidDiagramRenderer : AbstractDiagramRenderer
{
    /// <summary>
    ///     How diagrams are drawn on a light background.
    /// </summary>
    private static readonly RenderOptions Light = new() { Bg = "#FFFFFF", Fg = "#27272A" };

    /// <summary>
    ///     How diagrams are drawn on a dark background.
    /// </summary>
    private static readonly RenderOptions Dark = new() { Bg = "#1F1F23", Fg = "#E4E4E7" };

    /// <inheritdoc />
    protected override string RenderSvg(string source, bool dark)
    {
        return MermaidRenderer.RenderSvg(source, dark ? Dark : Light);
    }
}
