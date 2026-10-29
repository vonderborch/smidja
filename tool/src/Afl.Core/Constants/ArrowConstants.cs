namespace Afl.Core.Constants;

/// <summary>
///     Constants for the arrows between steps: when they fire, how many a step follows, and what a step the arrows of
///     several paths point at waits for.
/// </summary>
public static class ArrowConstants
{
    /// <summary>
    ///     Where an arrow goes to end the path it's on (and the run, when it's the last path).
    /// </summary>
    public const string End = "end";

    /// <summary>
    ///     An arrow that fires when its step succeeds; the default.
    /// </summary>
    public const string OnSuccess = "success";

    /// <summary>
    ///     An arrow that fires when its step fails (and handles the failure).
    /// </summary>
    public const string OnFailure = "failure";

    /// <summary>
    ///     An arrow that fires when its step runs over its <c>timeout</c> (and handles the failure).
    /// </summary>
    public const string OnTimeout = "timeout";

    /// <summary>
    ///     An arrow that fires however its step ends, like <c>finally</c>: it doesn't handle a failure.
    /// </summary>
    public const string OnAlways = "always";

    /// <summary>
    ///     A step follows every arrow that applies; the default.
    /// </summary>
    public const string FollowAll = "all";

    /// <summary>
    ///     A step follows only the first conditional arrow that holds, else its unconditional ones.
    /// </summary>
    public const string FollowFirst = "first";

    /// <summary>
    ///     The values <c>follow</c> can have.
    /// </summary>
    public static readonly IReadOnlyList<string> FollowModes = [FollowAll, FollowFirst];

    /// <summary>
    ///     Once a step's <c>waitFor</c> holds, the paths still heading only for it are cancelled; the default.
    /// </summary>
    public const string OthersCancel = "cancel";

    /// <summary>
    ///     Once a step's <c>waitFor</c> holds, the paths still heading for it finish, and are then ignored.
    /// </summary>
    public const string OthersFinish = "finish";

    /// <summary>
    ///     The values <c>others</c> can have.
    /// </summary>
    public static readonly IReadOnlyList<string> OthersModes = [OthersCancel, OthersFinish];

    /// <summary>
    ///     The wait rule that needs every live arrow in; the default.
    /// </summary>
    public const string WaitAll = "all";

    /// <summary>
    ///     The wait rule that needs one arrow in.
    /// </summary>
    public const string WaitAny = "any";

    /// <summary>
    ///     The wait rule that needs a number of arrows in.
    /// </summary>
    public const string WaitAtLeast = "atLeast";

    /// <summary>
    ///     The wait rule for the arrow from one step (a leaf of a group).
    /// </summary>
    public const string WaitArrow = "arrow";

    /// <summary>
    ///     The property listing the items an <see cref="WaitAtLeast" /> rule counts.
    /// </summary>
    public const string WaitOfPropertyName = "of";

    /// <summary>
    ///     The name of a step's <c>waitFor</c> property; the strings in one of its groups are step ids.
    /// </summary>
    public const string WaitForPropertyName = "waitFor";

    /// <summary>
    ///     The property listing a step's arrows.
    /// </summary>
    public const string ArrowsPropertyName = "arrows";

    /// <summary>
    ///     The property naming the step an arrow goes to.
    /// </summary>
    public const string ToPropertyName = "to";

    /// <summary>
    ///     How the key of an arrow's state in <c>Run.Arrows</c> is made; <c>{0}</c> is its step, <c>{1}</c> where it
    ///     goes and <c>{2}</c> its outcome.
    /// </summary>
    public const string ArrowKeyFormat = "{0}>{1}:{2}";
}
