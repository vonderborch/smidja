using System.Text.Json;
using Afl.Core.Agents;
using Afl.Core.Constants;
using Afl.Core.GitHosts;
using Afl.Core.Jira;
using Afl.Core.Permissions;
using Afl.Core.Repos;
using Afl.Core.Runs;
using Afl.Core.Steps;
using Afl.Core.Tickets;
using Afl.Core.Workflows;

namespace Afl.Cli.Output;

/// <summary>
///     Text renderers for the things the CLI prints.
/// </summary>
public static class TextRendering
{
    /// <summary>
    ///     Renders what a run is labelled with, in a line: its name, folder and tags.
    /// </summary>
    /// <param name="view">The run.</param>
    /// <param name="writer">The writer.</param>
    public static void RunLabelLine(RunView view, TextWriter writer)
    {
        var folder = view.Folder is null ? "at the top level" : $"in folder {view.Folder}";
        var tags = view.Tags.Count == 0 ? "no tags" : string.Join(' ', view.Tags.Select(t => $"#{t}"));
        writer.WriteLine($"Run {view.Id}: {view.Title ?? view.TicketTitle} ({folder}, {tags})");
    }

    /// <summary>
    ///     Renders a run in full.
    /// </summary>
    /// <param name="view">The run.</param>
    /// <param name="writer">The writer.</param>
    public static void Run(RunView view, TextWriter writer)
    {
        writer.WriteLine($"Run {view.Id}  [{Name(view.Status)}]  {view.TicketKey}: {view.Title ?? view.TicketTitle}");
        writer.WriteLine($"  repo {view.Repo}, workflow {view.Workflow}, agent {view.Agent}, {view.Mode} mode");
        if (view.Title is not null)
        {
            writer.WriteLine($"  ticket {view.TicketTitle}");
        }

        if (view.Folder is not null || view.Tags.Count > 0)
        {
            var folder = view.Folder is null ? null : $"in folder {view.Folder}";
            var tags = view.Tags.Count == 0 ? null : $"tags {string.Join(", ", view.Tags)}";
            writer.WriteLine($"  {string.Join(", ", new[] { folder, tags }.OfType<string>())}");
        }
        if (view.ParentRunId is not null)
        {
            writer.WriteLine($"  part of run {view.ParentRunId}, step {view.ParentStepId}");
        }

        if (view.WorkingPath is not null)
        {
            var branch = view.WorkspaceBranch is null ? string.Empty : $" on branch {view.WorkspaceBranch}";
            writer.WriteLine($"  works in {view.WorkingPath}{branch}{(view.Workspace is null ? string.Empty : $" ({view.Workspace})")}");
        }

        if (view.ScratchDirectory is not null)
        {
            writer.WriteLine($"  scratch {view.ScratchDirectory}");
        }

        if (view.MaxParallel is { } most)
        {
            writer.WriteLine(most == 1
                ? "  one at a time: parallel paths and child runs run one after another"
                : $"  at most {most} at once: parallel paths and child runs beyond that wait their turn");
        }

        if (view.LeftOutSteps.Count > 0)
        {
            writer.WriteLine($"  left out: {string.Join(", ", view.LeftOutSteps)}");
        }

        writer.WriteLine($"  permissions {view.PermissionPolicy}" + (view.PermissionRules.Count == 0
            ? string.Empty
            : $", this run's rules: {string.Join(", ", view.PermissionRules.Select(DescribeRule))}"));
        if (view.Skills is not null)
        {
            writer.WriteLine($"  skills {(view.Skills.All(string.IsNullOrEmpty) ? "(none)" : string.Join(", ", view.Skills.Where(s => s.Length > 0)))}");
        }

        writer.WriteLine();
        for (var i = 0; i < view.Steps.Count; i++)
        {
            var step = view.Steps[i];
            var marker = view.CurrentStep?.Index == i ? ">" : " ";
            var gate = step.Advance switch
            {
                StepConstants.AdvanceApprove => " (approval)",
                StepConstants.AdvanceApproveFinal => " (approval at the end)",
                _ => string.Empty,
            };
            var replays = step.Replays > 0 ? $"  replayed {step.Replays}x" : string.Empty;
            var status = step.NotReached ? "not reached" : Name(step.Status);
            writer.WriteLine($" {marker} {i + 1}. {step.Name + gate,-40} {status}{Iteration(step.Iteration, step.MaxRuns)}{replays}");
        }

        if (view.Path.Any(v => v.Iteration > 1 || v.Branch is not null) || view.Steps.Any(s => s.Status == StepStatus.Skipped || s.NotReached))
        {
            writer.WriteLine();
            writer.WriteLine($"Path: {StepVisit.Describe(view.Path)}");
        }

        if (view.Paths.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Paths:");
            foreach (var branch in view.Paths)
            {
                var at = view.PathSteps.FirstOrDefault(s => s.RunId == branch.RunId) is { } waiting
                    ? $", at {waiting.Id} ({Name(waiting.Status)})"
                    : string.Empty;
                writer.WriteLine($"  {branch.Describe()}{at}");
            }
        }

        if (view.CurrentStep is { } current)
        {
            writer.WriteLine();
            var agent = current.Agents.Count > 0
                ? $", agents {string.Join(", ", current.Agents)}, combine {current.Combine}"
                : current.Agent == view.Agent ? string.Empty : $", agent {current.Agent}";
            writer.WriteLine($"Current step: {current.Name} ({current.Type}{agent}, {Name(current.Status)}){Iteration(current.Iteration, current.MaxRuns)}");
            StepBody(current, writer);
        }

        if (view.Next.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Next:");
            foreach (var command in view.Next)
            {
                writer.WriteLine($"  {command}");
            }
        }
    }

