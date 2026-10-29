namespace Afl.Core.Constants;

/// <summary>
///     Well-known values used in workflow step definitions.
/// </summary>
public static class StepConstants
{
    /// <summary>
    ///     A step the AI does headless, without the user.
    /// </summary>
    public const string TypeAi = "ai";

    /// <summary>
    ///     A step the user works on with the AI in an interactive session, then marks done.
    /// </summary>
    public const string TypeInteractive = "interactive";

    /// <summary>
    ///     A step the user does themselves, then marks done.
    /// </summary>
    public const string TypeUser = "user";

    /// <summary>
    ///     The internal step type the AI authoring helper drafts a document with: an <c>ai</c> step whose output is
    ///     checked, and sent back to the AI to fix, before the user reviews it.
    /// </summary>
    public const string TypeAuthorDraft = "author-draft";

    /// <summary>
    ///     Once the step's work is done the run moves straight on to the next step.
    /// </summary>
    public const string AdvanceAuto = "auto";

    /// <summary>
    ///     Once the step's work is done it waits for the user to approve it (answering its inputs) or reject it with
    ///     feedback, whatever kind of step it is.
    /// </summary>
    public const string AdvanceApprove = "approve";

    /// <summary>
    ///     For a step that repeats: the run moves straight on while the loop will go round again, and waits for the user
    ///     to approve (or reject with feedback) only on the pass that ends it.
    /// </summary>
    public const string AdvanceApproveFinal = "approve-final";

    /// <summary>
    ///     A one-line text input.
    /// </summary>
    public const string InputText = "text";

    /// <summary>
    ///     A multi-line text input.
    /// </summary>
    public const string InputMultiline = "multiline";

    /// <summary>
    ///     A number input.
    /// </summary>
    public const string InputNumber = "number";

    /// <summary>
    ///     Pick one of the input's options.
    /// </summary>
    public const string InputChoice = "choice";

    /// <summary>
    ///     Pick any of the input's options.
    /// </summary>
    public const string InputMultiChoice = "multichoice";

    /// <summary>
    ///     A yes/no question.
    /// </summary>
    public const string InputConfirm = "confirm";

    /// <summary>
    ///     The answer to a <see cref="InputConfirm" /> input meaning yes.
    /// </summary>
    public const string Yes = "yes";

    /// <summary>
    ///     The answer to a <see cref="InputConfirm" /> input meaning no.
    /// </summary>
    public const string No = "no";

    /// <summary>
    ///     How the picked options of a <see cref="InputMultiChoice" /> input are joined in its answer.
    /// </summary>
    public const string ListSeparator = ", ";

    /// <summary>
    ///     The Claude Code permission mode used when a step doesn't set one.
    /// </summary>
    public const string DefaultPermissionMode = "acceptEdits";

    /// <summary>
    ///     The permission mode for read-only AI work. Headless, Claude can read but anything that needs approval is denied.
    /// </summary>
    public const string ReadOnlyPermissionMode = "default";

    /// <summary>
    ///     Claude Code's plan mode, which only works interactively: headless, Claude writes a plan file outside the repo
    ///     and then can't leave plan mode.
    /// </summary>
    public const string PlanPermissionMode = "plan";

    /// <summary>
    ///     The heading put between a user step's instructions and the feedback when the step is sent back to be redone.
    /// </summary>
    public const string ReworkHeading = "Redo this step. Changes asked for:";

    /// <summary>
    ///     The heading before the earlier answer, when a step is redone by an agent that can't continue its
    ///     conversation.
    /// </summary>
    public const string PreviousOutputHeading = "Your previous answer was:";

    /// <summary>
    ///     The prompt sent back to the AI when the user rejects its output or replays the step with feedback.
    /// </summary>
    public const string RejectionPromptTemplate =
        "The user reviewed your previous output and asked for changes:\n\n{{feedback}}\n\nRevise your work accordingly and reply with the full, updated result.";

    /// <summary>
    ///     The prompt used to summarise an interactive session once the user marks it done.
    /// </summary>
    public const string InteractiveSummaryPrompt =
        "The interactive session for this step is finished. Reply with a concise summary (a few paragraphs at most) of what was decided and done, suitable as context for the next step. Do not make any further changes.";

