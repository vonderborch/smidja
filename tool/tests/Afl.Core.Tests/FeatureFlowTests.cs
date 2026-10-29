using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Helpers;
using Afl.Core.Runs;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Core.Tests;

/// <summary>
///     Tests for the built-in <c>feature-flow</c> / <c>debug-flow</c> family: that each one expands and validates, and a
///     whole feature run on a fake agent, from the intent form to merged, reviewed and reported tickets.
/// </summary>
public class FeatureFlowTests : AbstractAgentPoolTest
{
    /// <summary>
    ///     The built-in workflows of the family.
    /// </summary>
    public static readonly TheoryData<string> FlowWorkflows =
        ["feature-flow", "debug-flow", "research", "architecture", "ticket-spike", "ticket-prototype", "ticket-wave", "ticket-implement", "reviewer"];

    /// <summary>
    ///     Initializes a new instance of the <see cref="FeatureFlowTests" /> class: the repo gets a first commit, and the
    ///     <c>main</c> agent plays every AI step of the flow (see <see cref="Respond" />).
    /// </summary>
    public FeatureFlowTests()
    {
        File.WriteAllText(Path.Combine(this.RepoPath, "README.md"), "hello\n");
        ProcessRunner.Run("git", ["add", "-A"], this.RepoPath);
        ProcessRunner.Run("git", ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first"], this.RepoPath);
        this.Fake("main").Respond = this.Respond;
    }

    /// <summary>
    ///     Gets the scratch directory of the run's ticket, in the main checkout.
    /// </summary>
    private string Scratch => Path.Combine(this.RepoPath, ".scratch", "dark-mode");

    /// <summary>
    ///     Gets how many times each kind of prompt was seen, keyed by the prompt's first words.
    /// </summary>
    private Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the working directory each ticket was implemented in.
    /// </summary>
    private Dictionary<string, string> ImplementedIn { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the prompt of the optional steps after the build, keyed by step id.
    /// </summary>
    private Dictionary<string, string> Prompts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the steps approved by <see cref="RunToTheEndAsync" />, in order.
    /// </summary>
    private List<string> Approvals { get; } = [];

    [Theory]
    [MemberData(nameof(FlowWorkflows))]
    public void BuiltIn_ExpandsAndValidates(string id)
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        var workflow = library.GetRequired(id);

        Assert.Empty(new WorkflowComposer(library).Validate(workflow));
        var values = (workflow.Parameters ?? []).ToDictionary(p => p.Id, p => p.Default ?? "x");
        var expanded = new WorkflowComposer(library).Expand(workflow, values);
        Assert.All(expanded.Steps.OfType<AiStepDefinition>().Where(s => s.SkipSkills is null && !WritesTheSpecs(id, s.Id)),
            s => Assert.Equal(["caveman"], s.Skills));
    }

    [Fact]
    public void BuiltIn_WritesTheSpecsWithoutTheStyle()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);

        var expanded = new WorkflowComposer(library).Expand(library.GetRequired("feature-flow"));

