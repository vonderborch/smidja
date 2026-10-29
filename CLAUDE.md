# smidja

A Claude Code plugin (`plugin/`) plus the afl workflow tool (`tool/`, C# / .NET 10 / Avalonia 12.1).
See README.md for the layout.

- Build/test the tool: `cd tool && dotnet build Afl.slnx && dotnet test Afl.slnx`
- Validate the plugin after editing anything under `plugin/` or `.claude-plugin/`: `claude plugin validate .`
- All afl logic belongs in `Afl.Core`. `Afl.Cli` and `Afl.UI` stay thin front-ends over its stores and `RunEngine`.
- Code style (the owner's preference, see github.com/vonderborch): extend through `Abstract*` base classes and
  registration attributes discovered by `TypeRegistry`, not `I*` interfaces or switch statements; no tiny
  single-purpose classes; `this.` prefix, file-scoped namespaces, braces always, XML docs on every member.
- Moving on is per step, not per type: `AbstractStepDefinition.Advance` (`auto`/`approve`, falling back to the handler's
  `DefaultAdvance`). A handler ends its work with `Finish`, which completes the step or waits for approval; approve,
  send back (`ReworkAsync`) and replay (`RunEngine.ReplayAsync`, which archives attempts into `RunStep.History`
  with `RunStep.Archive`) work the same for every type, so a new step type needn't handle gating itself.
- Step inputs: a new input type is one `AbstractStepInputType` with `[StepInputTypeRegistration]` and a
  `StepInputEditor`; the CLI prompt and the Runs page form pick their control from `Editor`, so neither needs
  changing. Each input has a `when`: `before` (any step; `AbstractStepHandler.BeginAsync` stops at
  `StepStatus.AwaitingInput` until they're answered), `after` (when `TakesInputs(step)`: a person finishes it, or
  it's gated) or `ai` (only where `CanAiAsk`: the AI ends its reply with an `afl-ask` block, read by `AskRequest`,
  and `AbstractAiStepHandler` pauses at `AwaitingInput`, recording an `AskRound`; `AbstractAgentStepDefinition.Ask` lets it ask
  its own questions, ids `ask-N`). Front-ends ask exactly `InputsAskedNow(run, state)` and answer through
  `RunEngine.AnswerAsync` (or approve/complete); answers go through `StepInputTypeRegistry.Resolve` into
  `RunStep.Answers`, and a step's prompt sees its own `before` answers as `{{inputs.<id>}}`. Input defaults may be
  templates: `InputsAskedNow` and `AcceptAnswers` render them (`StepInput.WithDefaultRendered`). The AI's ask rounds
  are kept as `AskRound`s (`RunStep.AskRounds`, moved into the `StepAttempt` on archive), and each attempt records its
  `AttemptCause` (`replay`/`loop`/`route`; "replayed N×" counts only replays).
- New step type / AI back-end / ticket system = one subclass of `AbstractStepHandler` / `AbstractStepExecutor` /
  `AbstractTicketSource<TFilter>` with its registration attribute. New CLI command = one `[Verb]` subclass of
  `AbstractCommand`. New UI page = one `AbstractAflPage<TDataContext>` with `[PageRegistration]`.
- AI agents (`Afl.Core/Agents/`): an agent is data (`AgentDefinition` in `~/.afl/agents.json`, never in a repo)
  whose `Kind` picks an `AbstractStepExecutor` (`claude`, `session`, `cli`, `mcp`); a new kind is one executor with
  `[ExecutorRegistration]` that reads its settings from `this.Agent` and declares `SupportsPermissions`,
  `SupportsResume` and `CanRunInteractive`. Presets are data in `agents/presets.json` (embedded). A step picks one
  with `AbstractStepDefinition.Agent`, else the run's (`Run.Executor`); `StepContext.Executor` resolves it per step and
  `RunEngine.Create` rejects agents a step can't use (`AbstractStepHandler.CheckAgent`). Tests inject executors
  with `RunEngine(store, executorOverride)` or the `Func<string, AbstractStepExecutor>` constructor, and MCP agents
  through `McpExecutor.ConnectAsync`. Each executor declares the agent fields it reads (`Fields`, `AgentField` with an
  editor name), which the Settings agent form renders, so a new kind needs no XAML.
- Capabilities and choosing agents: `CapabilityCatalog` merges the built-ins (`agents/capabilities.json`, embedded)
  with `AflSettings.Capabilities`; `AgentStore.CapabilitiesOf` adds the ones an executor implies, and `IsAvailable`
  says whether the agent is installed. A step with `Capabilities` treats `Agent` as a preference and otherwise picks
  among qualifying agents with an `AbstractAgentSelector` (`[AgentSelectorRegistration]`: `first`, `ai`). `ai` asks
  the run's main agent (`afl-agent` block); with `session` it stops at `StepStatus.ChoosingAgent` until
  `RunEngine` gets `use-agent`. The choice is `RunStep.AgentChoice` (`By`: selector/main-agent/user/preference,
  `AgentConstants.ChosenByMainAgent`).
- Reply blocks: fenced blocks at the end of an AI reply are `AbstractReplyBlock`s (`[ReplyBlockRegistration]`:
  `afl-ask`, `afl-delegate`, `afl-ticket`) that add their instructions to the prompt and, in `TryHandleAsync`, either take
  over the step (pause it or carry on) or let it finish; `AbstractReplyBlock<TRequest>` parses and checks the JSON. `afl-delegate` runs sub-tasks on other agents (`RunStep.Delegations`,
  `<step>.delegate-N.log`, at most `StepConstants.MaxDelegationRounds`) and resumes the agent with the results.
  A new block is one subclass; `AbstractAiStepHandler` loops over the registry.
  `afl-ticket` (`TicketReplyBlock`, on steps with `CreateTickets`) files tickets through
  `TicketSourceRegistry.CreatorFor` (the run's source, else the repo's linked Jira project, else its GitHub or GitLab
  project), each source implementing `AbstractTicketSource.CanCreateFor`/`CreateAsync`; results land in
  `RunStep.CreatedTickets`, at most `StepConstants.MaxTicketsPerStep`. GitHub and GitLab file through the repo's git
  host connection (see Git hosts).
- Step documents and skips (format 3.2): `AbstractStepDefinition.Artifact` is a repo-relative template path the output is
  saved to once the step is done (`StepContext.SaveArtifact`; `WorkflowLibrary.ResolveArtifactPath` keeps it inside
  the repo and out of `.git`, and a failure is a note, not an error), recorded as `RunStep.ArtifactPath` and read as
  `{{steps.<id>.artifact}}`; `{{ticket.slug}}` is `TemplateVariables.Slug`. There is no `skipWhen`: a
  conditional step is a condition on its arrows in plus a bypass arrow (see Flow). `OptionalMcpServers` are
  given only when set up and the agent `SupportsMcpServers` (`StepContext.OptionalMcpServersFor`), otherwise noted;
  they never block a run or narrow agent choice.
- MCP servers (`Afl.Core/Mcp/`): `McpServerStore` (`~/.afl/mcp-servers.json`) holds servers that mcp agents point
  at (`AgentDefinition.Server`) and that steps or agents attach as tools (`McpServers`, for executors with
  `SupportsMcpServers`). Secrets go only through its secure store under `mcp:<server>:<NAME>`
  (`AFL_MCP_SECRET_<SERVER>_<NAME>` override), referenced as `{{secret.NAME}}` and resolved only in memory; never
  write them to JSON or logs, never print them, and never ask for one in chat. Client config files with resolved
  secrets (`WriteClientConfig`) are mode 0600 and deleted after use. Importing from another app is one
  `AbstractMcpConfigSource` with `[McpConfigSourceRegistration]`; import moves secret-looking values to the store.
- Run modes: `AbstractProcessExecutor<TTurn>` runs its process through an `AbstractRunMode`
  (`[RunModeRegistration]`: `background`, `terminal`, the latter through `AbstractTerminalLauncher` with `.out`,
  `.exit` and `.cancel` files). `Run.Mode` comes from `--mode` or `AflSettings.DefaultRunMode`.
- Permissions (`Afl.Core/Permissions/`): how a run's agents get permission is `Run.PermissionPolicy`, an
  `AbstractPermissionPolicy` (`[PermissionPolicyRegistration]`, `PermissionPolicyRegistry`: `ask`, `strict`, `auto`;
  `AllowedAsDefault` is false for `auto`, which needs `--yes-auto` from the CLI) that adjusts each
  `ExecutionRequest` (`ApplyTo`) and says whether a refusal pauses the step (`PausesOnDenial`). `PermissionRuleStore.For` merges allow
  and deny rules (`PermissionRule`, Claude Code rule strings) from the run, the repo (`RepoRecord.PermissionRules`),
  the global `PermissionRuleStore` (`~/.afl/permissions.json`) and the guard rails (`AflSettings.GuardRules`,
  defaults in `PermissionConstants.DefaultGuardRules`, under every policy) into a `PermissionRuleSet` for
  `AllowedTools` and `DisallowedTools` (`--disallowedTools`, which Claude applies even under `bypassPermissions`). `ClaudeCliExecutor`
  keeps each `permission_denials` entry as a `PermissionDenial` with suggested rules from an
  `AbstractToolRuleSuggester` (`[ToolRuleSuggesterRegistration("Bash")]`, a fallback for the rest), which also sets
  `PermissionDenial.BlockedBy` when one of the request's deny rules matches the call (`IsBlockedBy`: Bash prefixes,
  path globs the way Claude reads them, `Read` rules covering `Glob`/`Grep`). Blocked calls, and calls refused again
  after being allowed, are only noted, never asked about, since an allow can't beat a deny. Under `ask`, a single-agent
  step pauses at `StepStatus.AwaitingPermission` with `RunStep.PermissionRequests`; `RunEngine.PermitAsync` takes
  once/always/deny (always saves the rule to its scope) or accept-output and resumes the session, at most
  `StepConstants.MaxPermissionRounds` rounds. Multi-agent steps and children get the rules but keep the note. CLI:
  `afl run permit`, `afl permission list|add|remove|guards`.
- Global settings: `SettingsStore` (`~/.afl/settings.json`, `AflSettings`): the default main agent, run mode and
  selector, the agent preference order, custom capabilities and `NotifyOn`. `Save` copies each field, so a new
  setting needs a line there.
- Notifications (`Afl.Core/Notifications/`): `RunEngine.WorkAsync` snapshots a `RunAttention` (state, step, step
  status, rounds) inside the lock and, after it, hands it to `RunEngine.Notifier` (a `RunNotifier`; null in tests,
  set by the CLI's `AbstractDrivingCommand` and the UI's `App.CreateShell`). `RunNotifier.For` notifies when the
  attention changed into a state in `AflSettings.NotifyOn` (default needs-you, done, failed); Done/Failed only for
  runs with no parent. It shows them through an `AbstractNotifier` (`[NotifierRegistration]`, `NotifierRegistry
  .ForThisMachine` picks the available one with the highest `Priority`): `terminal-notifier`, `osascript`,
  `notify-send`, `windows-toast` (PowerShell with an encoded script, text in env vars). The text never goes through a
  shell. A send that fails or takes over `RunNotifier.SendTimeout` is a status event, never a failed run. CLI:
  `afl settings-set notify-on`, `afl notify-test`; UI: Settings → Execution → Notifications.
  The UI's `WindowRunNotifier` (`Afl.UI/Attention/`) overrides `RunNotifier.Alert` (called for every notification
  due, even with no notifier) to draw the eye to the main window while it isn't active, through an
  `AbstractWindowAttention` (`[WindowAttentionRegistration]`, one per OS): `windows-flash` (`FlashWindowEx`),
  `mac-dock` (`NSApp requestUserAttention:`), `x11-urgency` (the WM urgency hint, cleared when the window is
  activated). Needs-you is urgent (flash or bounce until the user comes back); done/failed draw the eye briefly.
  Tests use a recording attention; `TestApp` has no notifier, so nothing real flashes.
