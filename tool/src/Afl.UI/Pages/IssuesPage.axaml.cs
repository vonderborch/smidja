using Afl.Core.Execution;
using Afl.Core.Permissions;
using Afl.Core.Tickets;
using Afl.Core.Workflows;
using Afl.UI.DataContexts;
using Afl.UI.Views;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Niddy.Avalonia.Dialogs;
using Niddy.Avalonia.PageSystem;
using Niddy.Avalonia.Tables;
using Niddy.Avalonia.Toasts;

namespace Afl.UI.Pages;

/// <summary>
///     The selected repo's tickets, and starting a workflow run on one.
/// </summary>
[PageRegistration(DisplayName = "Issues", SortKey = "1", Shortcut = "Primary+1")]
[PageIcon(PageIcons.Issues)]
public partial class IssuesPage : AbstractAflPage<IssuesPageDataContext>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="IssuesPage" /> class.
    /// </summary>
    public IssuesPage()
    {
        this.InitializeComponent();
    }

    /// <summary>
    ///     Gets the page's data context.
    /// </summary>
    private IssuesPageDataContext Issues => this.PageDataContext;

    /// <inheritdoc />
    protected override IssuesPageDataContext CreatePageDataContext()
    {
        return new IssuesPageDataContext(this.AppDataContext);
    }

    /// <summary>
    ///     Reloads the tickets.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (this.Issues is { } issues)
        {
            await issues.RefreshAsync();
        }
    }

    /// <summary>
    ///     Reloads the tickets when Enter is pressed in the search box.
    /// </summary>
    /// <param name="sender">The box.</param>
    /// <param name="e">The event.</param>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.OnRefresh(sender, e);
        }
    }

    /// <summary>
    ///     Fetches the linked ticket.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnLink(object? sender, RoutedEventArgs e)
    {
        if (this.Issues is { } issues)
        {
            await issues.LinkAsync();
        }
    }

    /// <summary>
    ///     Fetches the linked ticket when Enter is pressed in the link box.
    /// </summary>
    /// <param name="sender">The box.</param>
    /// <param name="e">The event.</param>
    private void OnLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.OnLink(sender, e);
        }
    }

    /// <summary>
    ///     Opens the repo's settings, where it's linked to GitHub, GitLab or Jira.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private void OnOpenRepoSettings(object? sender, RoutedEventArgs e)
    {
        this.NavigateTo<RepoSettingsPage>(true);
    }

    /// <summary>
    ///     Asks for the name of a piece of work with no ticket, adds it as a local ticket, and starts a workflow on it.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnNewWork(object? sender, RoutedEventArgs e)
    {
        if (this.Issues is not { } issues)
        {
            return;
        }

        var name = await this.AskAsync("New work",
            "A short name for work that has no ticket, e.g. \"dark mode\". Its documents go in the scratch directory under the name's slug.",
            string.Empty, "Name");
        if (!string.IsNullOrWhiteSpace(name) && await issues.NewWorkAsync(name))
        {
            this.OnStart(sender, e);
        }
    }

    /// <summary>
    ///     Starts a workflow on a ticket that was double-clicked, or had Enter pressed on it.
    /// </summary>
    /// <param name="sender">The table.</param>
    /// <param name="e">The ticket's row.</param>
    private void OnTicketActivated(object? sender, TableGridItemEventArgs e)
    {
        if (this.Issues is { } issues && e.Item is Ticket ticket)
        {
            issues.SelectedTicket = ticket;
            this.OnStart(sender, new RoutedEventArgs());
        }
    }

    /// <summary>
    ///     Opens the selected ticket in the browser.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The event.</param>
    private void OnOpenTicket(object? sender, RoutedEventArgs e)
    {
        this.OpenUrl(this.Issues?.SelectedTicket?.Url);
    }

    /// <summary>
    ///     Copies the selected ticket's key, e.g. <c>PROJ-45</c>.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The event.</param>
    private void OnCopyTicketKey(object? sender, RoutedEventArgs e)
    {
        if (this.Issues?.SelectedTicket is { } ticket)
        {
            this.CopyText(ticket.Key, $"key {ticket.Key}");
        }
    }

    /// <summary>
    ///     Copies the link to the selected ticket.
    /// </summary>
    /// <param name="sender">The menu item.</param>
    /// <param name="e">The event.</param>
    private void OnCopyTicketLink(object? sender, RoutedEventArgs e)
    {
        if (this.Issues?.SelectedTicket is { Url: { Length: > 0 } url } ticket)
        {
            this.CopyText(url, $"link to {ticket.Key}");
        }
    }

    /// <summary>
    ///     Picks a workflow, defaulting to the repo's, and starts a run on the selected ticket.
    /// </summary>
    /// <param name="sender">The button.</param>
    /// <param name="e">The event.</param>
    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (this.Issues is not { SelectedTicket: { } ticket } issues)
        {
            return;
        }

        var choices = issues.WorkflowChoices();
        if (choices.Count == 0)
        {
            return;
        }

        var listing = await Dialog.Selection.Open(this, "Start workflow",
            $"Run which workflow on {ticket.Key}? The repo's default is listed first.", choices,
            l => $"{l.Workflow.Name} ({l.Workflow.Id}, {l.Scope.ToString().ToLowerInvariant()}) — {l.Workflow.Steps.Count} steps",
            "Start");
        if (listing is null)
        {
            return;
        }

        var parameters = await this.AskParametersAsync(listing.Workflow);
        if (parameters is null)
        {
            return;
        }

        var optional = issues.OptionalStepChoices(listing.Workflow, parameters);
        if (optional.Count > 0 && !await ChecklistView.Open(this, "Optional steps",
                "Tick the optional steps this run should include. The ones ticked to start with are the workflow's defaults.",
                optional, "Continue"))
        {
            return;
        }

        var (picked, skills) = await this.AskSkillsAsync(issues, listing.Workflow);
        if (!picked)
        {
            return;
        }

        // Only ask who does the AI work when there's a choice beyond Claude Code.
        var agents = issues.AgentChoices();
        var agent = agents.Count <= 1
            ? agents.FirstOrDefault()
            : await Dialog.Selection.Open(this, "Agent",
                "Who does the AI work for steps that don't pick their own agent?", agents,
                a => a.Description is null ? $"{a.Name} ({a.Id})" : $"{a.Name} ({a.Id}) — {a.Description}", "Start");
        if (agents.Count > 1 && agent is null)
        {
            return;
        }

        var threading = issues.ThreadingChoices(listing.Workflow, parameters);
        var thread = threading.Count == 0
            ? null
            : await Dialog.Selection.Open(this, "Threading",
                "This workflow starts child runs. Let them run at the same time where it allows, or force one at a time?",
                threading, t => $"{t.Label} — {t.Description}", "Start");
        if (threading.Count > 0 && thread is null)
        {
            return;
        }

        var permissions = await this.AskPermissionsAsync(issues);
        if (permissions is null)
        {
            return;
        }

        var mode = await Dialog.Selection.Open(this, "Run mode",
            "Run the agents' commands in the background, or in a terminal window you can watch? (Agents that don't run a command ignore this.)",
            issues.RunModeChoices(), m => $"{m.Label} — {m.Description}", "Start");
        if (mode is null)
        {
            return;
        }

        if (issues.Start(listing.Workflow, agent?.Id ?? ExecutorRegistry.DefaultExecutor, mode.Value, parameters,
                thread?.Value == IssuesPageDataContext.SingleThreading ? 1 : null,
                optional.ToDictionary(o => o.Value, o => o.IsSelected), permissions.Name, skills) is { } run)
        {
            this.ShowToast($"Started run {run.Id}.", ToastType.Success);
            this.NavigateTo<RunsPage>(addToBackStack: false, parameter: run.Id);
        }
    }

    /// <summary>
    ///     Asks which skills the run's agents get: first, for an empty repo with nothing saved, what it's built with; then
    ///     the skills recommended for the repo to tick, and the run's skills to remove from or add to.
    /// </summary>
    /// <param name="issues">The page's data context.</param>
    /// <param name="workflow">The workflow being started.</param>
    /// <returns>
    ///     Whether to go on, and the run's skills: null when they're the workflow's own (or there are no skills to pick).
    /// </returns>
    public async Task<(bool Go, IReadOnlyList<string>? Skills)> AskSkillsAsync(IssuesPageDataContext issues, WorkflowDefinition workflow)
    {
        var choices = issues.SkillChoices();
        if (choices.Count == 0 || await issues.SkillAdviceAsync(workflow) is not { } advice)
        {
            return (true, null);
        }

        if (advice.NeedsStack && await this.AskStackAsync(issues, advice, this,
                "This repo has no code yet, so afl can't tell which skills fit. What will it be built with? You can change this in the repo's settings.")
            && await issues.SkillAdviceAsync(workflow) is { } updated)
        {
            advice = updated;
        }

        var picker = new SkillPickerDataContext(advice, choices);
        if (!await SkillPickerView.Open(this, picker, async over =>
            {
                if (await this.AskStackAsync(issues, picker.Advice, over, "What is this project built with? Skills are recommended from it.")
                    && await issues.SkillAdviceAsync(workflow) is { } changed)
                {
                    picker.Reload(changed);
                }
            }))
        {
            return (false, null);
        }

        var skills = picker.Picked();
        return (true, skills.SequenceEqual(advice.Defaults, StringComparer.Ordinal) ? null : skills);
    }

    /// <summary>
    ///     Asks what the repo is built with and saves it.
    /// </summary>
    /// <param name="issues">The page's data context.</param>
    /// <param name="advice">The advice, for the languages and frameworks to offer.</param>
    /// <param name="over">A control in the window the dialog opens over.</param>
    /// <param name="description">Why it's asked.</param>
    /// <returns>True if it was saved.</returns>
    private async Task<bool> AskStackAsync(IssuesPageDataContext issues, SkillAdvice advice, Control over, string description)
    {
        var stack = new StackDataContext(advice.Choices, issues.Repo?.Stack);
        return await StackView.Open(over, stack, description) && issues.SaveStack(stack.ToStack());
    }

    /// <summary>
    ///     Asks how the run's agents get permission to run commands, warning (with the guard rails) before a policy that
    ///     lets them run anything.
    /// </summary>
    /// <param name="issues">The page's data context.</param>
    /// <returns>The policy, or null if they cancelled.</returns>
    public async Task<AbstractPermissionPolicy?> AskPermissionsAsync(IssuesPageDataContext issues)
    {
        var policy = await Dialog.Selection.Open(this, "Permissions",
            "When an agent wants to run a command or use a tool its step doesn't allow, what should happen?",
            issues.PermissionChoices(), p => $"{p.DisplayName} — {p.Description}", "Continue");
        if (policy?.Warning is not { } warning)
        {
            return policy;
        }

        var guards = issues.GuardRules();
        var listed = guards.Count == 0
            ? "There are no guard rails: nothing is refused."
            : "Still refused (the guard rails, editable in Settings → Permissions):\n" + string.Join("\n", guards.Select(g => $"• {g}"));
        return await this.WarnAsync($"Start in {policy.DisplayName}?", $"{warning}\n\n{listed}", $"Use {policy.DisplayName}")
            ? policy
            : null;
    }

    /// <summary>
    ///     Asks for a value for each of a workflow's parameters, starting from its default. A required parameter is asked
    ///     again until it gets a value.
    /// </summary>
    /// <param name="workflow">The workflow.</param>
    /// <returns>The values by id (empty when it has no parameters), or null if they cancelled.</returns>
    private async Task<Dictionary<string, string>?> AskParametersAsync(WorkflowDefinition workflow)
    {
        var values = new Dictionary<string, string>();
        foreach (var parameter in workflow.Parameters ?? [])
        {
            var label = parameter.Label ?? parameter.Id;
            var description = parameter.Description is null
                ? $"A value for {workflow.Name}'s parameter {label}."
                : $"{label}: {parameter.Description}";
            string? value;
            do
            {
                value = await this.AskAsync(workflow.Name, parameter.IsRequired ? description + " (Required.)" : description,
                    parameter.Default ?? string.Empty, label);
                if (value is null)
                {
                    return null;
                }
            }
            while (parameter.IsRequired && string.IsNullOrWhiteSpace(value));

            values[parameter.Id] = value;
        }

        return values;
    }
}