        var steps = expanded.Steps.OfType<AiStepDefinition>().ToList();
        Assert.All(steps.Where(s => WritesTheSpecs("feature-flow", s.Id)), s => Assert.Null(s.Skills));
        Assert.Equal(["caveman"], steps.Single(s => s.Id == "research").Skills);
        Assert.All(steps, s => Assert.Null(s.SkipSkills));
    }

    /// <summary>
    ///     Returns whether a built-in workflow's step writes the specs, which it does without the style skill.
    /// </summary>
    /// <param name="workflow">The workflow id.</param>
    /// <param name="step">The step id.</param>
    /// <returns>True for feature-flow's specs steps.</returns>
    private static bool WritesTheSpecs(string workflow, string step)
    {
        return workflow == "feature-flow" && step is "specs" or "specs-review";
    }

    [Fact]
    public void Reviewers_AreCommonSteps_CheckedForEachOneNamed()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);
        var implement = library.GetRequired("ticket-implement");
        var review = implement.Steps.Single(s => s.Id == "review");
        review.With!["reviewers"] = "standards, nope";

        var errors = new WorkflowComposer(library).Validate(implement);

        Assert.Contains(errors, e => e.StartsWith("Step 'review': Workflow 'reviewer', step 'review': no common step 'review-nope'.", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("'review-standards'", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltIn_WithNoSkillsPicked_GivesNoSkill()
    {
        var library = new WorkflowLibrary(this.Paths, this.RepoPath);

        var expanded = new WorkflowComposer(library).Expand(library.GetRequired("feature-flow"), skills: []);

        Assert.All(expanded.Steps.OfType<AbstractAgentStepDefinition>(), s => Assert.Null(s.Skills));
    }

    [Fact]
    public async Task FeatureFlow_TakesNamedWorkFromIntentToMergedReviewedTickets()
    {
        var ticket = await new LocalTicketSource().GetAsync(this.RegisterRepo(), TicketRef.Parse("local:Dark mode"));
        var workflow = new WorkflowLibrary(this.Paths, this.RepoPath).GetRequired("feature-flow");
        var run = this.Engine.Create(this.RegisterRepo(), workflow, ticket, "main");

        run = await this.Engine.DriveAsync(run.Id);
        var approvals = new List<string>();
        for (var guard = 0; run.Status == RunStatus.Waiting && guard < 30; guard++)
        {
            var step = run.Steps.First(s => s.Status is StepStatus.AwaitingUser or StepStatus.AwaitingApproval);
            if (step.StepId == "pr")
            {
                run = await this.Engine.CompleteAsync(run.Id, null, new Dictionary<string, string> { ["url"] = "https://example.test/pr/1" });
            }
            else if (step.Status == StepStatus.AwaitingUser)
            {
                run = await this.Engine.CompleteAsync(run.Id, null, new Dictionary<string, string>
                {
                    ["goal"] = "Let people pick a dark theme.", ["done"] = "The toggle works.", ["criteria"] = "1. It persists.",
                });
            }
            else
            {
                approvals.Add(step.StepId);
                run = await this.Engine.ApproveAsync(run.Id);
            }
        }

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("## Goal\n\nLet people pick a dark theme.", File.ReadAllText(Path.Combine(this.Scratch, "INTENT.md")));
        Assert.StartsWith("# Research", File.ReadAllText(Path.Combine(this.Scratch, "RESEARCH.md")));
        Assert.StartsWith("# Findings", File.ReadAllText(Path.Combine(this.Scratch, "FINDINGS.md")));
        Assert.EndsWith("<!-- specs: complete -->", File.ReadAllText(Path.Combine(this.Scratch, "SPECS.md")).Trim());

        // One round of a spike and a prototype looped back to research; the second round found nothing more, so it went
        // on to the final specs, whose review loop asked for approval only once it was done.
        Assert.Equal(["research", "identify", "findings", "research", "identify", "specs-review", "tickets"], approvals);
        Assert.Equal(2, this.Counts["Write the specs"]);
        Assert.Equal(1, this.Counts["Research spike"]);
        Assert.True(File.Exists(Path.Combine(this.Scratch, "spike-001-theme-docs_REPORT.md")));
        Assert.True(File.Exists(Path.Combine(this.Scratch, "prototype-002-try-it_REPORT.md")));

        // The prototype ran in its own worktree and was kept unmerged; the real tickets were merged, wave by wave.
        Assert.StartsWith(this.Paths.WorktreesDirectory, this.ImplementedIn["prototype-002-try-it"]);
        Assert.False(File.Exists(Path.Combine(this.RepoPath, "prototype-002-try-it.txt")));
        Assert.Contains("afl/dark-mode/prototype-002-try-it", ProcessRunner.Run("git", ["branch"], this.RepoPath).StandardOutput);
        foreach (var id in new[] { "feature-003-toggle", "feature-004-theme", "chore-005-docs" })
        {
            Assert.True(File.Exists(Path.Combine(this.RepoPath, id + ".txt")), id);
            Assert.StartsWith(this.Paths.WorktreesDirectory, this.ImplementedIn[id]);
            Assert.True(File.Exists(Path.Combine(this.Scratch, id + "_REPORT.md")), id);
        }

        // One reviewer asked for changes once, so that ticket went round twice; three reviewers per pass.
        Assert.Equal(1 + 2 + 1, this.Counts["Implement ticket"]);
        Assert.Equal(3 * 4, this.Counts["Review the work"]);
        Assert.Contains("CHANGES fixed", File.ReadAllText(Path.Combine(this.RepoPath, "feature-003-toggle.txt")));
        Assert.True(ProcessRunner.Run("git", ["check-ignore", "-q", Path.Combine(this.Scratch, "SPECS.md")], this.RepoPath).Success);
        Assert.DoesNotContain(".scratch", ProcessRunner.Run("git", ["status", "--porcelain"], this.RepoPath).StandardOutput);

        // The optional steps on by default ran after the build: the security review saw the merged tickets, harden got
        // its findings, and the PR step asked for its URL. The ones off by default were left out.
        Assert.Contains("git log --merges --grep 'afl/dark-mode/'", this.Prompts["security"]);
        Assert.Contains("minor: log less", this.Prompts["harden"]);
        Assert.StartsWith("# Security", File.ReadAllText(Path.Combine(this.Scratch, "SECURITY.md")));
        Assert.Equal(["deploy", "observe", "retro"], run.LeftOutSteps);
        Assert.All(run.Steps.Where(s => run.LeftOutSteps.Contains(s.StepId)), s => Assert.Equal(StepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Research_LoopsSpikesAndPrototypesBackToResearch_ThenAnswers()
    {
        var run = await this.RunToTheEndAsync("research", new Dictionary<string, string>
        {
            ["question"] = "Which theming approach works on every platform?", ["why"] = "It decides the dark mode design.",
        });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("## What we need to know\n\nWhich theming approach", File.ReadAllText(Path.Combine(this.Scratch, "INTENT.md")));
        Assert.StartsWith("# Answer", File.ReadAllText(Path.Combine(this.Scratch, "ANSWER.md")));
        Assert.Equal(["research", "identify", "findings", "research", "identify", "answer"], this.Approvals);

        // The spikes serve the research rather than specs, and the user's choice of approach reached the planner.
        Assert.Contains("draft it serves, " + Path.Combine(this.Scratch, "RESEARCH.md"), this.Prompts["spike"]);
        Assert.Contains("settled with: spikes and prototypes", this.Prompts["identify"]);
        Assert.DoesNotContain("SPECS.md", this.Prompts["identify"]);
        Assert.Contains("afl/dark-mode/prototype-002-try-it", ProcessRunner.Run("git", ["branch"], this.RepoPath).StandardOutput);
        Assert.False(File.Exists(Path.Combine(this.RepoPath, "prototype-002-try-it.txt")));
    }

    [Fact]
    public async Task Architecture_ScoresTheOptionsAgainstThePreferences_ThenRecommends()
    {
        var run = await this.RunToTheEndAsync("architecture", new Dictionary<string, string>
        {
            ["decision"] = "Where theme settings live.", ["priorities"] = "simplicity, performance", ["weights"] = "simplicity 5, performance 2",
        });

        Assert.Equal(RunStatus.Done, run.Status);
        Assert.Contains("## What matters most\n\nsimplicity, performance", File.ReadAllText(Path.Combine(this.Scratch, "INTENT.md")));
        Assert.StartsWith("# Options", File.ReadAllText(Path.Combine(this.Scratch, "OPTIONS.md")));
        Assert.StartsWith("# Evaluation", File.ReadAllText(Path.Combine(this.Scratch, "EVALUATION.md")));
        Assert.StartsWith("# ADR", File.ReadAllText(Path.Combine(this.Scratch, "RECOMMENDATION.md")));

        // A round of findings went back to the options, not the research; saving the ADR in the repo is left out by default.
        Assert.Equal(["research", "options", "identify", "findings", "options", "identify", "recommend"], this.Approvals);
        Assert.Contains("draft it serves, " + Path.Combine(this.Scratch, "OPTIONS.md"), this.Prompts["spike"]);
        Assert.Equal(["adr"], run.LeftOutSteps);
    }

    /// <summary>
    ///     Runs a built-in workflow on the Dark mode ticket to the end, answering its form and approving every gate.
    /// </summary>
    /// <param name="id">The workflow id.</param>
    /// <param name="answers">The answers to the workflow's form.</param>
    /// <returns>The finished run.</returns>
    private async Task<Run> RunToTheEndAsync(string id, Dictionary<string, string> answers)
    {
        var ticket = await new LocalTicketSource().GetAsync(this.RegisterRepo(), TicketRef.Parse("local:Dark mode"));
        var workflow = new WorkflowLibrary(this.Paths, this.RepoPath).GetRequired(id);
        var run = await this.Engine.DriveAsync(this.Engine.Create(this.RegisterRepo(), workflow, ticket, "main").Id);
        for (var guard = 0; run.Status == RunStatus.Waiting && guard < 30; guard++)
        {
            var step = run.Steps.First(s => s.Status is StepStatus.AwaitingUser or StepStatus.AwaitingApproval);
            if (step.Status == StepStatus.AwaitingUser)
            {
                run = await this.Engine.CompleteAsync(run.Id, null, answers);
            }
            else
            {
                this.Approvals.Add(step.StepId);
                run = await this.Engine.ApproveAsync(run.Id);
            }
        }

        return run;
    }

    [Fact]
    public void FeatureFlow_OptionalSteps_CanBeIncludedOrLeftOut()
    {
        var ticket = new Ticket { Source = TicketRef.LocalSource, Key = "dark-mode", Title = "Dark mode" };
        var workflow = new WorkflowLibrary(this.Paths, this.RepoPath).GetRequired("feature-flow");

        var run = this.Engine.Create(this.RegisterRepo(), workflow, ticket, "main",
            optionalSteps: new Dictionary<string, bool> { ["retro"] = true, ["security"] = false });
        var ex = Assert.Throws<AflException>(() => this.Engine.Create(this.RegisterRepo(), workflow, ticket, "main",
            optionalSteps: new Dictionary<string, bool> { ["tickets"] = false }));

        Assert.Equal(["security", "deploy", "observe"], run.LeftOutSteps);
        Assert.Equal(AflErrorKind.InvalidInput, ex.Kind);
        Assert.Contains("has no optional step 'tickets'; they are security, harden, deploy, observe, pr, retro.", ex.Message);
    }

    /// <summary>
    ///     Plays the flow's AI steps: research and the first specs draft (twice), identifying a spike and a prototype
    ///     (then nothing, the second time), the spike's research, the prototype (which writes <c>&lt;ticket&gt;.txt</c>),
    ///     the findings, the final specs and two specs-review passes, the tickets, and per ticket an implementation that
    ///     writes <c>&lt;ticket&gt;.txt</c>, three reviewers (the standards one sends feature-003 back once) and a report.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The reply.</returns>
    private ExecutionResult Respond(ExecutionRequest request)
    {
        var prompt = request.Prompt;
        string Count(string key)
        {
            lock (this.Counts)
            {
                this.Counts[key] = this.Counts.GetValueOrDefault(key) + 1;
                return this.Counts[key].ToString();
            }
        }

        string reply;
        if (prompt.StartsWith("We are planning", StringComparison.Ordinal))
        {
            reply = "# Research: Dark mode\n## Sources read\nREADME.md\n\nSECURITY IMPACT: YES";
        }
        else if (prompt.StartsWith("Write the specs", StringComparison.Ordinal))
        {
            reply = $"# Specs: Dark mode (draft {Count("Write the specs")})";
        }
        else if (prompt.StartsWith("Review the specs", StringComparison.Ordinal))
        {
            reply = prompt.Contains("Pass 1.", StringComparison.Ordinal) ? "# Specs: Dark mode\nmore" : "# Specs: Dark mode\nall\n<!-- specs: complete -->";
        }
        else if (prompt.StartsWith("Decide what we still have to find out", StringComparison.Ordinal))
        {
            this.Prompts["identify"] = prompt;
            if (Count("Identify") == "1")
            {
                foreach (var id in new[] { "spike-001-theme-docs", "prototype-002-try-it" })
                {
                    File.WriteAllText(Path.Combine(this.Scratch, id + ".md"), $"# {id}");
                }

                reply = "Spikes: spike-001-theme-docs\nPrototypes: prototype-002-try-it\n\n## Why\nUnsure.";
            }
            else
            {
                reply = "NONE\n\n## Why\nAll settled.";
            }
        }
        else if (prompt.StartsWith("Research spike ", StringComparison.Ordinal))
        {
            Count("Research spike");
            this.Prompts["spike"] = prompt;
            reply = "# Report: spike-001-theme-docs\n## Answer\nUse CSS variables.";
        }
        else if (prompt.StartsWith("The spikes and prototypes", StringComparison.Ordinal))
        {
            reply = "# Findings: Dark mode\n## Prototypes\nprototype-002-try-it: waiting";
        }
        else if (prompt.StartsWith("Split the specs", StringComparison.Ordinal))
        {
            foreach (var id in new[] { "feature-003-toggle", "feature-004-theme", "chore-005-docs" })
            {
                File.WriteAllText(Path.Combine(this.Scratch, id + ".md"), $"# {id}");
            }

            reply = "Wave 1: feature-003-toggle, feature-004-theme\nWave 2: chore-005-docs\n\n## Why this order\nDocs last.";
        }
        else if (prompt.StartsWith("Implement ticket ", StringComparison.Ordinal) || prompt.StartsWith("Build prototype ", StringComparison.Ordinal))
        {
            var prototype = prompt.StartsWith("Build prototype ", StringComparison.Ordinal);
            if (!prototype)
            {
                Count("Implement ticket");
            }

            var id = prompt[(prototype ? "Build prototype " : "Implement ticket ").Length..].Split(' ')[0];
            lock (this.ImplementedIn)
            {
                this.ImplementedIn[id] = request.WorkingDirectory;
            }

            var file = Path.Combine(request.WorkingDirectory, id + ".txt");
            File.AppendAllText(file, prompt.Contains("VERDICT: CHANGES", StringComparison.Ordinal) ? "CHANGES fixed\n" : "built\n");
            reply = $"Built {id}.";
        }
        else if (prompt.StartsWith("Review the work for ticket ", StringComparison.Ordinal))
        {
            Count("Review the work");
            var sendBack = prompt.Contains("ticket feature-003-toggle (", StringComparison.Ordinal) && prompt.Contains("through the standards lens", StringComparison.Ordinal) &&
                           !File.ReadAllText(Path.Combine(request.WorkingDirectory, "feature-003-toggle.txt")).Contains("fixed", StringComparison.Ordinal);
            reply = sendBack ? "major: no tests\nVERDICT: CHANGES" : "Fine.\nVERDICT: PASS";
        }
        else if (prompt.StartsWith("Review the changes the build merged", StringComparison.Ordinal))
        {
            this.Prompts["security"] = prompt;
            reply = "# Security\nminor: log less\nVERDICT: PASS";
        }
        else if (prompt.StartsWith("Harden the change", StringComparison.Ordinal))
        {
            this.Prompts["harden"] = prompt;
            reply = "Hardened.";
        }
        else if (prompt.StartsWith("Answer the question", StringComparison.Ordinal))
        {
            reply = "# Answer: Dark mode\n## The answer\nCSS variables.";
        }
        else if (prompt.StartsWith("Lay out the options", StringComparison.Ordinal))
        {
            reply = "# Options: Dark mode\n## Option 1: keep it";
        }
        else if (prompt.StartsWith("Score the options", StringComparison.Ordinal))
        {
            reply = "# Evaluation: Dark mode\n## Scores";
        }
        else if (prompt.StartsWith("Recommend an option", StringComparison.Ordinal))
        {
            reply = "# ADR: Keep theme settings in one file\n## Status\nProposed";
        }
        else if (prompt.StartsWith("Write the report", StringComparison.Ordinal))
        {
            reply = "# Report";
        }
        else
        {
            reply = "?";
        }

        return new ExecutionResult(ExecutionStatus.Completed, reply, request.SessionId);
    }
}
