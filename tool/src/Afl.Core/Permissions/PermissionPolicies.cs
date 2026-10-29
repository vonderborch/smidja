using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Execution;
using Afl.Core.Registration;

namespace Afl.Core.Permissions;

/// <summary>
///     Registers an <see cref="AbstractPermissionPolicy" /> under the name runs and settings use for it.
/// </summary>
/// <param name="name">The policy name.</param>
public class PermissionPolicyRegistrationAttribute(string name) : AbstractRegistrationAttribute(name);

/// <summary>
///     How a run's agents get permission to run commands and use tools: what is passed to them up front and what
///     happens when one is refused something. A run picks one when it starts (<see cref="Runs.Run.PermissionPolicy" />).
///     A new policy is one subclass with <see cref="PermissionPolicyRegistrationAttribute" />.
/// </summary>
/// <param name="name">The policy name.</param>
public abstract class AbstractPermissionPolicy(string name)
{
    /// <summary>
    ///     Gets the policy name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets the name shown in front-ends.
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    ///     Gets a line describing the policy, shown in front-ends.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    ///     Gets a value indicating whether the policy may be the default for new runs. A policy that lets agents do a lot
    ///     without asking has to be chosen for each run.
    /// </summary>
    public virtual bool AllowedAsDefault => true;

    /// <summary>
    ///     Gets a warning to show whoever picks the policy, or null when it needs none.
    /// </summary>
    public virtual string? Warning => null;

    /// <summary>
    ///     Gets a value indicating whether a step whose agent was refused something pauses to ask the user, rather than
    ///     noting it and carrying on.
    /// </summary>
    public virtual bool PausesOnDenial => false;

    /// <summary>
    ///     Gets what to tell the user when an agent was refused something and the step carried on without it.
    /// </summary>
    public virtual string DenialAdvice =>
        "If it needs them, allow them with 'afl permission add', start the run with the ask policy, or add them to the step's allowedTools.";

    /// <summary>
    ///     Adds the run's rules to a request: allow rules after the step's own allowed tools, deny rules (the guard rails
    ///     first) as disallowed tools.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="rules">The rules for the run.</param>
    public virtual void ApplyTo(ExecutionRequest request, PermissionRuleSet rules)
    {
        request.AllowedTools = request.AllowedTools.Concat(rules.Allowed).Distinct().ToList();
        request.DisallowedTools = request.DisallowedTools.Concat(rules.Denied).Distinct().ToList();
    }
}

/// <summary>
///     Pauses a step when its agent is refused something, asks the user, and carries on with what they allow. The
///     default.
/// </summary>
[PermissionPolicyRegistration(PermissionConstants.PolicyAsk)]
public class AskPermissionPolicy() : AbstractPermissionPolicy(PermissionConstants.PolicyAsk)
{
    /// <inheritdoc />
    public override string DisplayName => "Ask";

    /// <inheritdoc />
    public override string Description =>
        "When an agent is refused a command, the step pauses and asks you: allow once, always allow, or deny.";

    /// <inheritdoc />
    public override bool PausesOnDenial => true;

    /// <inheritdoc />
    public override string DenialAdvice =>
        "If it needs them, allow them with 'afl permission add' or add them to the step's allowedTools.";
}

/// <summary>
///     Notes what an agent was refused and carries on: only the step's own tools and saved rules are allowed.
/// </summary>
[PermissionPolicyRegistration(PermissionConstants.PolicyStrict)]
public class StrictPermissionPolicy() : AbstractPermissionPolicy(PermissionConstants.PolicyStrict)
{
    /// <inheritdoc />
    public override string DisplayName => "Strict";

    /// <inheritdoc />
    public override string Description =>
        "Agents get only their steps' tools and your saved rules; anything else is refused and noted, without asking.";
}

/// <summary>
///     Lets agents run anything except the guard rails and deny rules, without asking. Only steps in the usual
///     editing mode (<see cref="StepConstants.DefaultPermissionMode" />) are let loose; steps that ask to be read-only
///     or to plan keep their mode. It must be chosen for each run, with a warning.
/// </summary>
[PermissionPolicyRegistration(PermissionConstants.PolicyAuto)]
public class AutoPermissionPolicy() : AbstractPermissionPolicy(PermissionConstants.PolicyAuto)
{
    /// <inheritdoc />
    public override string DisplayName => "Full auto";

    /// <inheritdoc />
    public override string Description =>
        "Agents may run any command and use any tool without asking, except the guard rails and your deny rules.";

    /// <inheritdoc />
    public override bool AllowedAsDefault => false;

    /// <inheritdoc />
    public override string Warning =>
        "Full auto lets the run's agents run any command on this machine without asking, except the guard rails. " +
        "Use it only in a repo you trust, on a ticket you trust.";

    /// <inheritdoc />
    public override string DenialAdvice =>
        "Under full auto only the guard rails, your deny rules and read-only or planning steps refuse anything. " +
        "If it needs them, take the rule out with 'afl permission guards --remove' or 'afl permission remove' " +
        "(or in Settings → Permissions), or change the step's permissionMode.";

    /// <inheritdoc />
    public override void ApplyTo(ExecutionRequest request, PermissionRuleSet rules)
    {
        base.ApplyTo(request, rules);
        if (request.PermissionMode == StepConstants.DefaultPermissionMode)
        {
            request.PermissionMode = PermissionConstants.BypassPermissionsMode;
        }
    }
}

/// <summary>
///     Finds permission policies by name.
/// </summary>
public class PermissionPolicyRegistry()
    : TypeRegistry<AbstractPermissionPolicy, PermissionPolicyRegistrationAttribute>("permission policy")
{
    /// <summary>
    ///     Gets the shared registry.
    /// </summary>
    public static PermissionPolicyRegistry Instance { get; } = new();

    /// <summary>
    ///     Gets every policy, the default first.
    /// </summary>
    public IReadOnlyList<AbstractPermissionPolicy> All => this.CreateAll()
        .OrderBy(p => p.Name != PermissionConstants.DefaultPolicy)
        .ThenBy(p => p.AllowedAsDefault ? 0 : 1)
        .ToList();

    /// <summary>
    ///     Creates the policy with a name, or the default when the name is unset or unknown (a run saved by a newer afl,
    ///     say).
    /// </summary>
    /// <param name="name">The policy name, or null.</param>
    /// <returns>The policy.</returns>
    public AbstractPermissionPolicy Resolve(string? name)
    {
        return this.Create(name is not null && this.Contains(name) ? name : PermissionConstants.DefaultPolicy);
    }

    /// <summary>
    ///     Checks a policy name a user gave.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="asDefault">Whether it is to be the default for new runs.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="AflException">The name is unknown, or the policy can't be a default.</exception>
    public AbstractPermissionPolicy Require(string name, bool asDefault = false)
    {
        var policy = this.Create(name);
        if (asDefault && !policy.AllowedAsDefault)
        {
            throw new AflException(AflErrorKind.InvalidInput,
                $"'{policy.Name}' can't be the default permission policy; choose it when you start a run. " +
                $"Defaults: {string.Join(", ", this.All.Where(p => p.AllowedAsDefault).Select(p => p.Name))}.");
        }

        return policy;
    }
}
