using System.Collections.Concurrent;
using Afl.Core.Registration;

namespace Afl.UI.Diagrams;

/// <summary>
///     Registers an <see cref="AbstractDiagramRenderer" /> with the <see cref="DiagramRendererRegistry" />.
/// </summary>
/// <param name="language">The fenced code block language it draws, e.g. <c>mermaid</c>.</param>
public class DiagramRendererRegistrationAttribute(string language) : AbstractRegistrationAttribute(language);

/// <summary>
///     The diagram languages markdown can draw, by fenced code block language.
/// </summary>
public class DiagramRendererRegistry() : TypeRegistry<AbstractDiagramRenderer, DiagramRendererRegistrationAttribute>("diagram language")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static DiagramRendererRegistry Instance { get; } = new();

    /// <summary>
    ///     The renderers made so far, by language, so each keeps its cache.
    /// </summary>
    private readonly ConcurrentDictionary<string, AbstractDiagramRenderer> _renderers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the shared renderer for a language.
    /// </summary>
    /// <param name="language">The fenced code block language.</param>
    /// <returns>The renderer, or null when no renderer draws that language.</returns>
    public AbstractDiagramRenderer? For(string language)
    {
        return this.Contains(language) ? this._renderers.GetOrAdd(language, l => this.Create(l)) : null;
    }
}

/// <summary>
///     A diagram drawn from a fenced code block: its SVG, or why it couldn't be drawn.
/// </summary>
/// <param name="Svg">The diagram as SVG, or null when it couldn't be drawn.</param>
/// <param name="Error">Why it couldn't be drawn, or null.</param>
public record DiagramRendering(string? Svg, string? Error);

/// <summary>
///     Draws one diagram language's fenced code blocks (<c>```mermaid</c>) as SVG, for <see cref="MarkdownView" />.
///     A new language is one subclass with a <see cref="DiagramRendererRegistrationAttribute" />. Drawings are cached by
///     source and theme, as markdown that streams in is laid out again on every update.
/// </summary>
public abstract class AbstractDiagramRenderer
{
    /// <summary>
    ///     The most drawings kept; the cache starts over past it.
    /// </summary>
    private const int CacheLimit = 64;

    /// <summary>
    ///     The drawings so far, by theme and source.
    /// </summary>
    private readonly ConcurrentDictionary<(bool Dark, string Source), DiagramRendering> _cache = new();

    /// <summary>
    ///     Draws a diagram, or says why it can't be drawn.
    /// </summary>
    /// <param name="source">The diagram's source: the fenced block's lines.</param>
    /// <param name="dark">Whether it's drawn on a dark background.</param>
    /// <returns>The drawing.</returns>
    public DiagramRendering Render(string source, bool dark)
    {
        if (this._cache.TryGetValue((dark, source), out var cached))
        {
            return cached;
        }

        DiagramRendering rendering;
        try
        {
            rendering = new DiagramRendering(SvgCssFlattener.Flatten(this.RenderSvg(source, dark)), null);
        }
        catch (Exception ex)
        {
            rendering = new DiagramRendering(null, ex.Message);
        }

        if (this._cache.Count >= CacheLimit)
        {
            this._cache.Clear();
        }

        this._cache[(dark, source)] = rendering;
        return rendering;
    }

    /// <summary>
    ///     Draws a diagram as SVG.
    /// </summary>
    /// <param name="source">The diagram's source.</param>
    /// <param name="dark">Whether it's drawn on a dark background.</param>
    /// <returns>The SVG.</returns>
    /// <exception cref="Exception">The source isn't a diagram this renderer can draw.</exception>
    protected abstract string RenderSvg(string source, bool dark);
}
