using Afl.Core.Permissions;
using ReactiveUI;

namespace Afl.UI.DataContexts;

/// <summary>
///     One refused tool call a step paused on, on the Runs page or an AI draft, with what the user decided about it: allow once,
///     always allow (with the rule and where to save it), or deny.
/// </summary>
public class PermissionRequestDataContext : ReactiveObject
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PermissionRequestDataContext" /> class.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="scopes">The scopes an always-allow rule can be saved to, or null for all of them.</param>
    public PermissionRequestDataContext(PermissionRequest request, IReadOnlyList<PermissionScope>? scopes = null)
    {
        this.Request = request;
        this.Rule = request.Denial.SuggestedRules.FirstOrDefault() ?? request.Denial.Tool;
        this.ScopeChoices = scopes ?? Scopes;
        this.Scope = this.ScopeChoices.Contains(PermissionScope.Repo) ? PermissionScope.Repo : this.ScopeChoices[0];
    }

    /// <summary>
    ///     Gets the scopes an always-allow rule can be saved to, as shown.
    /// </summary>
    public static IReadOnlyList<PermissionScope> Scopes { get; } = Enum.GetValues<PermissionScope>();

    /// <summary>
    ///     Gets the scopes this request's always-allow rule can be saved to, as shown: e.g. no repo for an AI draft.
    /// </summary>
    public IReadOnlyList<PermissionScope> ScopeChoices { get; }

    /// <summary>
    ///     Gets the request.
    /// </summary>
    public PermissionRequest Request { get; }

    /// <summary>
    ///     Gets the refused call as shown: its number, the tool and what it would have done.
    /// </summary>
    public string Label => $"{this.Request.Index}. {this.Request.Denial.Display}";

    /// <summary>
    ///     Gets the rules that would allow the call, most specific first.
    /// </summary>
    public IReadOnlyList<string> Rules => this.Request.Denial.SuggestedRules;

    /// <summary>
    ///     Gets or sets the rule to allow; one of <see cref="Rules" /> or one the user typed.
    /// </summary>
    public string Rule
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.RuleProblem));
        }
    } = string.Empty;

    /// <summary>
    ///     Gets why <see cref="Rule" /> can't be used, or null when it can.
    /// </summary>
    public string? RuleProblem => this.Decision == PermissionDecision.Deny ? null : PermissionRule.Problem(this.Rule);

    /// <summary>
    ///     Gets or sets where an always-allow rule is saved.
    /// </summary>
    public PermissionScope Scope
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    ///     Gets or sets what the user decided; null while undecided.
    /// </summary>
    public PermissionDecision? Decision
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.IsOnce));
            this.RaisePropertyChanged(nameof(this.IsAlways));
            this.RaisePropertyChanged(nameof(this.IsDenied));
            this.RaisePropertyChanged(nameof(this.RuleProblem));
            this.RaisePropertyChanged(nameof(this.IsReady));
        }
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the call is allowed for the next turn only.
    /// </summary>
    public bool IsOnce
    {
        get => this.Decision == PermissionDecision.Once;
        set => this.Pick(PermissionDecision.Once, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the rule is saved and allowed from now on.
    /// </summary>
    public bool IsAlways
    {
        get => this.Decision == PermissionDecision.Always;
        set => this.Pick(PermissionDecision.Always, value);
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the call stays refused.
    /// </summary>
    public bool IsDenied
    {
        get => this.Decision == PermissionDecision.Deny;
        set => this.Pick(PermissionDecision.Deny, value);
    }

    /// <summary>
    ///     Gets a value indicating whether the request is decided with a usable rule.
    /// </summary>
    public bool IsReady => this.Decision is not null && this.RuleProblem is null;

    /// <summary>
    ///     Returns what to pass the engine for this request.
    /// </summary>
    /// <returns>The choice.</returns>
    public PermissionChoice ToChoice()
    {
        var decision = this.Decision ?? PermissionDecision.Deny;
        return new PermissionChoice(this.Request.Index, decision,
            decision == PermissionDecision.Deny ? null : this.Rule.Trim(), this.Scope);
    }

    /// <summary>
    ///     Sets or clears the decision from one of the three toggles.
    /// </summary>
    /// <param name="decision">The toggle's decision.</param>
    /// <param name="on">Whether the toggle was turned on.</param>
    private void Pick(PermissionDecision decision, bool on)
    {
        if (on)
        {
            this.Decision = decision;
        }
        else if (this.Decision == decision)
        {
            this.Decision = null;
        }
    }
}