    /// <summary>
    ///     The combine strategy that waits for the user to choose one agent's result (or merge them); the default.
    /// </summary>
    public const string CombineChoose = "choose";

    /// <summary>
    ///     The combine strategy that has an agent merge every result into one.
    /// </summary>
    public const string CombineMerge = "merge";

    /// <summary>
    ///     The combine strategy that keeps every result, one after another under each agent's heading.
    /// </summary>
    public const string CombineAll = "all";

    /// <summary>
    ///     The name used for the log of a multi-agent step's merge.
    /// </summary>
    public const string MergeLogName = "merge";

    /// <summary>
    ///     The heading put before each agent's result when results are shown together; <c>{0}</c> is the agent id.
    /// </summary>
    public const string CandidateHeadingFormat = "## {0}";

    /// <summary>
    ///     The instructions put after a multi-agent step's prompt when its results are merged and the step sets no
    ///     <c>mergePrompt</c>.
    /// </summary>
    public const string MergeInstructions =
        "Several agents each did the task above. Their results follow, each under the agent's name. Merge them into one result that keeps the best of each and fixes anything they disagree on, then reply with only the merged result.";

    /// <summary>
    ///     The heading before the extra instructions given when merging results.
    /// </summary>
    public const string MergeNoteHeading = "When merging:";

    /// <summary>
    ///     Why a path run was cancelled when another path got to the step they were both heading for first.
    /// </summary>
    public const string OvertakenNote = "Cancelled: another path got there first.";

    /// <summary>
    ///     A loop that reaches its limit fails the step; the default.
    /// </summary>
    public const string OnLimitFail = "fail";

    /// <summary>
    ///     A loop that reaches its limit moves on as if its condition had been met.
    /// </summary>
    public const string OnLimitContinue = "continue";

    /// <summary>
    ///     How many times a step may run, through loops and routes back to it, when it doesn't say.
    /// </summary>
    public const int DefaultMaxRuns = 5;

    /// <summary>
    ///     How many step runs a run does in all, counting every loop and path, when its workflow doesn't say
    ///     (<see cref="Workflows.WorkflowDefinition.MaxSteps" />).
    /// </summary>
    public const int DefaultMaxSteps = 100;

    /// <summary>
    ///     The error for a run that reached its <c>maxSteps</c>; {0} is the limit, {1} the step it was about to start.
    /// </summary>
    public const string MaxStepsReachedFormat =
        "The run has done {0} step runs, its maxSteps; it stopped before starting step '{1}'. Raise maxSteps if the workflow really needs more.";

    /// <summary>
    ///     The kind of failure of a step whose process stopped part-way (<see cref="Runs.RunStep.FailureKind" />): it
    ///     is retried, not routed.
    /// </summary>
    public const string FailureInterrupted = "interrupted";

    /// <summary>
    ///     The kind of failure of a step that ran over its <c>timeout</c> (<see cref="Runs.RunStep.FailureKind" />).
    /// </summary>
    public const string FailureTimeout = "timeout";

    /// <summary>
    ///     The kind of failure of a step whose session or person said it failed (<c>afl run fail</c>): it is routed
    ///     like any failure, but not retried, since doing it again won't help.
    /// </summary>
    public const string FailureGivenUp = "given-up";

    /// <summary>
    ///     A condition on the answer to a step's input.
    /// </summary>
    public const string ConditionInput = "input";

    /// <summary>
    ///     A condition on a step's output text.
    /// </summary>
    public const string ConditionOutput = "output";

    /// <summary>
    ///     A condition an agent judges from a step's output.
    /// </summary>
    public const string ConditionAi = "ai";

    /// <summary>
    ///     A condition that holds when all its nested conditions do.
    /// </summary>
    public const string ConditionAll = "all";

    /// <summary>
    ///     A condition that holds when any of its nested conditions does.
    /// </summary>
    public const string ConditionAny = "any";

    /// <summary>
    ///     A condition that holds when its one nested condition doesn't.
    /// </summary>
    public const string ConditionNot = "not";