- Multi-agent steps: `AbstractAgentStepDefinition.Agents` fan out in `AbstractAiStepHandler`, each agent's result landing in
  `RunStep.Candidates` (`StepCandidate`, its own session and `<step>.<agent>.log`). Once all have finished, the
  step's `AbstractCombineStrategy` (`[CombineStrategyRegistration]`, picked by `Combine`) turns them into the output
  or leaves the step at `StepStatus.AwaitingChoice` for `RunEngine.ChooseAsync`/`MergeAsync`; a new strategy is one
  subclass and needs no front-end change.
- Step definitions (format 5.0, `Workflows/StepDefinitions.cs`): `AbstractStepDefinition` holds the shared fields
  (id, name, optional, arrows, follow, waitFor, others, retry, timeout, position, skills, artifact, use/include/with);
  each type has its own subclass (`AiStepDefinition`/`InteractiveStepDefinition` over `AbstractAgentStepDefinition`,
  `UserStepDefinition`, `WorkflowStepDefinition`), named by its handler's `DefinitionType`, and
  `StepDefinitionConverter` reads `type` (default `ai`) to pick it through `StepHandlerRegistry`. Handlers are
  `AbstractStepHandler<TDefinition>`, and validation that only fits one type lives in its `Validate` override. There
  are no upgraders: afl is in development, and files in an older format simply fail to load.