    /// <summary>
    ///     Renders one step of a run in full, with its earlier attempts.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="writer">The writer.</param>
    public static void Step(StepView step, TextWriter writer)
    {
        writer.WriteLine($"{step.Index + 1}. {step.Name} [{step.Id}, {step.Type}, advance {step.Advance}]  {Name(step.Status)}{Iteration(step.Iteration, step.MaxRuns)}");
        StepBody(step, writer);
        WriteBlock(writer, "Output", step.Status == StepStatus.Done ? step.Output : null);
        if (step.Answers.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Answers:");
            foreach (var (id, answer) in step.Answers)
            {
                writer.WriteLine($"  {id}: {answer}");
            }
        }

        for (var i = step.History.Count - 1; i >= 0; i--)
        {
            var attempt = step.History[i];
            writer.WriteLine();
            var reason = attempt.Reason is null ? string.Empty : $", reason: {attempt.Reason}";
            writer.WriteLine($"--- Attempt {i + 1} ({Name(attempt.Status)}, replaced by {Name(attempt.Cause)} {attempt.ReplacedAt:g}{reason})");
            WriteBlock(writer, null, attempt.Error ?? attempt.Output);
            WriteBlock(writer, "The AI asked", attempt.AskRounds is { Count: > 0 } rounds ? AskRound.Describe(rounds) : null);
            WriteBlock(writer, "Agent", attempt.AgentChoice?.Describe());
            WriteBlock(writer, "Handed off", attempt.Delegations is { Count: > 0 } delegations ? DescribeDelegations(delegations) : null);
            WriteBlock(writer, "Tickets filed", attempt.CreatedTickets is { Count: > 0 } tickets ? DescribeTickets(tickets) : null);
            WriteBlock(writer, "Child runs", attempt.ChildRuns is { Count: > 0 } children ? string.Join("\n", children.Select(c => c.Describe())) : null);
            WriteBlock(writer, "Saved to", attempt.ArtifactPath);
        }

        writer.WriteLine();
        writer.WriteLine($"Replay it with: afl run replay {step.RunId} {step.Id} [--feedback \"...\"]");
    }

    /// <summary>
    ///     Renders what a step is waiting on: its error, prompt or instructions, output so far, feedback and inputs.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="writer">The writer.</param>
    private static void StepBody(StepView step, TextWriter writer)
    {
        WriteBlock(writer, "Error", step.Error);
        WriteBlock(writer, step.Status == StepStatus.AwaitingUser && step.Interactive is null ? "Instructions" : "Prompt",
            step.Status is StepStatus.AwaitingUser or StepStatus.AwaitingSession ? step.Prompt : null);
        WriteBlock(writer, "Output", step.Status is StepStatus.Done or StepStatus.Pending ? null : step.Output);
        WriteBlock(writer, "Open questions", step.OpenQuestions.Count > 0
            ? string.Join("\n", step.OpenQuestions.Select(q => $"{q.Number}. {q.PlainText}"))
            : null);
        WriteBlock(writer, "Needs permission", step.PermissionRequests.Count > 0
            ? string.Join("\n", step.PermissionRequests.Select(DescribeRequest))
            : null);
        WriteBlock(writer, "Permissions decided", step.DecidedPermissions.Count > 0
            ? string.Join("\n", step.DecidedPermissions.Select(DescribeRequest))
            : null);
        WriteBlock(writer, "Feedback", step.Feedback.Count > 0 ? string.Join("\n", step.Feedback) : null);
        WriteBlock(writer, "The AI asked", step.AskRounds.Count > 0 ? AskRound.Describe(step.AskRounds) : null);
        WriteBlock(writer, step.Status == StepStatus.ChoosingAgent ? "Choosing the agent" : "Agent",
            step.Status == StepStatus.ChoosingAgent
                ? $"Any of these can do it: {string.Join(", ", step.AgentCandidates)}.\n\n{step.Prompt}"
                : step.AgentChoice?.Describe());
        WriteBlock(writer, "Handed off", step.Delegations.Count > 0 ? DescribeDelegations(step.Delegations) : null);
        WriteBlock(writer, "Tickets filed", step.CreatedTickets.Count > 0 ? DescribeTickets(step.CreatedTickets) : null);
        WriteBlock(writer, "Child runs", step.ChildRuns.Count > 0 ? string.Join("\n", step.ChildRuns.Select(c => c.Describe())) : null);
        WriteBlock(writer, "Saved to", step.ArtifactPath);
        WriteBlock(writer, "Notes", step.Notes.Count > 0 ? string.Join("\n", step.Notes) : null);
        foreach (var candidate in step.Candidates)
        {
            WriteBlock(writer, $"Result from {candidate.Agent} ({Name(candidate.Status)})",
                candidate.Error ?? candidate.Output ?? candidate.Prompt ?? "(none yet)");
        }

        if (step.AsksInputs)
        {
            writer.WriteLine();
            writer.WriteLine(AskedHeading(step));
            foreach (var input in step.Inputs)
            {
                var optional = input.Required ? string.Empty : ", optional";
                var fallback = input.Default is null ? string.Empty : $", default {input.Default}";
                writer.WriteLine($"  {input.Id}: {input.Label} ({input.Accepts}{optional}{fallback})");
            }
        }
    }

