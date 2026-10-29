using Afl.Core.Repos;

namespace Afl.Core.Workflows;

/// <summary>
///     A skill recommended for a repo.
/// </summary>
/// <param name="Skill">The skill.</param>
/// <param name="Reason">Why, as shown: the skill's reason and what matched, e.g. "C# project: found Afl.slnx, Afl.Core.csproj".</param>
/// <param name="Matched">The repo files that matched, or the stack entries when it was recommended for the stack.</param>
public record SkillRecommendation(SkillDefinition Skill, string Reason, IReadOnlyList<string> Matched);

/// <summary>
///     The languages and frameworks skills know about, offered when asking what an empty repo will use.
/// </summary>
/// <param name="Languages">The languages, sorted.</param>
/// <param name="Frameworks">The frameworks, sorted.</param>
public record StackChoices(IReadOnlyList<string> Languages, IReadOnlyList<string> Frameworks);

/// <summary>
///     What to offer the person starting a run: the workflow's own skills, the skills recommended for the repo, and
///     whether to ask what the repo uses first.
/// </summary>
/// <param name="Defaults">The workflow's skills, which a run gets unless the pick changes.</param>
/// <param name="Recommended">The skills recommended for the repo, best evidence first.</param>
/// <param name="Stack">The stack the recommendations used: the saved one plus what the files show; null when neither says anything.</param>
/// <param name="NeedsStack">Whether the repo is empty and has no saved stack, so the user should be asked for one.</param>
/// <param name="Choices">The languages and frameworks to offer when asking.</param>
public record SkillAdvice(
    IReadOnlyList<string> Defaults,
    IReadOnlyList<SkillRecommendation> Recommended,
    RepoStack? Stack,
    bool NeedsStack,
    StackChoices Choices);

/// <summary>
///     Recommends skills for a repo from the skills' <see cref="SkillDefinition.Recommend" /> rules: a skill is recommended
///     when one of its file rules matches a file in the repo (<see cref="RepoScan" />), or when the repo's stack has one
///     of its frameworks (or, for a skill naming no frameworks, one of its languages). The stack is the one saved on the
///     repo (<see cref="RepoRecord.Stack" />, asked while it was empty) plus the languages and frameworks of the skills
///     whose files matched. It's a quick, deterministic look at file names (and small files' text), never an AI call.
/// </summary>
/// <param name="skills">The skills to choose from.</param>
public class SkillRecommender(IEnumerable<SkillDefinition> skills)
{
    /// <summary>
    ///     How many matched files a reason names before it just counts the rest.
    /// </summary>
    private const int NamedMatches = 2;

    /// <summary>
    ///     Gets the skills to choose from.
    /// </summary>
    public IReadOnlyList<SkillDefinition> Skills { get; } = skills.ToList();

    /// <summary>
    ///     Works out what to offer when starting a workflow in a repo.
    /// </summary>
    /// <param name="library">The workflow library (its <see cref="WorkflowLibrary.Skills" /> are the choices).</param>
    /// <param name="repo">The repo.</param>
    /// <param name="workflow">The workflow, for its default skills; null for none.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The advice.</returns>
    public static async Task<SkillAdvice> AdviseAsync(
        WorkflowLibrary library,
        RepoRecord repo,
        WorkflowDefinition? workflow,
        CancellationToken ct = default)
    {
        var recommender = new SkillRecommender(library.Skills.List().Select(l => l.Skill));
        var scan = await RepoScan.OfAsync(repo.Path, ct).ConfigureAwait(false);
        var stack = recommender.StackFor(scan, repo.Stack);
        return new SkillAdvice(DefaultsOf(workflow), recommender.Recommend(scan, stack), stack, scan.IsEmpty && repo.Stack is null,
            recommender.StackChoices());
    }

    /// <summary>
    ///     Returns a workflow's own skills: the ones a run gets unless the person starting it picks others.
    /// </summary>
    /// <param name="workflow">The workflow, or null.</param>
    /// <returns>Its skills, trimmed, without blanks or repeats.</returns>
    public static IReadOnlyList<string> DefaultsOf(WorkflowDefinition? workflow)
    {
        return Clean(workflow?.Skills ?? []);
    }