    /// <summary>
    ///     A condition that always holds.
    /// </summary>
    public const string ConditionAlways = "always";

    /// <summary>
    ///     The name used for the log of an <see cref="ConditionAi" /> condition's judgement.
    /// </summary>
    public const string ConditionLogName = "condition";

    /// <summary>
    ///     The prompt an agent judging an <see cref="ConditionAi" /> condition gets; <c>{0}</c> is the question and
    ///     <c>{1}</c> the step's output.
    /// </summary>
    public const string ConditionPromptFormat =
        "Answer a yes/no question about the result of a workflow step. Don't change anything.\n\nQuestion: {0}\n\nThe step's result:\n\n{1}\n\nReply with YES or NO as the first word, then one sentence saying why.";

    /// <summary>
    ///     The reason recorded on a step's attempts that a loop repeats; <c>{0}</c> is the iteration that follows.
    /// </summary>
    public const string LoopReasonFormat = "Loop iteration {0}";

    /// <summary>
    ///     The reason recorded on a step's attempt when an arrow reaches it again after it has already run (another way
    ///     in, not a loop); <c>{0}</c> is the step whose arrow reached it.
    /// </summary>
    public const string ArrivedReasonFormat = "Reached again from '{0}'";

    /// <summary>
    ///     An input asked before the step's work starts; its answer can be used in the step's own prompt as
    ///     <c>{{inputs.&lt;id&gt;}}</c>.
    /// </summary>
    public const string WhenBefore = "before";

    /// <summary>
    ///     An input asked when the step is let through (approved, or marked done); the default.
    /// </summary>
    public const string WhenAfter = "after";

    /// <summary>
    ///     An input the AI is told about and asks only if it decides it needs the answer.
    /// </summary>
    public const string WhenAi = "ai";

    /// <summary>
    ///     How many times the AI may stop to ask questions in one attempt at a step.
    /// </summary>
    public const int MaxAskRounds = 3;

    /// <summary>
    ///     The fence language of the block the AI ends its reply with to ask questions.
    /// </summary>
    public const string AskBlockTag = "afl-ask";

    /// <summary>
    ///     The fence language of the block the AI ends its reply with to hand sub-tasks to other agents.
    /// </summary>
    public const string DelegateBlockTag = "afl-delegate";

    /// <summary>
    ///     The extension of the MCP client configuration file written beside a step's log for its agent.
    /// </summary>
    public const string McpConfigExtension = ".mcp.json";

    /// <summary>
    ///     In terminal mode, the file the wrapper script copies the command's standard output to.
    /// </summary>
    public const string TerminalOutputExtension = ".out";

    /// <summary>
    ///     In terminal mode, the file the command's standard error goes to.
    /// </summary>
    public const string TerminalErrorExtension = ".err";

    /// <summary>
    ///     In terminal mode, the file holding the command's standard input.
    /// </summary>
    public const string TerminalInputExtension = ".in";

    /// <summary>
    ///     In terminal mode, the file the wrapper script writes the process id to, so afl can stop it.
    /// </summary>
    public const string TerminalProcessExtension = ".pid";

    /// <summary>
    ///     In terminal mode, the file the wrapper script writes the exit code to once the command has finished.
    /// </summary>
    public const string TerminalExitExtension = ".exit";

    /// <summary>
    ///     How many times the AI may hand off work in one attempt at a step.
    /// </summary>
    public const int MaxDelegationRounds = 3;

    /// <summary>
    ///     The name used for the log of a delegated task; <c>{0}</c> counts the step's tasks from 1.
    /// </summary>
    public const string DelegateLogNameFormat = "delegate-{0}";

    /// <summary>
    ///     The instructions added to a step's prompt when the AI may hand off work; <c>{0}</c> is one line per agent it
    ///     may use.
    /// </summary>
    public const string DelegateInstructionsFormat =
        "You may hand self-contained sub-tasks to other agents that work in the same repository. To do so, stop and end your reply with a fenced block, for example:\n\n```afl-delegate\n{{\"tasks\": [{{\"agent\": \"<agent id, optional>\", \"capabilities\": [\"<capability>\"], \"prompt\": \"<everything the agent needs to know>\"}}]}}\n```\n\nThe tasks run at the same time and you'll get their results to carry on with. Name an agent, or give the capabilities the task needs and afl picks one. Each agent starts with no context, so make every prompt complete. Don't use the block once you're done.\n\nAgents you may use:\n{0}";