    /// <summary>
    ///     Describes a permission request: its number, the refused call and either the rules that would allow it or
    ///     what was decided.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The description.</returns>
    private static string DescribeRequest(PermissionRequest request)
    {
        var line = $"{request.Index}. {request.Denial.Display}";
        if (request.Decision is not { } decision)
        {
            return $"{line}\n   allow with: {string.Join("  or  ", request.Denial.SuggestedRules)}";
        }

        var scope = request.Scope is { } where ? $" ({Name(where)})" : string.Empty;
        return decision == PermissionDecision.Deny ? $"{line}: denied" : $"{line}: {Name(decision)} {request.Rule}{scope}";
    }

    /// <summary>
    ///     Describes a permission rule: the rule, marked when it denies.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The description.</returns>
    public static string DescribeRule(PermissionRule rule)
    {
        return rule.Kind == PermissionRuleKind.Deny ? $"deny {rule.Rule}" : rule.Rule;
    }

    /// <summary>
    ///     Describes the sub-tasks a step's AI handed to other agents, one per line.
    /// </summary>
    /// <param name="delegations">The sub-tasks.</param>
    /// <returns>The description.</returns>
    private static string DescribeDelegations(IEnumerable<Delegation> delegations)
    {
        return string.Join("\n", delegations.Select(d => d.Describe()));
    }

    /// <summary>
    ///     Describes the tickets a step's AI filed, one per line.
    /// </summary>
    /// <param name="tickets">The tickets.</param>
    /// <returns>The description.</returns>
    private static string DescribeTickets(IEnumerable<CreatedTicket> tickets)
    {
        return string.Join("\n", tickets.Select(t => t.Describe()));
    }

    /// <summary>
    ///     Says when a step asks its inputs.
    /// </summary>
    /// <param name="step">The step, asking inputs now.</param>
    /// <returns>The heading.</returns>
    private static string AskedHeading(StepView step)
    {
        if (step.Status == StepStatus.AwaitingInput)
        {
            return step.AiAsking ? "Waiting for your answers. The AI is asking:" : "Waiting for your answers before the step starts:";
        }

        return step.Status == StepStatus.AwaitingApproval ? "Asks when approving:" : "Asks when marking done:";
    }

    /// <summary>
    ///     Renders runs one per line.
    /// </summary>
    /// <param name="views">The runs.</param>
    /// <param name="writer">The writer.</param>
    public static void RunList(IReadOnlyList<RunView> views, TextWriter writer)
    {
        if (views.Count == 0)
        {
            writer.WriteLine("No runs. Start one with: afl start <ticket>");
            return;
        }

        // Child runs are listed under their parent, indented; ones whose parent isn't listed stay at the top level.
        foreach (var (view, depth) in RunStore.Nest(views, v => v.Id, v => v.ParentRunId))
        {
            var step = view.CurrentStep is { } current ? $"{current.Name} ({Name(current.Status)})" : string.Empty;
            var indent = new string(' ', depth * 2);
            var folder = depth == 0 && view.Folder is not null ? $"{view.Folder}/ " : string.Empty;
            var ticket = Truncate($"{indent}{folder}{view.TicketKey} {view.Title ?? view.TicketTitle}", 50);
            var tags = view.Tags.Count == 0 ? string.Empty : $"  #{string.Join(" #", view.Tags)}";
            var state = RunStates.Info(view.State);
            writer.WriteLine($"{view.Id}  {state.Symbol} {state.Label,-9} {view.Repo,-20} {ticket,-50}  {step}{tags}");
        }
    }

    /// <summary>
    ///     Renders a repo's run folders as a tree, with the runs a command moved.
    /// </summary>
    /// <param name="view">The folders.</param>
    /// <param name="writer">The writer.</param>
    public static void RunFolders(RunFoldersView view, TextWriter writer)
    {
        if (view.Moved.Count > 0)
        {
            writer.WriteLine($"Moved {string.Join(", ", view.Moved)}.");
        }

        if (view.Folders.Count == 0)
        {
            writer.WriteLine("No folders. Create one with: afl run folder create <folder>");
            return;
        }

        foreach (var folder in view.Folders)
        {
            var indent = new string(' ', folder.Folder.Count(c => c == RunLabels.FolderSeparator) * 2);
            var runs = folder.Runs == 1 ? "1 run" : $"{folder.Runs} runs";
            writer.WriteLine($"{indent}{RunLabels.NameOf(folder.Folder)}/  ({runs})");
        }
    }

    /// <summary>
    ///     Renders repos one per line.
    /// </summary>
    /// <param name="repos">The repos.</param>
    /// <param name="writer">The writer.</param>
    public static void RepoList(IReadOnlyList<RepoRecord> repos, TextWriter writer)
    {
        if (repos.Count == 0)
        {
            writer.WriteLine("No repos registered. Run afl inside a git repo, or: afl repo add <path>");
            return;
        }

        foreach (var repo in repos)
        {
            Repo(repo, writer);
        }
    }

    /// <summary>
    ///     Renders a repo on one line.
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="writer">The writer.</param>
    public static void Repo(RepoRecord repo, TextWriter writer)
    {
        writer.WriteLine($"{repo.Id,-28} {repo.Host?.Project ?? "-",-30} workflow={repo.DefaultWorkflowId ?? "-"}  jira={repo.Jira?.InstanceId ?? "-"}  {repo.Path}");
    }

    /// <summary>
    ///     Renders tickets one per line.
    /// </summary>
    /// <param name="tickets">The tickets.</param>
    /// <param name="writer">The writer.</param>
    public static void TicketList(IReadOnlyList<Ticket> tickets, TextWriter writer)
    {
        if (tickets.Count == 0)
        {
            writer.WriteLine("No matching tickets.");
            return;
        }

        foreach (var ticket in tickets)
        {
            var labels = ticket.Labels.Count > 0 ? $"  [{string.Join(", ", ticket.Labels)}]" : string.Empty;
            writer.WriteLine($"{ticket.Key,-24} {Truncate(ticket.Title, 70)}{labels}");
        }
    }

