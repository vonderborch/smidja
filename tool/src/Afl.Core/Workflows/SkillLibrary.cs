using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;

namespace Afl.Core.Workflows;

/// <summary>
///     The common skills available to a repo, which steps give their agents (<see cref="AbstractAgentStepDefinition.Skills" />).
///     Lookup is layered like workflows: the repo's own <c>.afl/skills</c>, then the global library in
///     <c>~/.afl/skills</c>, then the built-ins embedded in afl.
/// </summary>
/// <param name="paths">The global store paths.</param>
/// <param name="repoPath">The repo root whose own skills should be included, if any.</param>
public class SkillLibrary(AflPaths paths, string? repoPath = null)
    : AbstractLayeredLibrary<SkillDefinition, SkillListing>(paths.SkillsDirectory,
        repoPath is null ? null : AflPaths.RepoSkillsDirectory(repoPath))
{
    /// <summary>
    ///     The largest supporting file a skill imports, in bytes; bigger files are left out.
    /// </summary>
    public const int MaxImportedFileBytes = 256 * 1024;

    /// <inheritdoc />
    protected override string ResourcePrefix => PathConstants.BuiltInSkillResourcePrefix;

    /// <inheritdoc />
    protected override string Noun => "skill";

    /// <inheritdoc />
    protected override SkillListing CreateListing(SkillDefinition document, WorkflowScope scope, string? path)
    {
        return new SkillListing(document, scope, path);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> ValidateForSave(SkillDefinition document)
    {
        return Validate(document);
    }

    /// <summary>
    ///     Returns the skills with the given ids, in order and without repeats.
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The skills.</returns>
    /// <exception cref="AflException">One isn't in the library.</exception>
    public IReadOnlyList<SkillDefinition> Resolve(IEnumerable<string> ids)
    {
        return ids.Distinct(StringComparer.Ordinal).Select(this.GetRequired).ToList();
    }

    /// <summary>
    ///     Checks a skill for problems: its id, name, description and instructions, and that its files stay inside its
    ///     folder.
    /// </summary>
    /// <param name="document">The skill.</param>
    /// <returns>The problems; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(SkillDefinition document)
    {
        var errors = new List<string>();
        if (!WorkflowLibrary.IsValidId(document.Id))
        {
            errors.Add("Skill id must be lowercase letters, digits and dashes.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            errors.Add("Skill name is required.");
        }

        errors.AddRange(document.ValidateTags().Select(p => $"Skill {p}"));

        if (string.IsNullOrWhiteSpace(document.Description))
        {
            errors.Add("Skill description is required: agents read it to know when the skill applies.");
        }

        if (string.IsNullOrWhiteSpace(document.Content))
        {
            errors.Add("Skill content (its instructions) is required.");
        }

        errors.AddRange(ValidateRecommend(document.Recommend));

        foreach (var path in (document.Files ?? []).Keys)
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.StartsWith('~') ||
                normalized.Split('/').Any(p => p is "" or "." or ".."))
            {
                errors.Add($"Skill file '{path}' must be a relative path inside the skill's folder.");
            }
            else if (normalized.Equals(PathConstants.SkillFileName, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Skill file '{path}' clashes with the skill's own {PathConstants.SkillFileName}.");
            }
        }

        return errors;
    }

    /// <summary>
    ///     Reads a skill from a <c>SKILL.md</c>, or from a folder holding one, taking the folder's other text files as
    ///     its supporting files.
    /// </summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>The skill, not yet saved.</returns>
    /// <exception cref="AflException">There's no <c>SKILL.md</c> there.</exception>
    public static SkillDefinition Import(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = System.IO.Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        var file = System.IO.Directory.Exists(full) ? Path.Combine(full, PathConstants.SkillFileName) : full;
        if (!File.Exists(file))
        {
            throw new AflException(AflErrorKind.NotFound, $"There's no {PathConstants.SkillFileName} at {path}.");
        }

        var fallbackId = WorkflowLibrary.IsValidId(Path.GetFileName(directory)) ? Path.GetFileName(directory) : "skill";
        var skill = SkillDefinition.FromSkillMarkdown(File.ReadAllText(file), fallbackId);
        if (!System.IO.Directory.Exists(full))
        {
            return skill;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var other in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            var relative = Path.GetRelativePath(directory, other).Replace('\\', '/');
            if (relative.Equals(PathConstants.SkillFileName, StringComparison.OrdinalIgnoreCase) ||
                relative.Split('/').Any(p => p.StartsWith('.')) || new FileInfo(other).Length > MaxImportedFileBytes)
            {
                continue;
            }

            var text = File.ReadAllText(other);
            if (!text.Contains('\0'))
            {
                files[relative] = text;
            }
        }

        skill.Files = files.Count == 0 ? null : files;
        return skill;
    }

    /// <summary>
    ///     Checks a skill's <c>recommend</c> block: every file rule has a pattern, and languages and frameworks are ids.
    /// </summary>
    /// <param name="rule">The block, if any.</param>
    /// <returns>The problems; empty when valid.</returns>
    private static IEnumerable<string> ValidateRecommend(SkillRecommendRule? rule)
    {
        if (rule is null)
        {
            yield break;
        }

        foreach (var file in rule.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Glob))
            {
                yield return "Skill recommend: every file rule needs a glob, e.g. *.csproj.";
            }
            else if (file.Glob.Contains("..", StringComparison.Ordinal) || file.Glob.StartsWith('/'))
            {
                yield return $"Skill recommend: glob '{file.Glob}' must be relative to the repo root.";
            }
        }

        foreach (var id in (rule.Languages ?? []).Concat(rule.Frameworks ?? []))
        {
            if (!WorkflowLibrary.IsValidId(id))
            {
                yield return $"Skill recommend: '{id}' must be lowercase letters, digits and dashes.";
            }
        }

        if ((rule.Files ?? []).Count == 0 && (rule.Languages ?? []).Count == 0 && (rule.Frameworks ?? []).Count == 0)
        {
            yield return "Skill recommend: give files, languages or frameworks, or leave recommend out.";
        }
    }
}
