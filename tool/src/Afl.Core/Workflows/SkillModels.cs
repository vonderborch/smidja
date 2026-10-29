using System.Text;
using System.Text.Json;
using Afl.Core.Constants;
using Afl.Core.Storage;

namespace Afl.Core.Workflows;

/// <summary>
///     A skill written once and given to the agents of any number of steps (<see cref="AbstractAgentStepDefinition.Skills" />): the
///     instructions an agent follows, in the same shape as a Claude Code <c>SKILL.md</c>, plus any supporting files. Its
///     <see cref="AbstractLibraryDocument.Description" /> is required: agents read it to decide when the skill applies.
/// </summary>
[DocumentVersion(1, 2)]
public class SkillDefinition : AbstractLibraryDocument
{
    /// <summary>
    ///     Gets or sets the instructions, in markdown (the body of a <c>SKILL.md</c>).
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets supporting text files the instructions refer to, by path relative to the skill's folder.
    /// </summary>
    public Dictionary<string, string>? Files { get; set; }

    /// <summary>
    ///     Gets or sets what kind of repo the skill suits, so runs there are offered it (<see cref="SkillRecommender" />);
    ///     null for a general skill that workflows pick themselves (1.2).
    /// </summary>
    public SkillRecommendRule? Recommend { get; set; }

    /// <summary>
    ///     Renders the skill as a <c>SKILL.md</c>: frontmatter with its name and description, then its instructions.
    /// </summary>
    /// <returns>The markdown.</returns>
    public string ToSkillMarkdown()
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(this.Id).Append('\n');
        // Quoted the JSON way, which YAML reads too, so a colon or quote in the description can't break the frontmatter.
        builder.Append("description: ").Append(JsonSerializer.Serialize((this.Description ?? string.Empty).ReplaceLineEndings(" ").Trim())).Append('\n');
        builder.Append("---\n\n");
        builder.Append(this.Content.Trim()).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    ///     Reads a <c>SKILL.md</c>: its frontmatter's <c>name</c> and <c>description</c>, and the rest as instructions.
    /// </summary>
    /// <param name="markdown">The file's text.</param>
    /// <param name="fallbackId">The id to use when the frontmatter has no name (e.g. the folder name).</param>
    /// <returns>The skill.</returns>
    public static SkillDefinition FromSkillMarkdown(string markdown, string fallbackId)
    {
        var text = markdown.ReplaceLineEndings("\n");
        var skill = new SkillDefinition { Id = fallbackId, Name = fallbackId, Content = text.Trim() };
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return skill;
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return skill;
        }

        var fields = ReadFrontmatter(text[4..end].Split('\n'));
        if (fields.TryGetValue("name", out var name))
        {
            skill.Id = WorkflowLibrary.IsValidId(name) ? name : skill.Id;
            skill.Name = name;
        }

        if (fields.TryGetValue("description", out var description))
        {
            skill.Description = description;
        }

