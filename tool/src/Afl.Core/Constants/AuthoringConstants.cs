namespace Afl.Core.Constants;

/// <summary>
///     Well-known values for the AI authoring helper: the kinds of document it drafts and the files in a session's
///     folder.
/// </summary>
public static class AuthoringConstants
{
    /// <summary>
    ///     The kind of document that is a skill.
    /// </summary>
    public const string KindSkill = "skill";

    /// <summary>
    ///     The kind of document that is a common step.
    /// </summary>
    public const string KindStep = "step";

    /// <summary>
    ///     The kind of document that is a workflow.
    /// </summary>
    public const string KindWorkflow = "workflow";

    /// <summary>
    ///     The file in a session's folder the agent writes the draft to.
    /// </summary>
    public const string DraftFileName = "draft.json";

    /// <summary>
    ///     The folder in a session's folder holding what the agent reads: the schemas, the guide, an example and the
    ///     catalog.
    /// </summary>
    public const string ReferenceFolderName = "reference";

    /// <summary>
    ///     The id of the one step in an authoring run's workflow.
    /// </summary>
    public const string DraftStepId = "draft";

    /// <summary>
    ///     How many times, by default, a draft that doesn't check out is sent back to the agent to fix before the user
    ///     sees it.
    /// </summary>
    public const int DefaultMaxFixRounds = 3;

    /// <summary>
    ///     The prefix of the repo id an authoring run is filed under, before the session's id.
    /// </summary>
    public const string RepoIdPrefix = "authoring-";
}