    /// <summary>
    ///     Heads the results of delegated tasks in the prompt that lets the AI carry on.
    /// </summary>
    public const string DelegationResultsHeading = "The agents you handed work to replied:";

    /// <summary>
    ///     One delegated task's result; <c>{0}</c> is its number, <c>{1}</c> the agent, <c>{2}</c> how it ended and
    ///     <c>{3}</c> its output or error.
    /// </summary>
    public const string DelegationResultFormat = "### Task {0}: {1} ({2})\n\n{3}";

    /// <summary>
    ///     Ends the prompt that lets the AI carry on after delegated work.
    /// </summary>
    public const string DelegationContinue = "Carry on with the step using these results and reply with the full result.";

    /// <summary>
    ///     The fence language of the block the AI ends its reply with to file tickets.
    /// </summary>
    public const string TicketBlockTag = "afl-ticket";

    /// <summary>
    ///     How many tickets the AI may file in one attempt at a step.
    /// </summary>
    public const int MaxTicketsPerStep = 10;

    /// <summary>
    ///     The instructions added to a step's prompt when the AI may file tickets; <c>{0}</c> is where they go and
    ///     <c>{1}</c> how many more it may file.
    /// </summary>
    public const string TicketInstructionsFormat =
        "You may file follow-up tickets (in {0}) for work that is real but outside this step, such as hardening, tech debt or bugs you found. To do so, stop and end your reply with a fenced block, for example:\n\n```afl-ticket\n{{\"tickets\": [{{\"title\": \"<short title>\", \"body\": \"<what and why, in Markdown>\", \"labels\": [\"<label>\"], \"type\": \"bug|feature|task\"}}]}}\n```\n\nYou'll get the new tickets' keys to carry on with; mention them in your result. File at most {1} more, don't file duplicates, and don't use the block once you're done.";

    /// <summary>
    ///     Heads the outcome of filing tickets in the prompt that lets the AI carry on.
    /// </summary>
    public const string TicketResultsHeading = "The tickets you asked for:";

    /// <summary>
    ///     Ends the prompt that lets the AI carry on after filing tickets.
    /// </summary>
    public const string TicketContinue = "Carry on with the step and reply with the full result, listing the tickets filed.";

    /// <summary>
    ///     The note left when a step's optional MCP server isn't given to its agent; <c>{0}</c> is the server and
    ///     <c>{1}</c> the agent.
    /// </summary>
    public const string OptionalMcpServerSkippedFormat =
        "Optional MCP server '{0}' wasn't given to {1}: it isn't set up here (afl mcp-list) or the agent can't take servers.";

    /// <summary>
    ///     The note when one of a step's extra folders isn't one: {0} is the folder as written.
    /// </summary>
    public const string DirectorySkippedFormat = "Skipped the folder '{0}': it doesn't exist.";

    /// <summary>
    ///     The note on an optional step that was left out when the run started.
    /// </summary>
    public const string LeftOutNote = "Skipped: this optional step was left out when the run started.";

    /// <summary>
    ///     The fence language of the block the main agent answers with when it chooses a step's agent.
    /// </summary>
    public const string AgentBlockTag = "afl-agent";

    /// <summary>
    ///     The name used for the log of the main agent choosing a step's agent.
    /// </summary>
    public const string ChooseAgentLogName = "choose-agent";

    /// <summary>
    ///     The prompt the run's main agent gets to choose which agent does a step; <c>{0}</c> is the step's name,
    ///     <c>{1}</c> its rendered prompt and <c>{2}</c> one line per agent that qualifies.
    /// </summary>
    public const string ChooseAgentPromptFormat =
        "You are coordinating a workflow. Choose which agent should do the next step. Don't do the step and don't change anything.\n\nStep: {0}\n\nThe step's prompt:\n\n{1}\n\nAgents that can do it:\n{2}\n\nReply with a fenced block naming one of them and saying why in one sentence, for example:\n\n```afl-agent\n{{\"agent\": \"<agent id>\", \"reason\": \"<why>\"}}\n```";

