using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Svg;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;
using FullPlugins = Markdown.Avalonia.Full.MdAvPlugins;
using MarkdownEngine = Markdown.Avalonia.Markdown;

namespace Afl.UI.Diagrams;

/// <summary>
///     Adds diagrams to <see cref="Views.MarkdownView" />: a fenced code block whose language has an
///     <see cref="AbstractDiagramRenderer" /> (<c>```mermaid</c>) is drawn as a picture. One that can't be drawn shows as
///     the code block it is, with a note saying why.
/// </summary>
/// <remarks>
///     Markdown.Avalonia reads every <c>```</c> block with its own parser before any a plugin adds, and lets one
///     override replace it. This plugin is that override: it draws the diagram languages and hands every other block to
///     an engine with the stock plugins, so code is highlighted as before.
/// </remarks>
public class DiagramMarkdownPlugin : IMdAvPlugin
{
    /// <inheritdoc />
    public void Setup(SetupInfo info)
    {
        info.Register(new DiagramCodeBlockOverride());
    }

    /// <summary>
    ///     Reads fenced code blocks in place of Markdown.Avalonia's own parser, drawing the diagram languages.
    /// </summary>
    private sealed class DiagramCodeBlockOverride() : BlockOverride2(CodeBlockParserName)
    {
        /// <summary>
        ///     The name of Markdown.Avalonia's fenced code block parser, which this replaces.
        /// </summary>
        private const string CodeBlockParserName = "CodeBlocksWithLangEvaluator";

        /// <summary>
        ///     The widest a diagram is drawn; a wider one is scaled down.
        /// </summary>
        private const double WidestDiagram = 900;

        /// <summary>
        ///     The engine that reads the blocks that aren't diagrams, made the first time one is read.
        /// </summary>
        private MarkdownEngine? _stock;

        /// <inheritdoc />
        public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status,
            IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd)
        {
            // The opening fence is group 1 and the language group 2; the block ends at the same fence on its own line,
            // as Markdown.Avalonia reads it. An unclosed block (still streaming in) is left as text until it closes.
            var fence = firstMatch.Groups[1].Value;
            var close = new Regex($@"\n[ ]*{Regex.Escape(fence)}[ ]*\n").Match(text, firstMatch.Index + firstMatch.Length);
            if (!close.Success)
            {
                parseTextBegin = parseTextEnd = -1;
                return null;
            }

            parseTextBegin = firstMatch.Index;
            parseTextEnd = close.Index + close.Length;
            var block = text[parseTextBegin..parseTextEnd];
            var codeStart = firstMatch.Index + firstMatch.Length;
            var code = text[codeStart..close.Index] + "\n";
            var language = firstMatch.Groups[2].Value.Trim();

            var renderer = DiagramRendererRegistry.Instance.For(language);
            if (renderer is null)
            {
                return this.Stock(block, status);
            }

            var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            var rendering = renderer.Render(code, dark);
            if (rendering.Svg is { } svg && Picture(svg) is { } picture)
            {
                return [new UnBlockElement(picture)];
            }

            // Shown as the code block it is, so nothing is lost, with why it isn't a diagram.
            var note = new TextBlock
            {
                Text = $"This {language} diagram couldn't be drawn: {rendering.Error ?? "its picture couldn't be read."}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 8)
            };
            return [.. this.Stock(block, status), new UnBlockElement(note)];
        }

        /// <summary>
        ///     Reads a code block the way Markdown.Avalonia does without this plugin.
        /// </summary>
        /// <param name="block">The block, fences included.</param>
        /// <param name="status">The parse status.</param>
        /// <returns>The block's elements.</returns>
        private IEnumerable<DocumentElement> Stock(string block, ParseStatus status)
        {
            this._stock ??= new MarkdownEngine { Plugins = new FullPlugins() };
            return this._stock.ParseGamutElement(block, status);
        }

        /// <summary>
        ///     Turns SVG into a control showing it.
        /// </summary>
        /// <param name="svg">The SVG.</param>
        /// <returns>The control, or null when the SVG can't be read.</returns>
        private static Control? Picture(string svg)
        {
            try
            {
                if (SvgSource.LoadFromSvg(svg) is not { } source)
                {
                    return null;
                }

                return new Image
                {
                    Source = new SvgImage { Source = source },
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxWidth = WidestDiagram,
                    Margin = new Thickness(0, 4, 0, 8)
                };
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