    /// <summary>
    ///     Works out a run's skills from a starting pick: <paramref name="replace" /> (or the workflow's defaults when it's
    ///     null), plus <paramref name="add" />, less <paramref name="drop" />.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="replace">The skills to start from instead of the workflow's; empty for none.</param>
    /// <param name="add">Skills to add.</param>
    /// <param name="drop">Skills to take out.</param>
    /// <returns>The run's skills, or null when nothing was given (so the run keeps the workflow's).</returns>
    public static List<string>? Pick(
        WorkflowDefinition workflow,
        IEnumerable<string>? replace,
        IEnumerable<string> add,
        IEnumerable<string> drop)
    {
        var adding = Clean(add);
        var dropping = Clean(drop);
        if (replace is null && adding.Count == 0 && dropping.Count == 0)
        {
            return null;
        }

        return Clean([.. replace is null ? DefaultsOf(workflow) : Clean(replace), .. adding])
            .Except(dropping, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Recommends skills for a scanned repo.
    /// </summary>
    /// <param name="scan">The repo's files.</param>
    /// <param name="stack">The repo's stack (see <see cref="StackFor" />), if known.</param>
    /// <returns>The recommendations: those matched by files first, then those matched by the stack, each by name.</returns>
    public IReadOnlyList<SkillRecommendation> Recommend(RepoScan scan, RepoStack? stack)
    {
        var byFiles = new List<SkillRecommendation>();
        var byStack = new List<SkillRecommendation>();
        foreach (var skill in this.Skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (skill.Recommend is not { } rule)
            {
                continue;
            }

            var files = MatchedFiles(scan, rule);
            if (files.Count > 0)
            {
                byFiles.Add(new SkillRecommendation(skill, ReasonFor(skill, "found " + Summarize(files)), files));
                continue;
            }

            var fits = StackMatches(rule, stack);
            if (fits.Count > 0)
            {
                byStack.Add(new SkillRecommendation(skill, ReasonFor(skill, "the project uses " + string.Join(", ", fits)), fits));
            }
        }

        return [.. byFiles, .. byStack];
    }

    /// <summary>
    ///     Works out a repo's stack: the saved one plus the languages and frameworks of the skills its files match.
    /// </summary>
    /// <param name="scan">The repo's files.</param>
    /// <param name="saved">The stack saved on the repo, if any.</param>
    /// <returns>The stack, or null when it names nothing.</returns>
    public RepoStack? StackFor(RepoScan scan, RepoStack? saved)
    {
        var matched = this.Skills
            .Where(s => s.Recommend is { } rule && MatchedFiles(scan, rule).Count > 0)
            .Select(s => s.Recommend!)
            .ToList();
        var stack = RepoStack.Of(
            (saved?.Languages ?? []).Concat(matched.SelectMany(r => r.Languages ?? [])),
            (saved?.Frameworks ?? []).Concat(matched.SelectMany(r => r.Frameworks ?? [])));
        return stack.IsEmpty ? null : stack;
    }

    /// <summary>
    ///     Collects every language and framework the skills name.
    /// </summary>
    /// <returns>The choices.</returns>
    public StackChoices StackChoices()
    {
        var rules = this.Skills.Select(s => s.Recommend).OfType<SkillRecommendRule>().ToList();
        return new StackChoices(
            rules.SelectMany(r => r.Languages ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            rules.SelectMany(r => r.Frameworks ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    ///     Trims skill ids, dropping blanks and repeats.
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The cleaned ids, in order.</returns>
    private static List<string> Clean(IEnumerable<string> ids)
    {
        return ids.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Returns the files any of a rule's file rules match.
    /// </summary>
    /// <param name="scan">The repo's files.</param>
    /// <param name="rule">The rule.</param>
    /// <returns>The matching paths, in the scan's order.</returns>
    private static List<string> MatchedFiles(RepoScan scan, SkillRecommendRule rule)
    {
        return (rule.Files ?? []).SelectMany(scan.Matching).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Returns the stack entries a rule fits: its frameworks in the stack, or, when it names none, its languages.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="stack">The stack, if any.</param>
    /// <returns>The entries that fit.</returns>
    private static List<string> StackMatches(SkillRecommendRule rule, RepoStack? stack)
    {
        if (stack is null)
        {
            return [];
        }

        return (rule.Frameworks ?? []).Count > 0
            ? rule.Frameworks!.Intersect(stack.Frameworks, StringComparer.Ordinal).ToList()
            : (rule.Languages ?? []).Intersect(stack.Languages, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Names the first few matched files and counts the rest.
    /// </summary>
    /// <param name="files">The paths.</param>
    /// <returns>E.g. "Afl.slnx, Afl.Core.csproj and 3 more".</returns>
    private static string Summarize(IReadOnlyList<string> files)
    {
        var named = string.Join(", ", files.Take(NamedMatches).Select(f => f[(f.LastIndexOf('/') + 1)..]));
        return files.Count > NamedMatches ? $"{named} and {files.Count - NamedMatches} more" : named;
    }

    /// <summary>
    ///     Puts the skill's own reason in front of what matched.
    /// </summary>
    /// <param name="skill">The skill.</param>
    /// <param name="evidence">What matched.</param>
    /// <returns>The reason.</returns>
    private static string ReasonFor(SkillDefinition skill, string evidence)
    {
        return skill.Recommend?.Reason is { Length: > 0 } reason ? $"{reason}: {evidence}" : char.ToUpperInvariant(evidence[0]) + evidence[1..];
    }
}
