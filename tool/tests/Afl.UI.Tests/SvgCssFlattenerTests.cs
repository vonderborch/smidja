using Afl.UI.Diagrams;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for <see cref="SvgCssFlattener" />, which turns a diagram's CSS into plain values.
/// </summary>
public class SvgCssFlattenerTests
{
    [Fact]
    public void Variables_AreResolvedFromTheRootStyleAndRule_WithFallbacks()
    {
        const string svg = """<svg style="--fg:#27272A;--bg:#FFFFFF"><style>svg { --_text: var(--fg); }</style><text fill="var(--_text)" stroke="var(--missing, #112233)"/></svg>""";

        var flat = SvgCssFlattener.Flatten(svg);

        Assert.Contains("fill=\"#27272A\"", flat);
        Assert.Contains("stroke=\"#112233\"", flat);
    }

    [Fact]
    public void ColorMix_MixesInSrgb()
    {
        const string svg = """<svg style="--bg:#FFFFFF;--fg:#000000"><rect fill="color-mix(in srgb, var(--fg) 50%, var(--bg))"/></svg>""";

        Assert.Contains("fill=\"#808080\"", SvgCssFlattener.Flatten(svg));
    }

    [Fact]
    public void ColorMix_WithTransparent_Fades()
    {
        const string svg = """<svg><rect fill="color-mix(in srgb, #FF0000 25%, transparent)"/></svg>""";

        Assert.Contains("fill=\"rgba(255,0,0,0.25)\"", SvgCssFlattener.Flatten(svg));
    }

    [Fact]
    public void RemSizes_BecomePixels()
    {
        const string svg = """<svg><style>svg { --fs-s: 0.875rem; }</style><text font-size="var(--fs-s)"/></svg>""";

        Assert.Contains("font-size=\"14\"", SvgCssFlattener.Flatten(svg));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MermaidDiagrams_HaveNoCssFunctionsLeft(bool dark)
    {
        var rendering = DiagramRendererRegistry.Instance.For("mermaid")!.Render("flowchart LR\n  A[Ticket] --> B{Clear?}\n  B -->|yes| C[Plan]\n", dark);

        Assert.Null(rendering.Error);
        Assert.DoesNotContain("var(", rendering.Svg);
        Assert.DoesNotContain("color-mix(", rendering.Svg);
    }
}