- Flow (format 5.0, `Afl.Core/Flow/`, `Afl.Core/StateMachines/`): a workflow is a free graph of steps joined by
  arrows. `WorkflowDefinition.Start` (default: the first listed step) begins it; list order only affects display, and
  `WorkflowDefinition.Chain()` links code-built workflows in order. Each step's `Arrows` (`ArrowDefinition`: `to` (a
  step or `end`), `on` (an `AbstractOutcome` in `OutcomeRegistry`: `success`, `failure`, `timeout`, `always`),
  `when`/`until`, `max`/`onLimit` on arrows that go back, `label`) say where it goes; `follow` (`all`, `first`) says
  how many forward arrows it takes. A step with two or more forward arrows in waits by its `waitFor`, an
  `AbstractWaitRule` in `WaitRuleRegistry` (`all`, `any`, `atLeast`, groups to any depth, `ArrowWaitRule` leaves;
  `WaitRuleConverter` reads the shorthand and group forms), and `others` (`cancel`, `finish`) says what happens to
  the paths still heading there. Arrows are fired, dead or pending (`ArrowState`, keyed by `FlowMachine.KeyOf`): a
  rule evaluates over live arrows only, a step whose rule is dead is not reached and kills its own arrows (dead-path
  elimination), and once nothing runs every pending arrow dies. A conditional step is `when: not X` on its arrows in
  plus a bypass arrow `when: X` (the editor's "Make this step conditional…"). Arrows back skip `waitFor`, are
  checked first and win; `on: failure` handles a failure, `always` fires without handling it, and
  `WorkflowDefinition.OnFailure` names a catch step. `retry` re-runs a failed step before its failure arrows;
  `timeout` fails it as `timeout`. The core is statecharts over the generic
  `AbstractStateMachine<TState, TTrigger, TContext, TTransition>` (ordered transitions with async guards, plus graph
  analysis: back edges, ancestors, cycles, post-dominators). `FlowMachine.Of(workflow)` (cached per expanded
  definition) answers `IsBack`, `IncomingForward`, `WaitRuleOf`/`WaitOf`, `OnlyLeadsTo` (which paths `others:
  cancel` cancels), `LimitOf` ("iteration n/max"), which steps are `Guaranteed`/`Possible` before a step
  (references to merely possible ones need `{{steps.x.output?}}`) and `Validate`/`Warnings`. `StepLifecycle` is the
  `StepStatus` statechart: actions `Require(trigger)` and handlers change status only through `Move`. `FlowRouter`
  turns a finished step into fired and dead arrows (`RouteAsync`) and works out what they make ready; going back
  archives only that cycle (`RunStep.Archive` with a reason) and bumps `RunStep.Runs`. Steps never reached stay
  `Pending` (front-ends show "not reached" via `Run.NotReached`), so `Skipped` means left out. `Run.CurrentStepId`
  (null = done) is the step the run runs itself; `Run.Path` records each visit (`StepVisit.Describe`).
  `WorkflowDefinition.MaxSteps` (default `StepConstants.DefaultMaxSteps`) caps a run's step runs. Templates also get
  `{{loop.iteration}}`/`{{loop.max}}`, `{{steps.x.previous}}` and `{{arrived.from}}`/`{{arrived.fromName}}`. A new
  condition kind is one `AbstractCondition` with `[ConditionRegistration]` that declares the fields it uses
  (`Fields`, which the builder's condition editor and `ToDefinition` follow); a new wait rule or outcome is one
  subclass with its registration.
- Paths (`Runs/FlowCoordinator.cs`, the only router): when a step's arrows make two or more steps ready, the step
  that split the run waits at `StepStatus.AwaitingPaths` and each ready step runs as its own path (`Run.Paths`,
  `ActivePath`: step, run id, origin, lane, `ArrivedFrom`) in a hidden path run (`RunKind.Path`, made by
  `RunEngine.CreateBranch`: a deep copy of the parent with `StopAfterStep`, not counted against `MaxRunDepth`, hidden
  from `afl runs`) that does that one step, tracked in `RunStep.Paths` (`ChildRun`). As each finishes, its step is
  merged back and routed by the parent, which records `Run.Arrows`, starts what's ready, cancels overtaken paths
  (`StepConstants.OvertakenNote`) and, once one path is left, runs it itself. A splitting step with `workspace:
  worktree` gives each path a lane (its own worktree) merged back once the lane empties. Lanes nest (`ActivePath.OuterLane`, `ChildRun.OuterLane`; `FlowCoordinator.LanesOf` walks them out), a lane stays open while any path in it or in a lane inside it is active, and a join of one lane's paths stays in that lane (`LaneOfJoin`). `FlowCoordinator` and
  `WorkflowChildCoordinator` (the `workflow` step's child runs) share `AbstractChildRunCoordinator` (create, drive up
  to `Run.MaxParallel` at once, refresh, retry, cancel, settle workspaces). `FlowSimulator` routes a workflow with the
  same `FlowRouter` code without running anything (given each step's outcome and whether each conditional arrow is
  taken), for the editor's Simulate. Acting on a step goes through `RunEngine.ResolveTarget(runId, stepId?)`, which
  uses `RunStore.WaitingSteps(run)` to follow paths down to the path run holding the step and fails, listing them,
  when several wait without a step (the CLI's `--step`, `AbstractStepRunCommand`). Replay archives the step and what
  ran after it; cancel cancels the paths.
- Composition (format 3.3, `Afl.Core/Workflows/`): `WorkflowLibrary` and `CommonStepLibrary` (`workflows/steps/`,
  `~/.afl/steps`, `.afl/steps`) are `AbstractLayeredLibrary<TDocument, TListing>`s (built-in, then global, then repo)
  over `AbstractLibraryDocument`s (id, name, description, `tags`; `NormalizeTags`/`ValidateTags`, and
  `NameText`/`ContentText`, which by default is every string in the saved JSON). `LibraryQuery` (text, tags, scope,
  contents or not) filters them, through `Search` on a library or `Apply` on any listings, with name matches first and a
  content snippet otherwise. `AbstractLibraryListCommand` gives every list verb `--tag`/`--search`/`--names-only`/`--scope`;
  `WorkflowLibrary.Steps` is the common steps for the same repo. `WorkflowComposer` resolves `use` (common step with
  `{{params.x}}` filled from `with`, overlaid at the JSON level; `type` always from the common step), `include`
  (steps spliced in with ids `<include>-<step>`, references rewritten in templates and in the properties named by
  `StepConstants.StepReferenceProperties`; arrows into an include go to its start and its ending steps' arrows out of it take
  the include step's own arrows) and `requires` (a leading `workflow` step with `reuse`). `RunEngine.Create`
  expands the workflow once and stores the result in `Run.Workflow`, so nothing that runs steps knows about
  composition; validation everywhere (save, `afl workflow validate`, the builder) goes through
  `WorkflowComposer.Validate`, which checks parameters, cycles and `MaxCompositionDepth` before the static
  `WorkflowLibrary.Validate` on the expanded workflow. If you add a step property holding a step id, add it to
  `StepReferenceProperties`.
- Child runs: the `workflow` step type (`WorkflowStepHandler`) gets its children from an `AbstractChildSource`
  (`[ChildSourceRegistration]`: `ticket`, `tickets`, `created-tickets`, `lines` (with `pattern`/`separator`),
  `items`; each declares `Fields` for the builder, and `KnownItems` returns the items when they are fixed without
  running, so `WorkflowComposer` validates the child once per item, e.g. `use: "review-{{item}}"`; the `code-review` common
  step relies on this to check each named reviewer), creates them through `StepContext.Engine` (recording `Run.ParentRunId`/`ParentStepId`/`Depth`, capped
  by `MaxChildRuns` and `MaxRunDepth`) and waits at `StepStatus.AwaitingChildren`, tracking `RunStep.ChildRuns`. The
  engine stays generic: it calls `AbstractStepHandler.RefreshAsync` for a waiting step, and after a public action
  leaves a run finished or failed it wakes waiting parents (`WakeParentsAsync`, outside the child's lock; children
  driven inline by their parent skip it). Front-ends order runs with `RunStore.Nest` so children follow parents.
- Run labels (`Afl.Core/Runs/RunLabels.cs`): a run can have its own name (`Run.Title`; `DisplayTitle` falls back to the
  ticket's), tags (`Run.Tags`) and a folder (`Run.Folder`, a `/`-joined path compared ignoring case). Only a run started
  directly can be filed; its child and path runs show under it. A repo's folders are those in `RepoRecord.RunFolders`
  (created ones, listed while empty) plus those its runs are filed in, with their parents (`RunLabels.WithParents`).
  `RunEngine` has `Rename`, `Tag`/`SetTags`, `ListFolders`, `CreateFolder`, `MoveToFolder`, `RenameFolder` (moves the
  runs and folders in it) and `RemoveFolder` (moves them up a level; no run is deleted); a folder keeps the spelling it
  already has. CLI: `afl run rename|tag|move`, `afl run folders`, `afl run folder create|rename|remove`, and
  `afl runs --tag a,b --folder x`.
- Run states and filtering (`Afl.Core/Runs/{RunStates,RunFilter}.cs`): `Run.State` (`RunStates.Of`) is what lists show a
  run as doing: its status, with a run waiting on a person split out as `NeedsYou` (a failed step stays `Failed`).
  `RunStates.All` gives each state's symbol (● ▶ ◐ ✖ ✓ ⊘), label and CLI name; `RunStates.Parse` reads CLI names.
  `RunFilter` (search words, all of which must be found in the run's title, ticket, id, tags, folder, workflow, repo,
  agent or state; states; tags) is shared by `afl runs --search <text> --status needs-you,failed` and the UI, where the
  Runs page has a search box, state chips with counts (`RunFilterDataContext`), "Clear filters", coloured state symbols
  on rows (`UiConverters.StateBrush`) and per-state counts on folders and collapsed parent runs. The UI keeps a run
  tree when any run in it matches and opens the folders and parents of the matches.
- Workspaces (`Afl.Core/Workspaces/`): where a `workflow` step's children work is an `AbstractWorkspace`
  (`[WorkspaceRegistration]`, `WorkspaceRegistry`: `shared`, the default, and `worktree`), picked by
  `AbstractStepDefinition.Workspace`, with `MergeBack`. `WorkflowStepHandler` prepares a child's workspace just before it is
  first driven (so serial children start from the work merged before them), completes it when the child is done
  (a `Conflict` outcome fails the step; retry completes the unsettled ones) and discards it on cancel. A run's agents
  work in `Run.WorkingPath ?? Run.RepoPath` (`StepContext.CreateRequest`); children inherit `WorkingPath`, while
  artifacts and the scratch directory always resolve against `RepoPath`, the main checkout, which is passed as
  `ExecutionRequest.AdditionalDirectories` (`--add-dir` for Claude) when the two differ. `worktree` checkouts live in
  `AflPaths.WorktreesDirectory` (`~/.afl-worktrees/<run id>`), beside the afl home rather than in it, because the guard
  rails deny `Read`/`Edit` of `~/.afl/**` and Claude applies a deny rule even to its own working directory; keep any
  folder agents work in out of the home the same way (as `AuthoringWorkspacesDirectory` does). `Run.MaxParallel`
  (`afl start --max-parallel n`, `--sequential` = 1, inherited by children) caps how many paths or children run at once.
- Optional steps (format 3.7): `AbstractStepDefinition.Optional` (`OptionalStep`: `Default`, `Help`). `RunEngine.Create`
  takes the starter's choices (`afl start --include/--exclude`, the UI's checklist, `Views/ChecklistView.Open` in a Niddy `Dialog.View`) and
  records `WorkflowDefinition.LeftOutSteps(choices)`, computed on the expanded workflow, as `Run.LeftOutSteps`
  (format 2.7; child runs take their workflow's defaults). `FlowRouter.TrySkipAsync` skips a left-out step first,
  with `StepConstants.LeftOutNote`.
- Skills (format 3.4): `SkillDefinition` documents in `SkillLibrary` (`workflows/skills/`, `~/.afl/skills`,
  `.afl/skills`; `WorkflowLibrary.Skills`). `AbstractAgentStepDefinition.Skills` and `WorkflowDefinition.Skills` (merged into
  `ai`/`interactive` steps by `WorkflowComposer.Expand`, empty entries dropped) are resolved into
  `ExecutionRequest.Skills`, and the executor delivers them in `AbstractStepExecutor.PrepareSkills`: by default the
  content goes into the prompt; `ClaudeCliExecutor` writes a session plugin (`afl-skills`) and passes
  `--plugin-dir`. A workflow's `skills` are only the ones picked to start with: `Run.Skills` (`RunEngine.Create(...,
  skills)`, `afl start --skills/--add-skills/--drop-skills` through `SkillRecommender.Pick`; null keeps the
  workflow's) replaces them in `WorkflowComposer.Expand` and is inherited by child and path runs. Recommendations are
  data: a skill's `Recommend` block (`SkillRecommendRule`: `files` with glob/contains, `languages`, `frameworks`,
  `reason`; skill format 1.2) is matched by `SkillRecommender` against a `RepoScan` (`git ls-files` or a capped walk;
  `IsEmpty` when only docs/dotfiles) and the repo's stack (inferred from matched skills plus `RepoRecord.Stack`,
  `RepoRegistry.SetStack`). `SkillRecommender.AdviseAsync` gives front-ends one `SkillAdvice` (defaults, recommended,
  stack, `NeedsStack`, `StackChoices`); the CLI is `afl skill recommend`, the UI `Views/SkillPickerView`
  (`SkillPickerDataContext`) and `Views/StackView` (`StackDataContext`, also Repo settings → Project), both over the
  shared `PickListView`. Nothing recommended is added unless the user picks it. Other 3.4 fields: `OutputTemplate` (the output rendered from `{{inputs.x}}` and `{{output}}`) and
  `advance: approve-final` (approval only on a loop's last pass).
- Authoring (the AI helper, `Afl.Core/Authoring/`): a kind of document an AI can draft is one `AbstractAuthoringKind`
  (`[AuthoringKindRegistration("skill"|"step"|"workflow")]`, `AuthoringKindRegistry`) giving its library, `Parse`,
  `Validate`, `Catalog` (what exists already, for the prompt), its embedded schema/format reference and `Save`.
  `AuthoringService` owns a draft end to end: an `AuthoringSession` (`AuthoringStore`, `~/.afl/authoring/<id>/`, with
  `draft.json` beside it) and a one-step run built in code (`RunKind.Authoring`, a synthetic repo whose path is the
  session folder, the selected repo only in `directories` and never writable, policy always `ask`, WebSearch allowed
  and WebFetch left to the permission prompt). Its step is the internal `author-draft` type
  (`AuthorDraftStepHandler`): after each turn it validates `draft.json` through the kind and resumes the same session
  to fix problems, up to `MaxFixRounds`; leftover problems block Approve. Questions, permissions, Request changes and
  approval are the ordinary run machinery; `AuthoringService.ApproveAsync` re-validates and saves through the kind
  (`overwrite` to replace; a built-in base is saved as a global/repo copy). `AuthoringView.Status` is what
  front-ends show. `RunStore.List` leaves authoring runs out unless `includeAuthoring`, so the Runs page and
  `afl runs` never see them. Handlers with `AbstractStepHandler.IsInternal` are left out of the builder, the schema
  and `StepHandlerRegistry.PublicKeys`, and rejected in user workflows and common steps. CLI: `afl author-*`; UI:
  "New/Improve with AI…" and the "AI drafts" list on the Library pages (`LibraryPageDataContext.Authoring.cs`,
  `AuthoringSessionDataContext`, `Views/AuthoringSessionView`).
- Work without a ticket: `LocalTicketSource` (`local:<name>`, `afl start --name`) makes a ticket from a name. The
  scratch directory (`AflSettings.ScratchDirectory`, copied to `Run.ScratchRoot`) is `ScratchArea`: made when a run
  uses a `scratch.` variable or saves an artifact there, with a self-ignoring `.gitignore`; it is never tracked.
- Persisted documents derive from `AbstractVersionedDocument`. Bump `[DocumentVersion]` when a change isn't purely
  additive; there are no upgraders (a newer file than this afl is refused, an older one fails to load).
- `workflows/*.json` are embedded into Afl.Core as built-ins; keep them valid (`afl workflow validate`) and in
  sync with `Workflows/WorkflowModels.cs`, `workflows/README.md` and `workflows/workflow.schema.json`
  (`WorkflowSchemaTests` checks the schema against the model and registries).
- Niddy (Niddy.Core, Niddy.Avalonia, Niddy.Avalonia.Generators) comes from NuGet at one shared version
  (`NiddyVersion` in `tool/Directory.Build.props`). Don't fork Niddy code here; flag gaps instead. Its API guide is
  https://github.com/vonderborch/Niddy/blob/main/docs/AI-GUIDE.md.
- Jira: `Afl.Core/Jira/`. A new Jira flavour is one `AbstractJiraClient` subclass with
  `[JiraClientRegistration]`. JQL comes only from `JqlBuilder`. Tokens are handled only through `JiraInstanceStore`
  (Niddy `ISecureStorage`, `AFL_JIRA_TOKEN_<ID>` override); never write them to JSON or logs. Tests fake HTTP with
  `FakeHttpHandler`, and a test that saves a token must pass an `EncryptedFileSecureStorage` in the scratch home
  so it never touches the real keychain. Code that lists or fetches tickets for the UI goes through
  `Shell.TicketSources` (a `TicketSourceRegistry` over the shell's own Jira store), not `TicketSourceRegistry.Instance`
  (which reads `$AFL_HOME`). A repo's Jira link holds its associated projects (`RepoJiraLink.Projects`): they reach
  JQL only as `JiraFilter.ProjectScope` (set by `JiraTicketSource.ResolveCriteria`), and front-ends pick the repo
  for a ticket with `RepoRegistry.ResolveForTicket`.
- Git hosts (`Afl.Core/GitHosts/`): GitHub and GitLab, cloud or self-hosted. `GitHubClient` reads (list, search,
  get) through Niddy's `GitHubIssues` with a `GitHubReadOptions` cache under `AflPaths.CacheDirectory/<kind>`
  (`~/.afl/cache/github`: ETags, and the last answer when GitHub is down); filing stays on its own POST. Niddy takes the
  API address only from `HttpClient.BaseAddress`, so Enterprise reads go through a per-site `HttpClient` (`ApiHttp`),
  and tests give their fake `HttpClient` a `BaseAddress`. A kind is one `AbstractGitHostClient`
  (`[GitHostClientRegistration]`, `GitHostClientRegistry`: `github`, `gitlab`) that knows its API base, CLI
  (`gh`/`glab`, `AFL_GH`/`AFL_GLAB`), issue URL shape, token page and REST calls (current user, list, get, create);
  `AbstractGitHostTicketSource` turns any of them into a ticket source, so a new kind is one client plus a
  one-line source subclass. `GitHostStore` (`~/.afl/githosts.json`) holds the connections, with the built-in
  `github` and `gitlab` (github.com, gitlab.com; `IsBuiltIn`, can't be removed) always first. Tokens go only through
  its secure store under `githost:<id>` (`AFL_GITHOST_TOKEN_<ID>` override); with none, `ResolveTokenAsync` borrows
  the cloud env tokens and `gh`/`glab auth token --hostname <host>`, held in memory only (`GitHostToken.ToString()`
  never shows the value). Never write a token to JSON or logs, print it, or ask for one in chat; the UI shows only
  `TokenStatusAsync`. Tests pass `borrowSignIns: false` (and an `EncryptedFileSecureStorage`) so the developer's own
  sign-in is never used. A repo's link is `RepoRecord.Host` (`RepoHostLink`: connection, kind, project such as
  `group/sub/project`), set on first registration from the remotes (`GitHostStore.DetectLink`, origin first,
  matching the remote's host against the connections) and changed with `RepoRegistry.LinkHost`/`UnlinkHost`/
  `DetectHost`. `TicketRef` reads issue URLs through `GitHostStore.FindByUrl`, and `TicketRef.ForRepo` puts a bare
  `#N` on the repo's own host.
- UI (`Afl.UI`): `App` is a `NiddyApp`; its app data context is the `ShellDataContext` and its layout is
  `Views/ShellLayout` (repo picker plus a gear for the repo's settings, Niddy `PageMenu`, then Add repo… and
  Settings at the bottom). A page is an `AbstractAflPage<TDataContext>` with `[PageRegistration]` and
  `[PageIcon(PageIcons.X)]` (SVG paths live in `Pages/PageIcons`; the generator registers it; the menu sorts by
  `SortKey`: Issues "1", Runs "2", Workflows "3"). `SettingsPage` (tabs: Jira, Git hosts, Agents, MCP servers, Execution, Permissions;
  its data context is split into partials per tab) and `RepoSettingsPage` (the picked
  repo's name, default workflow, GitHub/GitLab project link, Jira link, projects and filters, project stack, permission rules) are `TopLevel = false` and opened from the layout's
  buttons (Cmd/Ctrl+, opens Settings). It builds its data context in `CreatePageDataContext`; Niddy disposes it when
  the page closes. Check-box lists bind to `SelectableItem<T>`; add/remove lists of ids (skills on a workflow or step) use `PickListDataContext` with the page's shared `PickList` template (picked ids each with Remove, then an editable dropdown of the rest plus Add, so a typed `{{params.x}}` entry works). Tag filters (the Library pages and the flow editor's step/include picker) use `TagFilterDataContext` with the shared `TagFilterView`: picked tags as removable chips, the most common other tags as one-click suggestions, and a searchable box over the rest; counts are faceted over what's shown, and the owner re-filters on `Edited` then calls `Update`. Jira discovery from a page (projects, boards) creates
  its client with `Shell.JiraInstances.CreateClient(instance, Shell.TicketSources.JiraHttp)` so tests hit the fake
  server, and falls back to typed keys/ids when Jira can't be reached. Pages navigate with `this.NavigateTo<TPage>(addToBackStack, parameter)` and read the parameter in
  `OnNavigatedTo` (e.g. Issues hands Runs the id of the run it just started). Destructive confirmations use
  `WarnAsync` (Niddy's warning dialog); toasts use `ShowToast`, which works even after the page navigated away. The
  data context derives from `AbstractPageDataContext` (reloads when the sidebar repo changes; wrap store calls in `Try` so errors land in
  `ErrorMessage`). Runs are driven in-process by `RunDriver`; interactive steps open a terminal through
  `Afl.Core/Terminals` (a new OS/terminal is one `AbstractTerminalLauncher` with `[TerminalLauncherRegistration]`).
  After writing `repos.json` from a page, call `Shell.Reload()` so every page gets the fresh `RepoRecord` without
  waiting for the file watcher. Stores write through Niddy's `AtomicFile` and watch through `DebouncedFileWatcher`, so
  `Changed` already arrives once per burst; don't add another debounce in data contexts. The app logs (unhandled
  exceptions included) to `~/.afl/logs`. The builder edits through an `AbstractLibraryEditor` (`WorkflowLibraryEditor`,
  `CommonStepLibraryEditor`, `SkillLibraryEditor`); each kind is its own sidebar page, an
  `AbstractLibraryPage<TEditor>` subclass carrying only its registration, so a new kind of document is one editor
  subclass plus one page subclass. It saves only through its library's
  `Save(document, scope)`. A workflow's steps are edited in the flow graph (`Views/FlowEditorView` over
  `Flow/FlowGraphDataContext`), which fills the step area: Add step lists the step types (each handler's
  `DisplayName`/`Summary`) and opens a `FlowPickerDataContext` (search plus tag filters) for common steps and
  includes; clicking a node opens its properties beside the graph (`IsPropertiesOpen`, `ShowsStepPanel`), and arrows
  are drawn from a node's handle (drag, or click then click the target) or its right-click menu (`TargetsFrom`);
  `Connect` adds a plain arrow. The graph is laid out in layers (rank = longest path from the start) unless a step
  has a saved `position` (dragging moves it; `Tidy` clears them); the start step has a "▶ start" badge
  (`MakeStart`), steps that end the run a "■ ends" tag, steps that split "⑂ parallel" and steps with several arrows
  in "waits for all/any/…". Each kind of arrow is an `AbstractFlowEdgeSource` (`[FlowEdgeSourceRegistration]`;
  `ArrowEdgeSource`, coloured by `on`, dashed going back, labelled with `label` or its condition). Clicking an arrow
  opens its editor beside the graph (`OpenArrow`, an `ArrowDataContext`: target, fires on, condition, and the limit
  for one going back); the open arrow is drawn heavier with a dot on its end, and dragged onto another step to
  repoint it (`Retarget`). Arrow records are rebuilt every layout, so the editor finds its arrow again with
  `Matches(edge)`. The step editor (`StepDraftDataContext`) holds the fields every step shares; what a step type adds is one `AbstractStepDetailsDataContext<TDefinition>` part with `[StepDetailsRegistration(typeof(TDefinition))]` (agent, user, workflow; subtypes such as interactive find their base's part), shown as `Details` through a DataTemplate in `LibraryPage.axaml`, cached per type so switching back keeps its edits. What a step does lives in the `StepFields` template in `LibraryPage.axaml`: a step written out here shows it inline, a `use` step tucks it into a collapsed "Override the common step" expander (counting the fields it sets beyond `PlacementProperties`), and an `include` step shows only id, name, parameter values and arrows. A step that refers to a common step or workflow (`ReferencedId`, from `use`, `include` or the part's `WorkflowsUsed`) gets a View button that navigates to that page with the id as the parameter (`LibraryPageDataContext.Open`), or opens it in place on the same page. Workflows that lead back to the one being edited (`WorkflowComposer.LeadsTo`) are left out of every workflow picker. The step editor has Arrows out (with Follow and reordering) and Waits for (All/Any/Custom, a
  recursive `WaitGroupDraftDataContext` editor, plus `others`); a step's menu has "Make this step conditional…"
  (`ConditionalStepDataContext`, `ApplyConditional`). Edits are snapshotted into a `DraftHistory<T>` for Undo/Redo
  (⌘Z/⇧⌘Z); several steps can be picked (box drag, ⇧-click, ⌘A) and moved, deleted, copied, pasted or duplicated
  (inner arrows kept, ids made unique, references rewritten). Simulate (`IsSimulating`) locks the canvas
  (`CanEdit`), gives each step a cycling outcome button and makes conditional arrows and arrows back clickable to
  toggle taken; it runs `FlowSimulator` and draws a `SimulationOverlay` (an `AbstractFlowOverlay`) over the graph:
  arrows fired, dead (faded) or pending, and each step's `FlowNodeMark` and status. The Runs page opens on its Graph tab,
  showing the selected run on its workflow's graph the same way (`RunsPageDataContext.Graph`, a read-only
  `FlowGraphDataContext` owned by a hidden `LibraryPageDataContext`, with a `RunOverlay`); the Timeline tab is the
  alternative view. Clicking a step on the graph opens it in a popup over it (`OpenStep`, `PopupStep`), which carries
  the step's actions (the shared `StepActions` template; on the graph they appear only there) and its card (the
  shared `StepCard`, also the timeline's item), "Show in timeline" (`ShowInTimeline`) and, for a step waiting in a
  path run, "Open path run". The popup opens by itself when the run newly waits on the user (`Run.WaitsForUser`,
  `RunStep.WaitsForUser`; for a split, the first waiting step in its paths) and stays closed after Close until what
  waits changes. The run list is `Rows` (`AbstractRunListRowDataContext`: `FolderRowDataContext` and
  `RunRowDataContext`; `RunRows` is just the runs'): folders first, then runs, filtered by `TagFilter` (a run is kept with
  the runs it started when any of them has every picked tag; empty folders are hidden while filtering). Child and path
  runs ("⑂ path x") fold under their run, collapsed by default, with a summary of what's folded and how many need
  you; `_expanded` holds each row's `ExpandKey`. Opening a run expands its ancestors and, when the selected run
  changes, its folders (`ExpandTo`), and folding away the selected run selects its parent. Selecting a folder row
  keeps the selected run; New folder… creates inside the selected folder. Folder changes aren't watched, so each
  action reloads (`TryAndReload`). A split shows its paths with Open
  buttons and which of their steps wait (`PathStepsText`).
  Delete run… (`RunEngine.Delete`, `afl run delete`) cancels the run first if it's still going, then removes it with its
  step logs, lock file and the child runs it started (not reused ones), tidying their workspaces; a child can't be
  deleted while its parent is kept. The ticket's scratch documents are shared between runs and stay.
  A step waiting for approval whose output ends with questions (its last heading starts with "Questions" or "Open
  questions" and sits over a list: `OpenQuestion.Find`, `RunStep.OpenQuestions`) shows an answer box per question (`Questions`, `SendAnswers`);
  `RunEngine.AnswerQuestionsAsync` sends the step back with `OpenQuestion.FormatAnswers` as the feedback (blanks keep
  the AI's assumption). The CLI shows them as `openQuestions` and answers with `afl run reject --answer n=...`.
  Every AI step that waits for approval (`approve`, `approve-final`; every agent of a multi-agent one) is told that
  format in its prompt (`AbstractAiStepHandler.OpenQuestionsInstructions`, `StepConstants.OpenQuestionsInstructionsFormat`:
  a last `## Open questions` section, numbered, bold question then the assumption), so built-in and user workflows
  don't each spell it out; blocking questions stay in `afl-ask`.
- The Issues page lists tickets in a Niddy `TableGrid` (Key, Title, Status, Assignee; sortable, paged at 25, an
  `EmptyText`); double-click or Enter (`ItemActivated`) starts a workflow like Start workflow. Runs stays a `ListBox`,
  because its rows are a folding tree.
- Context menus: every list whose rows have actions gets them on right-click too. Set `views:RowMenus.IsEnabled="True"`
  on the list (a `ListBox`, or anything with `ItemsSource`/`SelectedItem`, like the Issues `TableGrid`): right-clicking
  a row selects it before the menu opens, and no menu opens over empty space, so the menu reuses the page's buttons'
  handlers and `Can…` bindings on the selection. Put the menu on the list when every row is the same kind; for mixed
  rows (Runs: folders and runs) put one in each row template, typed by the page's data context (`RowMenus` hands it
  the list's DataContext on open). Submenus built from data (Tags, Move to) bind `ItemsSource` to `MenuChoice` lists
  refreshed in the menu's `Opening` and use the `ChoiceMenuItem` theme (`App.axaml`) for checks and radios.
  `AbstractAflPage.CopyText`/`OpenUrl` back "Copy …" and "Open in browser" items. A list rebuilt on store changes
  must hold still while its menu is open (Runs: `IsRowMenuOpen`, set from the menus' `Opened`/`Closed`, defers
  `RebuildRows`), or the menu closes under the user whenever a run's file changes. The flow editor builds its node and
  arrow menus in code, from `ContextRequested`. On macOS Control+click is a right-click (`Views/ContextClicks`,
  installed by `App.Initialize`): the press is swallowed at the top of its route and the release raises
  `ContextRequested`, so every menu opened from `ContextRequested` works with it unchanged. Open menus from
  `ContextRequested`, not from a right-button press; code that has to check the button itself uses
  `ContextClicks.IsContextPress`/`IsContextRelease`.
- Markdown anywhere in the UI goes through `Views/MarkdownView` (over Markdown.Avalonia, which comes with
  Niddy.Avalonia): formatted by default, a Raw toggle, and Expand into `MarkdownView.OpenDialog`: another `MarkdownView`
  (with Copy, no Expand) in Niddy's `Dialog.View.Open(..., fill: true, maxWidth)`, a resizable window at 80% of the
  one it opens over (Niddy's `Dialog.Markdown` caps its viewer at 420px, so it isn't used). On desktop Niddy's shell
  shows dialogs as windows, which close on a later dispatcher turn, so UI tests pump until `OwnedWindows` is empty. With `IsEditable`
  it is an editor with a Preview toggle that writes back to its two-way `Markdown` binding. It's used for step output,
  prompts, candidates and earlier attempts on Runs, issue bodies, step prompts and SKILL.md. A step artifact ending in
  `.md` / `.markdown` (`UiConverters.IsMarkdownPath`) gets a Preview link next to Open. Diagrams (`Afl.UI/Diagrams/`):
  a fenced block whose language has an `AbstractDiagramRenderer` (`[DiagramRendererRegistration("mermaid")]`,
  `DiagramRendererRegistry`; mermaid is drawn by Mermaider, no browser) is shown as an SVG picture, themed light or
  dark, cached by source. `DiagramMarkdownPlugin` replaces Markdown.Avalonia's fenced-block parser
  (`CodeBlocksWithLangEvaluator`, a `BlockOverride2`; a `RegisterTop` parser never gets the block) and hands other
  languages to a stock engine, so highlighting is unchanged; one that fails shows as code with a note why.
  `MarkdownView` must assign a new `MdAvPlugins` (adding to the existing one doesn't take). The Skia SVG renderer
  ignores CSS variables, `color-mix()` and `rem`, so `AbstractDiagramRenderer.Render` passes every SVG through
  `SvgCssFlattener`; a new language is one renderer subclass.
- Never block on async work (`GetAwaiter().GetResult()`) unless every await beneath it uses
  `ConfigureAwait(false)`: the UI and the headless tests run on Avalonia's dispatcher and would deadlock.
- UI tests (`tests/Afl.UI.Tests`, xunit v3 + Avalonia.Headless) run `TestApp` (the app's own shell over a scratch
  home) and render pages in Niddy's `AppShell` over `AbstractUiTest.CreateShell()` (fake executor, encrypted-file Jira
  store, a fake Jira server, `AbstractUiTest.Jira`, and a fake GitHub/GitLab API, `AbstractUiTest.GitHost`, so
  nothing reaches the network). `AFL_UI_SCREENSHOTS=<dir>` saves a PNG of every page; look at them
  after UI changes.
- The `/afl` skill only reaches afl through the CLI. If you change CLI commands or `--json` output shapes
  (`Output/RunView.cs`), update `plugin/skills/afl/SKILL.md` too.