    /// <summary>
    ///     One agent in <see cref="ChooseAgentPromptFormat" />; <c>{0}</c> is its id, <c>{1}</c> its description,
    ///     <c>{2}</c> its capabilities and <c>{3}</c> its model.
    /// </summary>
    public const string ChooseAgentCandidateFormat = "- {0}: {1} (capabilities: {2}; model: {3})";

    /// <summary>
    ///     The heading the AI puts its open questions under, at the end of a step's output, so the user can answer
    ///     them when the step waits for approval (<see cref="Afl.Core.Runs.OpenQuestion" />).
    /// </summary>
    public const string OpenQuestionsHeading = "## Open questions";

    /// <summary>
    ///     The instructions added to the prompt of an AI step that waits for approval, so the questions it leaves for
    ///     the user are in the shape <see cref="Afl.Core.Runs.OpenQuestion" /> reads; <c>{0}</c> is
    ///     <see cref="OpenQuestionsHeading" />.
    /// </summary>
    public const string OpenQuestionsInstructionsFormat =
        "The user reviews your reply before the run moves on. If you have questions for them that you've worked around with an assumption, end your reply with a section headed exactly `{0}`, even when you're asked to reply with a document and nothing else: a numbered list, one question per item, the question in bold followed by the assumption you made, for example:\n\n{0}\n1. **Should the old API keep working?** Assumed yes, so it's kept behind a flag.\n\nMake it the last section: only a closing line you're asked to end with, and the fenced blocks described above, may follow it. Leave the section out when you have no questions, and don't list questions you can't work around: ask those with the `afl-ask` block, if you've been told how. The user's answers come back as feedback and the step is redone with them.";

    /// <summary>
    ///     The id given to the AI's own questions; <c>{0}</c> counts from 1 within the step.
    /// </summary>
    public const string AskQuestionIdFormat = "ask-{0}";

    /// <summary>
    ///     The instructions added to a step's prompt when the AI may ask questions; <c>{0}</c> lists the step's inputs
    ///     it may ask (or says it has none), <c>{1}</c> says whether it may ask its own questions.
    /// </summary>
    public const string AskInstructionsFormat =
        "If you can't finish this step well without an answer from the user, stop and end your reply with a fenced block, for example:\n\n```afl-ask\n{{\"inputs\": [\"<input id>\"], \"questions\": [{{\"label\": \"<question>\", \"type\": \"text\"}}]}}\n```\n\nPut what you've done so far before the block; you'll get the answers and can carry on. Ask only what you can't reasonably decide yourself, and don't use the block once you're done.\n\n{0}\n\n{1}";

    /// <summary>
    ///     Lists the step inputs the AI may ask, in <see cref="AskInstructionsFormat" />; <c>{0}</c> is one line per input.
    /// </summary>
    public const string AskInputsFormat = "Inputs you may ask for (by id in \"inputs\"):\n{0}";

    /// <summary>
    ///     Says the step has no inputs the AI may ask, in <see cref="AskInstructionsFormat" />.
    /// </summary>
    public const string AskNoInputs = "This step has no predefined inputs; leave \"inputs\" out.";

    /// <summary>
    ///     Allows the AI its own questions, in <see cref="AskInstructionsFormat" />.
    /// </summary>
    public const string AskOwnQuestions =
        "You may ask your own questions in \"questions\": each has a \"label\", a \"type\" (text, multiline, number, choice, multichoice or confirm) and, for choice and multichoice, \"options\".";

    /// <summary>
    ///     Forbids the AI its own questions, in <see cref="AskInstructionsFormat" />.
    /// </summary>
    public const string AskNoOwnQuestions = "Don't ask your own questions; leave \"questions\" out.";

    /// <summary>
    ///     The heading before the user's answers when the AI's questions are answered.
    /// </summary>
    public const string AnswersHeading = "The user answered your questions:";