        var bodyStart = text.IndexOf('\n', end + 1);
        skill.Content = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..].Trim();
        return skill;
    }

    /// <summary>
    ///     Reads the top-level keys of a frontmatter block. A value can be on the key's line (plain or quoted), carry on over
    ///     more-indented lines (folded with spaces), or be a <c>&gt;</c> (folded) or <c>|</c> (literal) block scalar. Nested
    ///     mappings and lists are read as their raw text, which is enough for the keys a skill uses.
    /// </summary>
    /// <param name="lines">The lines between the <c>---</c> markers.</param>
    /// <returns>The values by key.</returns>
    private static Dictionary<string, string> ReadFrontmatter(IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#'))
            {
                continue;
            }

            var first = line[(colon + 1)..].Trim();
            var more = new List<string>();
            while (i + 1 < lines.Count && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
            {
                more.Add(lines[++i]);
            }

            while (more.Count > 0 && more[^1].Trim().Length == 0)
            {
                more.RemoveAt(more.Count - 1);
            }

            fields[line[..colon].Trim()] = first.Length > 0 && (first[0] == '>' || first[0] == '|')
                ? ReadBlockScalar(first[0] == '>', more)
                : Unquote(string.Join(' ', new[] { first }.Concat(more.Select(m => m.Trim())).Where(p => p.Length > 0)));
        }

        return fields;
    }

    /// <summary>
    ///     Reads a block scalar's lines: folded (<c>&gt;</c>) joins lines with spaces and keeps blank lines as breaks;
    ///     literal (<c>|</c>) keeps the lines as written. Either way the common indent is removed and the end trimmed.
    /// </summary>
    /// <param name="folded">Whether the scalar is folded rather than literal.</param>
    /// <param name="lines">The scalar's lines, still indented.</param>
    /// <returns>The value.</returns>
    private static string ReadBlockScalar(bool folded, IReadOnlyList<string> lines)
    {
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var unindented = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
        if (!folded)
        {
            return string.Join('\n', unindented).TrimEnd();
        }

        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var l in unindented.Append(string.Empty))
        {
            if (l.Trim().Length > 0)
            {
                current.Add(l.Trim());
            }
            else if (current.Count > 0)
            {
                paragraphs.Add(string.Join(' ', current));
                current.Clear();
            }
        }

        return string.Join('\n', paragraphs);
    }

    /// <summary>
    ///     Reads a frontmatter value, which may be double-quoted (with escapes) or single-quoted.
    /// </summary>
    /// <param name="value">The value as written.</param>
    /// <returns>The value.</returns>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch (JsonException)
            {
                return value[1..^1];
            }
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1].Replace("''", "'") : value;
    }

    /// <summary>
    ///     Writes the skill as a folder an agent can load: a <c>SKILL.md</c> plus its supporting files.
    /// </summary>
    /// <param name="directory">The folder; it is created if needed.</param>
    public void WriteFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PathConstants.SkillFileName), this.ToSkillMarkdown());
        foreach (var (relative, content) in this.Files ?? [])
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}

/// <summary>
///     What kind of repo a skill suits: it is recommended when one of its <see cref="Files" /> rules matches a file in
///     the repo, or when the repo's stack has one of its <see cref="Frameworks" /> (or, for a skill naming no frameworks,
///     one of its <see cref="Languages" />).
/// </summary>
public class SkillRecommendRule
{
    /// <summary>
    ///     Gets or sets the files that show the skill fits, any one of which is enough.
    /// </summary>
    public List<SkillFileRule>? Files { get; set; }

    /// <summary>
    ///     Gets or sets the languages the skill is for, e.g. <c>csharp</c> or <c>python</c>: lowercase ids.
    /// </summary>
    public List<string>? Languages { get; set; }

    /// <summary>
    ///     Gets or sets the frameworks the skill is for, e.g. <c>fastapi</c> or <c>playwright</c>: lowercase ids.
    /// </summary>
    public List<string>? Frameworks { get; set; }

    /// <summary>
    ///     Gets or sets the reason shown with the recommendation, e.g. "C# project"; null to show only what matched.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
///     A file that shows a skill fits a repo.
/// </summary>
public class SkillFileRule
{
    /// <summary>
    ///     Gets or sets the file pattern (<c>*</c> and <c>?</c>), matched against each file's name, or against its path
    ///     from the repo root when the pattern has a <c>/</c>; e.g. <c>*.csproj</c> or <c>src/*.py</c>.
    /// </summary>
    public string Glob { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets text the file must contain (ignoring case), e.g. <c>fastapi</c> in <c>pyproject.toml</c>; null when
    ///     the file being there is enough. Only files up to <see cref="RepoScan.MaxContainsBytes" /> are read.
    /// </summary>
    public string? Contains { get; set; }
}

/// <summary>
///     A common skill together with where it was found.
/// </summary>
/// <param name="Skill">The skill.</param>
/// <param name="Scope">Where it came from.</param>
/// <param name="Path">The file it was loaded from, or null for built-ins.</param>
public record SkillListing(SkillDefinition Skill, WorkflowScope Scope, string? Path)
    : LibraryListing<SkillDefinition>(Skill, Scope, Path);
