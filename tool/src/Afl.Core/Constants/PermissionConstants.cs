namespace Afl.Core.Constants;

/// <summary>
///     Well-known values for what agents may run: permission policies, rule scopes and the guard rails.
/// </summary>
public static class PermissionConstants
{
    /// <summary>
    ///     The policy that pauses a step when its agent is refused something and asks the user. This is the default.
    /// </summary>
    public const string PolicyAsk = "ask";

    /// <summary>
    ///     The policy that notes what an agent was refused and carries on, as afl always used to.
    /// </summary>
    public const string PolicyStrict = "strict";

    /// <summary>
    ///     The policy that lets agents run anything but the guard rails, without asking.
    /// </summary>
    public const string PolicyAuto = "auto";

    /// <summary>
    ///     The policy a run uses when neither it nor the settings say.
    /// </summary>
    public const string DefaultPolicy = PolicyAsk;

    /// <summary>
    ///     The Claude Code permission mode that allows every tool not denied.
    /// </summary>
    public const string BypassPermissionsMode = "bypassPermissions";

    /// <summary>
    ///     How many times a step pauses for permission in one attempt before it stops asking and notes what was refused.
    /// </summary>
    public const int MaxPermissionRounds = 5;

    /// <summary>
    ///     The longest one-line summary of a refused tool call.
    /// </summary>
    public const int MaxSummaryLength = 160;

    /// <summary>
    ///     The longest command an exact <c>Bash(…)</c> rule is suggested for.
    /// </summary>
    public const int MaxExactRuleLength = 200;

    /// <summary>
    ///     The deny rules every run gets, whatever its policy, until the user changes them in settings.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGuardRules =
    [
        "Bash(git push:*)",
        "Bash(sudo:*)",
        "Bash(rm -rf /:*)",
        "Bash(rm -rf ~:*)",
        "Read(~/.ssh/**)",
        "Read(~/.afl/**)",
        "Edit(~/.afl/**)",
    ];

    /// <summary>
    ///     The message that resumes an agent after the user has decided on what it was refused. Placeholders: what it may
    ///     now use, and what is still refused.
    /// </summary>
    public const string ResumeMessageFormat =
        "The user has reviewed the tool calls you were refused.\n\n{0}{1}\nCarry on with the task from where you stopped. " +
        "Retry what you may now use; for anything still refused, find another way or say what you couldn't do.";

    /// <summary>
    ///     What blocks a call the agent was refused again after the user allowed it: something afl can't see, such as
    ///     the agent's own settings.
    /// </summary>
    public const string RefusedAfterAllowed = "a deny rule afl can't see (it was refused again after being allowed)";

    /// <summary>
    ///     The note on a step whose agent was refused calls a deny rule blocks, so afl didn't ask about them.
    ///     Placeholders: the agent, and the calls with what blocks each.
    /// </summary>
    public const string BlockedNoteFormat =
        "{0} was refused {1}. A deny rule blocks these whatever is allowed, so afl didn't ask about them. If the agent " +
        "should be able to, change the guard rails ('afl permission guards' or Settings → Permissions) or remove the " +
        "deny rule ('afl permission remove').";

    /// <summary>
    ///     The note on a step whose output was accepted although its agent was refused something.
    /// </summary>
    public const string AcceptedWithDenialsNote =
        "Accepted as it was, although the agent was refused: {0}. It may not have finished everything.";
}