    /// <summary>
    ///     Renders a ticket in full.
    /// </summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="writer">The writer.</param>
    public static void Ticket(Ticket ticket, TextWriter writer)
    {
        writer.WriteLine($"{ticket.Key}: {ticket.Title}");
        writer.WriteLine($"  {ticket.Status} {ticket.Url}");
        if (ticket.Assignee is not null)
        {
            writer.WriteLine($"  assigned to {ticket.Assignee}");
        }

        if (ticket.Labels.Count > 0)
        {
            writer.WriteLine($"  labels: {string.Join(", ", ticket.Labels)}");
        }

        WriteBlock(writer, null, ticket.Body);
    }

    /// <summary>
    ///     Renders workflows one per line.
    /// </summary>
    /// <param name="listings">The workflows.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="matches">How each listing matched a search, by id; null when nothing was searched for.</param>
    public static void WorkflowList(IReadOnlyList<WorkflowListing> listings, TextWriter writer,
        IReadOnlyDictionary<string, LibraryMatch>? matches = null)
    {
        if (listings.Count == 0 && matches is not null)
        {
            writer.WriteLine("No workflows match.");
        }

        foreach (var listing in listings)
        {
            var steps = string.Join(" > ", listing.Workflow.Steps.Select(s => $"{s.Id}({StepKind(s)})"));
            writer.WriteLine($"{listing.Workflow.Id,-16} {listing.Scope,-8} {listing.Workflow.Name}");
            writer.WriteLine($"{string.Empty,-25} {steps}");
            LibraryNotes(listing.Workflow, matches, writer, 25);
        }
    }

    /// <summary>
    ///     Renders a library document's tags, and where a search found it when that wasn't its name, under its line.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="matches">How each listing matched a search, by id; null when nothing was searched for.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="indent">How far to indent the notes.</param>
    private static void LibraryNotes(AbstractLibraryDocument document, IReadOnlyDictionary<string, LibraryMatch>? matches, TextWriter writer,
        int indent)
    {
        var pad = new string(' ', indent + 1);
        if (document.Tags is { Count: > 0 } tags)
        {
            writer.WriteLine($"{pad}tags: {string.Join(", ", tags)}");
        }

        if (matches?.GetValueOrDefault(document.Id) is { InName: false, Snippet: { } snippet })
        {
            writer.WriteLine($"{pad}found: {snippet}");
        }
    }

    /// <summary>
    ///     Describes a step's type for a listing, with <c>approve</c> when it waits for approval, e.g.
    ///     <c>ai, approve</c>.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <returns>The description.</returns>
    private static string StepKind(AbstractStepDefinition step)
    {
        if (step.Include is not null)
        {
            return $"include {step.Include}";
        }

        var type = step is WorkflowStepDefinition workflowStep ? $"{step.Type} {workflowStep.Workflow}" : step.Type;
        if (step.Use is not null)
        {
            return $"use {step.Use}";
        }

        if (step.Advance == StepConstants.AdvanceApproveFinal)
        {
            return $"{type}, {StepConstants.AdvanceApproveFinal}";
        }

        return StepHandlerRegistry.Instance.Contains(step.Type) && StepHandlerRegistry.Instance.For(step).IsGated(step)
            ? $"{type}, {StepConstants.AdvanceApprove}"
            : type;
    }