    /// <summary>
    ///     What the AI is told to do once its questions are answered.
    /// </summary>
    public const string AnswersContinue = "Carry on with the step and reply with the full result.";

    /// <summary>
    ///     The type of a step that runs another workflow as one or more child runs.
    /// </summary>
    public const string TypeWorkflow = "workflow";

    /// <summary>
    ///     A <c>workflow</c> step waits for every child run to finish (<c>children: wait</c>); the default.
    /// </summary>
    public const string ChildrenWait = "wait";

    /// <summary>
    ///     A <c>workflow</c> step finishes once its child runs have started (<c>children: start</c>).
    /// </summary>
    public const string ChildrenStart = "start";

    /// <summary>
    ///     The child source used when a <c>workflow</c> step doesn't say: one child on the run's own ticket.
    /// </summary>
    public const string ChildSourceTicket = "ticket";

    /// <summary>
    ///     The child source giving one child per ticket in a list.
    /// </summary>
    public const string ChildSourceTickets = "tickets";

    /// <summary>
    ///     The child source giving one child per ticket an earlier step filed.
    /// </summary>
    public const string ChildSourceCreatedTickets = "created-tickets";

    /// <summary>
    ///     The child source giving one child per non-empty line of an earlier step's output.
    /// </summary>
    public const string ChildSourceLines = "lines";

    /// <summary>
    ///     The child source giving one child per item in a comma- or newline-separated list.
    /// </summary>
    public const string ChildSourceItems = "items";

    /// <summary>
    ///     The workspace in which child runs share their parent's checkout (the default).
    /// </summary>
    public const string WorkspaceShared = "shared";

    /// <summary>
    ///     The workspace giving each child run its own git worktree and branch, merged back once it is done.
    /// </summary>
    public const string WorkspaceWorktree = "worktree";

    /// <summary>
    ///     The most child runs one <c>workflow</c> step may start.
    /// </summary>
    public const int MaxChildRuns = 20;

    /// <summary>
    ///     How deep child runs may nest: a run started by a run started by a run, and so on.
    /// </summary>
    public const int MaxRunDepth = 4;

    /// <summary>
    ///     How deep common steps, includes, child workflows and requirements may refer to one another.
    /// </summary>
    public const int MaxCompositionDepth = 5;

    /// <summary>
    ///     The step properties (at any depth, e.g. in conditions and loops) whose value is the id of another step in the
    ///     same workflow, which an include has to prefix.
    /// </summary>
    public static readonly IReadOnlyList<string> StepReferenceProperties =
        [StepPropertyName, ArrowConstants.ToPropertyName, FromPropertyName, StartPropertyName];

    /// <summary>
    ///     The property naming the step a condition or child source reads.
    /// </summary>
    public const string StepPropertyName = "step";

    /// <summary>
    ///     The name of a step's <c>type</c> property, which picks its subclass.
    /// </summary>
    public const string TypePropertyName = "type";

    /// <summary>
    ///     The property naming the step a workflow starts at.
    /// </summary>
    public const string StartPropertyName = "start";

    /// <summary>
    ///     The property naming the step a loop goes back to.
    /// </summary>
    public const string FromPropertyName = "from";

    /// <summary>
    ///     The template variable holding the item a child run was started for (a ticket key or a line), in a
    ///     <c>workflow</c> step's <c>with</c>.
    /// </summary>
    public const string ItemVariable = "item";

    /// <summary>
    ///     The template prefix of a parameter, <c>{{params.&lt;id&gt;}}</c>.
    /// </summary>
    public const string ParameterPrefix = "params.";

    /// <summary>
    ///     The id prefix of the leading step a requirement (<c>requires</c>) becomes.
    /// </summary>
    public const string RequirementStepPrefix = "requires-";

    /// <summary>
    ///     The heading over each child's output when a <c>workflow</c> step has several children.
    /// </summary>
    public const string ChildOutputHeadingFormat = "## {0} ({1})";

    /// <summary>
    ///     A note on a <c>workflow</c> step that reused an existing run.
    /// </summary>
    public const string ReusedRunNoteFormat = "Reused run {0} of '{1}' on {2}.";
}