    /// <summary>
    ///     Renders a workflow in full.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <param name="writer">The writer.</param>
    public static void Workflow(WorkflowDefinition workflow, TextWriter writer)
    {
        writer.WriteLine($"{workflow.Name} ({workflow.Id})");
        if (workflow.Description is not null)
        {
            writer.WriteLine($"  {workflow.Description}");
        }

        foreach (var parameter in workflow.Parameters ?? [])
        {
            writer.WriteLine(Parameter(parameter));
        }

        foreach (var requirement in workflow.Requires ?? [])
        {
            writer.WriteLine($"  requires {requirement.Workflow} to have run on the ticket");
        }

        if (workflow.OptionalSteps is { Count: > 0 } optionalSteps)
        {
            writer.WriteLine($"  optional steps (afl start --include/--exclude): {string.Join(", ", optionalSteps.Select(s => $"{s.Id} ({(s.Optional!.Default ? "in" : "out")} by default)"))}");
        }

        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];
            writer.WriteLine();
            var agent = step.Agent is null ? string.Empty : $", agent {step.Agent}";
            var optional = step.Optional is null ? string.Empty : $", optional, {(step.Optional.Default ? "in" : "out")} by default";
            writer.WriteLine($"{i + 1}. {step.Name} [{step.Id}, {StepKind(step)}{agent}{optional}]");
            WriteBlock(writer, null, step.Optional?.Help);
            WriteBlock(writer, null, step.Templates().FirstOrDefault());
            WriteBlock(writer, null, step.With is { Count: > 0 } with ? string.Join("\n", with.Select(p => $"{p.Key}: {p.Value}")) : null);
        }
    }

    /// <summary>
    ///     Describes a parameter in one indented line: its id, label, default and description.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The line.</returns>
    private static string Parameter(ParameterDefinition parameter)
    {
        var fallback = parameter.IsRequired ? "required" : $"default \"{parameter.Default}\"";
        var label = parameter.Label is null ? string.Empty : $" {parameter.Label}";
        var description = parameter.Description is null ? string.Empty : $": {parameter.Description}";
        return $"  param {parameter.Id}{label} ({fallback}){description}";
    }

    /// <summary>
    ///     Renders common steps one per line.
    /// </summary>
    /// <param name="listings">The common steps.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="matches">How each listing matched a search, by id; null when nothing was searched for.</param>
    public static void CommonStepList(IReadOnlyList<CommonStepListing> listings, TextWriter writer,
        IReadOnlyDictionary<string, LibraryMatch>? matches = null)
    {
        if (listings.Count == 0 && matches is not null)
        {
            writer.WriteLine("No common steps match.");
            return;
        }

        if (listings.Count == 0)
        {
            writer.WriteLine("No common steps. Save one with: afl step-new <id> --from-workflow <workflow> --step <step>");
            return;
        }

        foreach (var listing in listings)
        {
            var parameters = listing.Step.Parameters is { Count: > 0 } list ? $"  params: {string.Join(", ", list.Select(p => p.Id))}" : string.Empty;
            writer.WriteLine($"{listing.Step.Id,-20} {listing.Scope,-8} {listing.Step.Name} [{StepKind(listing.Step.Step)}]{parameters}");
            LibraryNotes(listing.Step, matches, writer, 29);
        }
    }

    /// <summary>
    ///     Renders a common step in full.
    /// </summary>
    /// <param name="step">The common step.</param>
    /// <param name="writer">The writer.</param>
    public static void CommonStep(CommonStepDefinition step, TextWriter writer)
    {
        writer.WriteLine($"{step.Name} ({step.Id}) [{StepKind(step.Step)}]");
        if (step.Description is not null)
        {
            writer.WriteLine($"  {step.Description}");
        }

        foreach (var parameter in step.Parameters ?? [])
        {
            writer.WriteLine(Parameter(parameter));
        }

        WriteBlock(writer, null, step.Step.Templates().FirstOrDefault());
        writer.WriteLine();
        writer.WriteLine($"Use it in a workflow step: {{ \"use\": \"{step.Id}\", \"with\": {{ ... }} }}");
    }

    /// <summary>
    ///     Renders common skills one per line.
    /// </summary>
    /// <param name="listings">The skills.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="matches">How each listing matched a search, by id; null when nothing was searched for.</param>
    public static void SkillList(IReadOnlyList<SkillListing> listings, TextWriter writer, IReadOnlyDictionary<string, LibraryMatch>? matches = null)
    {
        if (listings.Count == 0 && matches is not null)
        {
            writer.WriteLine("No skills match.");
            return;
        }

        if (listings.Count == 0)
        {
            writer.WriteLine("No skills. Create one with: afl skill-new <id>, or afl skill-import <SKILL.md>");
            return;
        }

        foreach (var listing in listings)
        {
            writer.WriteLine($"{listing.Skill.Id,-20} {listing.Scope,-8} {listing.Skill.Name}: {listing.Skill.Description?.ReplaceLineEndings(" ")}");
            LibraryNotes(listing.Skill, matches, writer, 29);
        }
    }

    /// <summary>
    ///     Renders a common skill in full.
    /// </summary>
    /// <param name="skill">The skill.</param>
    /// <param name="writer">The writer.</param>
    public static void Skill(SkillDefinition skill, TextWriter writer)
    {
        writer.WriteLine($"{skill.Name} ({skill.Id})");
        writer.WriteLine($"  {skill.Description}");
        foreach (var file in (skill.Files ?? []).Keys.Order())
        {
            writer.WriteLine($"  file: {file}");
        }

        WriteBlock(writer, null, skill.Content);
        writer.WriteLine();
        writer.WriteLine($"Use it in a step or workflow: \"skills\": [\"{skill.Id}\"]");
    }

    /// <summary>
    ///     Renders the skills to offer for a run: the workflow's own, those recommended for the repo, and, for an empty
    ///     repo, how to say what it will use.
    /// </summary>
    /// <param name="advice">The advice.</param>
    /// <param name="workflow">The workflow.</param>
    /// <param name="writer">The writer.</param>
    public static void SkillAdvice(SkillAdvice advice, WorkflowDefinition workflow, TextWriter writer)
    {
        writer.WriteLine($"{workflow.Name} picks: {(advice.Defaults.Count == 0 ? "(no skills)" : string.Join(", ", advice.Defaults))}");
        if (advice.Stack is { } stack)
        {
            writer.WriteLine($"Project: {string.Join(", ", stack.Languages.Concat(stack.Frameworks))}");
        }

        if (advice.NeedsStack)
        {
            writer.WriteLine("The repo is empty, so afl can't tell what it's built with. Say with --language and --framework, e.g.:");
            writer.WriteLine($"  languages:  {string.Join(", ", advice.Choices.Languages)}");
            writer.WriteLine($"  frameworks: {string.Join(", ", advice.Choices.Frameworks)}");
        }

        if (advice.Recommended.Count == 0)
        {
            writer.WriteLine("No skills recommended for this repo.");
            return;
        }

        writer.WriteLine("Recommended for this repo:");
        foreach (var recommendation in advice.Recommended)
        {
            writer.WriteLine($"  {recommendation.Skill.Id,-20} {recommendation.Reason}");
        }

        writer.WriteLine();
        writer.WriteLine("Start with your pick: afl start ... --skills a,b (or --add-skills / --drop-skills).");
    }

    /// <summary>
    ///     Renders agents one per line.
    /// </summary>
    /// <param name="views">The agents.</param>
    /// <param name="writer">The writer.</param>
    public static void AgentList(IReadOnlyList<AgentView> views, TextWriter writer)
    {
        foreach (var view in views)
        {
            var traits = new[] { view.BuiltIn ? "built-in" : null, view.Available ? null : "not installed" }
                .Where(t => t is not null).ToList();
            writer.WriteLine(traits.Count == 0
                ? $"{view.Id,-16} {view.Kind,-8} {view.Name}"
                : $"{view.Id,-16} {view.Kind,-8} {view.Name}  ({string.Join(", ", traits)})");
            if (view.Command is not null || view.Model is not null)
            {
                writer.WriteLine($"{string.Empty,-25} {view.Command}{(view.Model is null ? string.Empty : $"  model {view.Model}")}");
            }

            if (view.Capabilities.Count > 0)
            {
                writer.WriteLine($"{string.Empty,-25} can: {string.Join(", ", view.Capabilities)}");
            }

            if (view.McpServers.Count > 0)
            {
                writer.WriteLine($"{string.Empty,-25} MCP servers: {string.Join(", ", view.McpServers)}");
            }
        }
    }

    /// <summary>
    ///     Renders the global settings.
    /// </summary>
    /// <param name="view">The settings.</param>
    /// <param name="writer">The writer.</param>
    public static void Settings(SettingsView view, TextWriter writer)
    {
        writer.WriteLine($"default-agent     {view.DefaultAgent}");
        writer.WriteLine($"default-run-mode  {view.DefaultRunMode}");
        writer.WriteLine($"default-selector  {view.DefaultSelector}");
        writer.WriteLine($"scratch-directory {view.ScratchDirectory}");
        writer.WriteLine($"agent-preference  {(view.AgentPreference.Count == 0 ? "(agents.json order)" : string.Join(", ", view.AgentPreference))}");
        writer.WriteLine(view.Capabilities.Count == 0
            ? "capabilities      (none of your own; see afl capability list)"
            : $"capabilities      {string.Join(", ", view.Capabilities.Select(c => c.Id))}");
        writer.WriteLine($"default-permissions {view.DefaultPermissions}");
        writer.WriteLine($"guard rails       {(view.GuardRules.Count == 0 ? "(none)" : string.Join(", ", view.GuardRules))}");
        writer.WriteLine($"notify-on         {(view.NotifyOn.Count == 0 ? "(none: no notifications)" : string.Join(", ", view.NotifyOn))}");
    }

    /// <summary>
    ///     Renders the permission policies and rules.
    /// </summary>
    /// <param name="view">The rules.</param>
    /// <param name="writer">The writer.</param>
    public static void Permissions(PermissionsView view, TextWriter writer)
    {
        writer.WriteLine("Policies (afl start --permissions <name>):");
        foreach (var policy in view.Policies)
        {
            var mark = policy.Name == view.DefaultPolicy ? " (default)" : policy.AllowedAsDefault ? string.Empty : " (per run only)";
            writer.WriteLine($"  {policy.Name,-8}{mark} {policy.Description}");
        }

        WriteRules(writer, "Guard rails (denied in every run)", view.GuardRules);
        WriteRules(writer, "Global rules", view.Global.Select(DescribeRule).ToList());
        if (view.Repo is not null)
        {
            WriteRules(writer, $"Rules for {view.Repo}", view.RepoRules.Select(DescribeRule).ToList());
        }
    }

    /// <summary>
    ///     Writes a heading and a list of rules, one per line.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="heading">The heading.</param>
    /// <param name="rules">The rules.</param>
    private static void WriteRules(TextWriter writer, string heading, IReadOnlyList<string> rules)
    {
        writer.WriteLine();
        writer.WriteLine($"{heading}:");
        if (rules.Count == 0)
        {
            writer.WriteLine("  (none)");
        }

        foreach (var rule in rules)
        {
            writer.WriteLine($"  {rule}");
        }
    }

    /// <summary>
    ///     Renders capabilities one per line.
    /// </summary>
    /// <param name="views">The capabilities.</param>
    /// <param name="writer">The writer.</param>
    public static void CapabilityList(IReadOnlyList<CapabilityView> views, TextWriter writer)
    {
        foreach (var view in views)
        {
            writer.WriteLine($"{view.Id,-16} {(view.BuiltIn ? "built-in" : "yours"),-9} {view.Description}");
        }
    }

    /// <summary>
    ///     Renders an agent's definition as JSON.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="writer">The writer.</param>
    public static void Agent(AgentDefinition agent, TextWriter writer)
    {
        writer.WriteLine(agent.BuiltIn ? $"{agent.Name} ({agent.Id}, built-in)" : $"{agent.Name} ({agent.Id})");
        writer.WriteLine(JsonSerializer.Serialize(agent, JsonConstants.Options));
    }

    /// <summary>
    ///     Renders agent presets one per line.
    /// </summary>
    /// <param name="presets">The presets.</param>
    /// <param name="writer">The writer.</param>
    public static void AgentPresets(IReadOnlyList<AgentPresetView> presets, TextWriter writer)
    {
        foreach (var preset in presets)
        {
            var state = preset.Added ? "added" : preset.Installed ? "installed" : "not found";
            writer.WriteLine($"{preset.Agent.Id,-12} {state,-10} {preset.Agent.Name}  ({preset.Agent.Command})");
        }

        writer.WriteLine();
        writer.WriteLine("Add one with: afl agent add <preset> [--id <id>] [--model <model>]");
    }

    /// <summary>
    ///     Renders MCP servers one per line.
    /// </summary>
    /// <param name="views">The servers.</param>
    /// <param name="writer">The writer.</param>
    public static void McpServerList(IReadOnlyList<McpServerView> views, TextWriter writer)
    {
        if (views.Count == 0)
        {
            writer.WriteLine("No MCP servers. Add one with: afl mcp add <id> --command <cmd>, or afl mcp import");
            return;
        }

        foreach (var view in views)
        {
            var missing = view.Secrets.Where(s => !s.Set).Select(s => s.Name).ToList();
            var state = missing.Count == 0 ? string.Empty : $"  (missing secret {string.Join(", ", missing)})";
            writer.WriteLine($"{view.Id,-20} {view.Transport,-6} {view.Target}{state}");
        }
    }

    /// <summary>
    ///     Renders an MCP server; secrets show only whether they're set.
    /// </summary>
    /// <param name="view">The server.</param>
    /// <param name="writer">The writer.</param>
    public static void McpServer(McpServerView view, TextWriter writer)
    {
        writer.WriteLine($"{view.Name} ({view.Id}){(view.Source is null ? string.Empty : $", imported from {view.Source}")}");
        if (view.Description is not null)
        {
            writer.WriteLine($"  {view.Description}");
        }

        writer.WriteLine($"  {view.Transport}: {view.Target}");
        foreach (var (name, value) in view.Env)
        {
            writer.WriteLine($"  env {name}={value}");
        }

        foreach (var (name, value) in view.Headers)
        {
            writer.WriteLine($"  header {name}: {value}");
        }

        foreach (var secret in view.Secrets)
        {
            writer.WriteLine($"  secret {secret.Name}: {(secret.Set ? "set" : $"not set (afl mcp secret {view.Id} {secret.Name}, or {secret.Variable})")}");
        }
    }

    /// <summary>
    ///     Renders the servers found in other apps' configuration.
    /// </summary>
    /// <param name="views">The servers found.</param>
    /// <param name="writer">The writer.</param>
    public static void McpCandidates(IReadOnlyList<McpCandidateView> views, TextWriter writer)
    {
        if (views.Count == 0)
        {
            writer.WriteLine("No MCP servers found in Claude Code, Claude Desktop, Cursor or Codex configuration.");
            return;
        }

        foreach (var view in views)
        {
            var notes = new[]
                {
                    view.Exists ? "already registered" : null,
                    view.SecretNames.Count > 0 ? $"secrets: {string.Join(", ", view.SecretNames)}" : null,
                }
                .Where(n => n is not null).ToList();
            writer.WriteLine($"{view.Source + "/" + view.Id,-32} {view.Transport,-6} {view.Target}" +
                             (notes.Count == 0 ? string.Empty : $"  ({string.Join("; ", notes)})"));
        }

        writer.WriteLine();
        writer.WriteLine("Import with: afl mcp import --server <id> (or source/id), or --all");
    }

    /// <summary>
    ///     Renders a server's tools.
    /// </summary>
    /// <param name="view">The tools.</param>
    /// <param name="writer">The writer.</param>
    public static void McpTools(McpToolsView view, TextWriter writer)
    {
        if (view.Tools.Count == 0)
        {
            writer.WriteLine($"'{view.Server}' has no tools.");
            return;
        }

        foreach (var tool in view.Tools)
        {
            writer.WriteLine(tool.Name);
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                writer.WriteLine($"  {tool.Description.Trim().Split('\n')[0]}");
            }

            if (tool.Arguments.Count > 0)
            {
                writer.WriteLine($"  arguments: {string.Join(", ", tool.Arguments.Select(a => tool.Required.Contains(a) ? a + "*" : a))}");
            }
        }
    }

    /// <summary>
    ///     Renders Jira instances one per line.
    /// </summary>
    /// <param name="views">The instances.</param>
    /// <param name="writer">The writer.</param>
    public static void JiraInstanceList(IReadOnlyList<JiraInstanceView> views, TextWriter writer)
    {
        if (views.Count == 0)
        {
            writer.WriteLine("No Jira instances. Add one with: afl jira instance add <id> --kind cloud|datacenter --url <url>");
            return;
        }

        foreach (var view in views)
        {
            JiraInstance(view, writer);
        }
    }

    /// <summary>
    ///     Renders a Jira instance on one line.
    /// </summary>
    /// <param name="view">The instance.</param>
    /// <param name="writer">The writer.</param>
    public static void JiraInstance(JiraInstanceView view, TextWriter writer)
    {
        var instance = view.Instance;
        var token = view.HasToken ? "token set" : $"no token (set {view.TokenVariable} or re-add)";
        writer.WriteLine($"{instance.Id,-16} {Name(instance.Kind),-11} {instance.BaseUrl,-40} {instance.Email ?? string.Empty,-28} {token}");
    }

    /// <summary>
    ///     Renders git host connections one per line.
    /// </summary>
    /// <param name="views">The connections.</param>
    /// <param name="writer">The writer.</param>
    public static void GitHostConnectionList(IReadOnlyList<GitHostConnectionView> views, TextWriter writer)
    {
        foreach (var view in views)
        {
            GitHostConnection(view, writer);
        }
    }

    /// <summary>
    ///     Renders a git host connection on one line, with where its sign-in comes from (never the token).
    /// </summary>
    /// <param name="view">The connection.</param>
    /// <param name="writer">The writer.</param>
    public static void GitHostConnection(GitHostConnectionView view, TextWriter writer)
    {
        var connection = view.Connection;
        var builtIn = connection.IsBuiltIn ? " (built in)" : string.Empty;
        writer.WriteLine($"{connection.Id,-16} {view.KindName,-7} {connection.BaseUrl,-36} {view.TokenStatus}{builtIn}");
    }

    /// <summary>
    ///     Renders the account a git host sign-in is for.
    /// </summary>
    /// <param name="view">The result.</param>
    /// <param name="writer">The writer.</param>
    public static void GitHostTest(GitHostTestView view, TextWriter writer)
    {
        var name = string.IsNullOrWhiteSpace(view.User.Name) ? string.Empty : $" ({view.User.Name})";
        writer.WriteLine($"{view.Connection}: signed in as {view.User.Login}{name}. {view.TokenStatus}.");
    }

    /// <summary>
    ///     Renders a repo's GitHub or GitLab link.
    /// </summary>
    /// <param name="view">The link.</param>
    /// <param name="writer">The writer.</param>
    public static void RepoHostLink(RepoHostLinkView view, TextWriter writer)
    {
        if (view.Link is not { } link)
        {
            writer.WriteLine($"{view.Repo} isn't linked to GitHub or GitLab. Run: afl repo host detect, or afl repo host link <connection> <project>");
            return;
        }

        var connection = view.Connection is { } c ? $"{c.DisplayName} ({c.BaseUrl})" : $"{link.ConnectionId} (missing: add it again or relink)";
        writer.WriteLine($"{view.Repo} -> {link.Project} on {connection}");
    }

    /// <summary>
    ///     Renders a repo's Jira link and saved filters.
    /// </summary>
    /// <param name="view">The link.</param>
    /// <param name="writer">The writer.</param>
    public static void JiraLink(JiraLinkView view, TextWriter writer)
    {
        if (view.Link is not { } link)
        {
            writer.WriteLine($"{view.Repo} isn't linked to Jira. Link it with: afl jira link --instance <id>");
            return;
        }

        var instance = view.Instance is { } i ? $"{i.Id} ({i.BaseUrl})" : $"{link.InstanceId} (missing: re-add it or relink)";
        writer.WriteLine($"{view.Repo} -> Jira {instance}");
        writer.WriteLine(link.Projects.Count == 0
            ? "  Projects: any (associate some with: afl jira project add KEY …)"
            : $"  Projects: {string.Join(", ", link.Projects)}");
        if (link.Filters.Count == 0)
        {
            writer.WriteLine("  No saved filters. Add one with: afl jira filter add <name> --project KEY --assignee me …");
            return;
        }

        foreach (var filter in link.Filters)
        {
            var marker = string.Equals(filter.Name, link.DefaultFilter, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            writer.WriteLine($" {marker} {filter.Name,-20} {DescribeFilter(filter)}");
        }
    }

    /// <summary>
    ///     Renders Jira projects one per line.
    /// </summary>
    /// <param name="projects">The projects.</param>
    /// <param name="writer">The writer.</param>
    public static void JiraProjects(IReadOnlyList<JiraProject> projects, TextWriter writer)
    {
        foreach (var project in projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine($"{project.Key,-14} {project.Name}");
        }
    }

    /// <summary>
    ///     Renders Jira boards one per line.
    /// </summary>
    /// <param name="boards">The boards.</param>
    /// <param name="writer">The writer.</param>
    public static void JiraBoards(IReadOnlyList<JiraBoard> boards, TextWriter writer)
    {
        foreach (var board in boards)
        {
            writer.WriteLine($"{board.Id,-8} {board.Type ?? "-",-8} {board.ProjectKey ?? "-",-12} {board.Name}");
        }
    }

    /// <summary>
    ///     Describes a filter's criteria on one line.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The description.</returns>
    public static string DescribeFilter(JiraFilter filter)
    {
        var parts = new List<string>();
        Add("project", filter.Project);
        Add("board", filter.BoardId?.ToString());
        Add("team", filter.Team);
        Add("assignee", filter.Assignee);
        Add("status", filter.Statuses.Count > 0 ? string.Join(",", filter.Statuses) : null);
        Add("labels", filter.Labels.Count > 0 ? string.Join(",", filter.Labels) : null);
        Add("jql", filter.Jql);
        return parts.Count == 0 ? "(all open tickets)" : string.Join("  ", parts);

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                parts.Add($"{name}={value}");
            }
        }
    }

    /// <summary>
    ///     Writes an indented block of text under an optional heading, if there is any text.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="heading">The heading.</param>
    /// <param name="text">The text.</param>
    public static void WriteBlock(TextWriter writer, string? heading, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        writer.WriteLine();
        if (heading is not null)
        {
            writer.WriteLine($"{heading}:");
        }

        foreach (var line in text.TrimEnd().ReplaceLineEndings("\n").Split('\n'))
        {
            writer.WriteLine(line.Length == 0 ? string.Empty : $"    {line}");
        }
    }

    /// <summary>
    ///     Names a status the way the JSON output does (e.g. <c>awaiting-approval</c>).
    /// </summary>
    /// <param name="value">The status.</param>
    /// <returns>The kebab-case name.</returns>
    private static string Name(Enum value)
    {
        return JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
    }

    /// <summary>
    ///     Describes which time round a step a loop can bring back is on.
    /// </summary>
    /// <param name="iteration">The iteration, counting from 1.</param>
    /// <param name="maxRuns">How many times the loop may run the step; null when it's in no loop.</param>
    /// <returns>"  iteration n/max", or empty for a step in no loop.</returns>
    private static string Iteration(int iteration, int? maxRuns)
    {
        return maxRuns is { } max ? $"  iteration {iteration}/{max}" : string.Empty;
    }

    /// <summary>
    ///     Shortens text to a maximum length.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The maximum length.</param>
    /// <returns>The text, shortened with an ellipsis if needed.</returns>
    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..(length - 3)] + "...";
    }
}
