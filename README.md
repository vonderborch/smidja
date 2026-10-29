# smidja

> *Smiðja* is Old Norse for "smithy", the workshop where things get made. *Afl* is the forge hearth at its centre (the word also means "strength").

My Claude Code toolkit, in one repo:

- **`plugin/`**: a Claude Code plugin with skills, slash commands and subagents.
- **`workflows/`**: the built-in afl workflows: ordered steps done by the AI, by you, or by both.
- **`tool/`**: **afl**, a C# harness that takes a GitHub or GitLab issue (cloud or self-hosted) or a Jira ticket (Cloud or Data Center) and runs a workflow against it, with a CLI (`afl`) and an Avalonia desktop UI built on [Niddy](https://github.com/vonderborch/Niddy).

```
smidja/
├── .claude-plugin/marketplace.json   # makes this repo installable with /plugin
├── plugin/
│   ├── .claude-plugin/plugin.json
│   ├── skills/
│   │   ├── afl/                     # /afl, explicit-only (disable-model-invocation)
│   │   └── dotnet-build-test/        # model-invoked when working in .NET repos
│   ├── commands/handoff.md           # /smidja:handoff <run id>
│   └── agents/code-reviewer.md
├── workflows/                        # feature-flow, debug-flow, feature, bugfix, fix-until-green, hotfix, epic, … (embedded)
├── tool/                             # Afl.slnx
│   ├── src/Afl.Core                 # stores, tickets, workflows, steps, executors, RunEngine
│   ├── src/Afl.Cli                  # `afl` .NET global tool
│   ├── src/Afl.UI                   # Avalonia UI (system-wide, live-updates from ~/.afl)
│   ├── tests/Afl.Core.Tests
│   └── tests/Afl.UI.Tests           # headless Avalonia: page data contexts + a render of every page
└── scripts/                          # install-cli.sh, run-ui.sh
```

## Install

**Plugin** (in Claude Code):

```
/plugin marketplace add ~/RiderProjects/smidja
/plugin install smidja@smidja
```

Once the repo is pushed to GitHub, you can add it with `/plugin marketplace add <owner>/smidja` instead.
Run `/plugin marketplace update smidja` to pick up changes.

**CLI** (needs the .NET 10 SDK):

```bash
./scripts/install-cli.sh
```

**UI**:

```bash
./scripts/run-ui.sh
```

## How the pieces fit

All front-ends share one store, `~/.afl/` (override with `AFL_HOME`):

```
~/.afl/
  repos.json                  # repos afl knows about (any afl command run in a git repo registers it),
                              # with each repo's GitHub/GitLab link, Jira link and saved filters
  githosts.json               # GitHub Enterprise and self-managed GitLab sites (no secrets; tokens live in the OS keychain)
  jira.json                   # Jira instances (no secrets; tokens live in the OS keychain)
  agents.json                 # your AI agents (command-line tools or MCP tools); claude and session are built in
  mcp-servers.json            # MCP servers afl connects to (no secrets; those live in the OS keychain)
  settings.json               # global settings: main agent, run mode, how steps choose agents, your capabilities
  workflows/*.json            # your global workflows (override the built-ins by id)
  steps/*.json                # your global common steps, which workflows `use`
  runs/<repoId>/<runId>.json  # run state; <runId>/<stepId>.log holds raw Claude output
  logs/                       # the desktop app's log files (unhandled errors and diagnostics)
```

A repo can also commit its own workflows to `<repo>/.afl/workflows/` and common steps to `<repo>/.afl/steps/`.
Those override global and built-in ones with the same id.

A **run** takes one ticket through one workflow. Each step is one of:

| Step | What happens |
| --- | --- |
| `ai` | Claude runs the prompt headlessly (`claude -p`). |
| `interactive` | afl opens an interactive `claude` session. When you mark it done, afl records a summary. |
| `user` | afl shows you the instructions and waits for `afl run done`. |
| `workflow` | afl starts child runs of another workflow (one, or one per ticket or line) and waits for them. |

Any step can also wait for your approval once its work is done (`"advance": "approve"`): you approve it, or send
it back with feedback (an AI step revises in the same session).

Steps can ask you questions (`inputs`: text, multi-line, number, choice, multi-choice or yes/no) at three points,
set per input with `when`: `before` the step starts (its prompt can use `{{inputs.<id>}}`), `after` its work (when
you approve it or mark it done), or `ai`: the AI asks it part-way through, only if it needs the answer. An `ai`
step with `"ask": true` may also ask its own questions. The CLI prompts for them (or takes `--input id=value`),
the UI shows a form, and `/afl` asks them in the chat. Later steps use the answers as
`{{steps.<step>.inputs.<id>}}`.

A workflow is a graph: each step's **arrows** say where the run goes once it ends, on success, failure, timeout or
always, and only when a condition holds if they say so. Several arrows that fire split the run into **parallel
paths**, and a step that several arrows point at says what it waits for (`all`, `any`, at least n, or nested groups)
and whether the paths still on their way are cancelled. An arrow back to the same or an earlier step is a **loop**,
at most `max` times. A condition checks an input's answer, the output's text, or asks an AI a yes/no question, and
can combine others with `all`/`any`/`not`. Steps can also `retry` and have a `timeout`, and the run shows the path it
took. The built-in `fix-until-green` workflow loops until the tests pass.

A step can save its output as a document in the repo (`artifact`, e.g. `docs/afl/{{ticket.slug}}/DESIGN.md`) that
later steps point the AI at, use MCP servers when they're set up (`optionalMcpServers`), and let the AI file
follow-up tickets in Jira or GitHub (`createTickets`). The built-in `feature-flow` workflow uses all of these to take
a piece of work from intent through research, specs, spikes and prototypes, tickets and reviewed builds, a security
review, release and observability plans to a retro that updates `CLAUDE.md`; `hotfix` is the fast path for
production problems.

Workflows build on each other. A step can `use` a **common step** (a reusable step file, such as the built-in
`code-review`) and fill in its parameters with `with`, overriding any field; a step can `include` another workflow's
steps inline; a `workflow` step runs another workflow as **child runs**, one per ticket or line of output to fan out
(the built-in `epic` runs `feature` on each sub-ticket it files); and `requires` makes a workflow depend on another
having finished on the ticket, reusing that run if there is one. Workflows and common steps can take
**parameters** (`{{params.<id>}}`), given with `afl start --param id=value` or when you start one in the UI.

Prompts are templates. They can use `{{ticket.title}}`, `{{steps.plan.output}}`, `{{feedback}}` and other
variables; see `workflows/README.md`.

| You want to… | Use |
| --- | --- |
| Start or drive a run from a terminal | `afl` CLI |
| Have *this* Claude session do the AI steps, with you approving | `/afl <ticket>` in Claude Code |
| Watch every repo's runs at once | afl UI |
| Write a hand-off note at the end of a session | `/smidja:handoff <run id>` |

`/afl` only runs when you type it (`disable-model-invocation: true`), and it reaches afl only through the CLI.
It uses the *session executor*: afl hands each AI step's prompt to the chat instead of calling `claude -p`.

## afl CLI

```
afl issues [--search dark]              # open GitHub or GitLab issues for this repo's linked project
afl issues --source jira [--filter mine] [--project P] [--assignee me] [--status "In Progress"] [--jql "…"]
afl ticket 42                           # 42, #42, owner/repo#42, group/project#42, PROJ-45, or a GitHub/GitLab/Jira URL
afl start PROJ-45 [--workflow bugfix] [--agent codex] [--mode terminal] [--param area=billing]   # runs until it needs you
afl start --name "dark mode" --workflow feature-flow [--single-thread]   # work with no ticket; child runs one at a time
afl start PROJ-45 --workflow feature-flow --include retro,deploy --exclude pr   # pick optional steps (afl workflow-show lists them)
afl start PROJ-45 --skills caveman,csharp-style [--add-skills x] [--drop-skills y]   # the run's skills (afl skill recommend suggests some)
afl start PROJ-45 --permissions ask|strict|auto [--yes-auto]   # what happens when an agent is refused a command
afl runs [--all-repos]                  # child runs are listed under their parent (fork branches inside their run)
afl run show|resume|approve|done|answer|retry|cancel <run>    # the last 4 characters of the run id are enough
afl run approve|done <run> --input scope="Just the toggle" --input notes=@notes.md   # answer the step's inputs
afl run answer <run> [--input id=value ...]                       # answer a step waiting for input (before it, or the AI asking)
afl run reject <run> --feedback "Also handle X"                  # send the step waiting for approval back
afl run reject <run> --answer 2="Use 1.x" [--feedback "..."]    # answer the open questions the AI ended its output with
afl run approve|done|answer|reject|retry|submit|choose|merge|note <run> --step lint   # which step, when a fork's branches have several waiting
afl run step <run> <step>                                         # a step's output and earlier attempts
afl run replay <run> <step> [--feedback "..."]                    # redo a step that has run, and the steps after it
afl run note <run> "Decided to use X"
afl run rename <run> Dark mode, take two                          # the run's own name (no name: the ticket's title again)
afl run tag <run> billing urgent [--remove later] [--clear]       # tags; filter with afl runs --tag billing,urgent
afl run move <run> billing/q4                                     # file a run in a folder (none: the top level)
afl run folders | afl run folder create|rename|remove <folder> [<new>]   # removing one moves what's in it up a level
afl runs --folder billing                                         # the runs in a folder and the folders inside it
afl runs --status needs-you,failed                                # only runs in these states (needs-you, running, waiting, failed, done, cancelled)
afl runs --search billing export                                  # runs whose title, ticket, id, tags, folder, workflow, repo or state hold every word
afl run delete <run>                                              # delete a finished run, its logs and the child runs it started
afl run submit <run> --agent session --output @result.md      # the session's result, on a step several agents do
afl run fail <run> --reason "The staging box is down"         # a session or user step can't be done; its failure arrows run
afl run choose <run> <agent> [--output @edited.md]                # use one agent's result (edited, if you like)
afl run merge <run> [--agent claude] [--note "Keep codex's tests"] # have an agent merge every agent's result
afl run use-agent <run> <agent> [--step review] [--reason "..."]  # pick the agent for a step (or answer choosing-agent)
afl run permit <run> --once 1 | --always 1=Bash(dotnet test:*) [--scope repo] | --deny 1 | --all once | --accept-output   # answer needs-permission
afl permission list | add <rule> [--deny] [--scope repo|global] | remove <rule> | guards [--add|--remove <rule>] [--reset]
afl workflow list|show <id>|validate <file>|new <id> [--from feature] [--in-repo]
afl workflow show <id> --expanded [--param id=value]  # the steps a run follows, with common steps and includes filled in
afl step list|show <id>|validate <file>                # common steps
afl step new <id> --from-workflow hotfix --step review [--in-repo]   # save a workflow's step as a common step
afl skill list|show <id>|validate <file>|new <id> [--in-repo]|delete <id> [--in-repo]   # common skills, e.g. caveman
afl skill import <SKILL.md or folder> [--in-repo] | export <id> <dir>
afl skill recommend [--workflow feature-flow] [--language python] [--framework fastapi] [--clear-stack]   # skills that suit this repo
afl workflow list --tag review --tag security        # workflow, step and skill lists: only ones with every tag given
afl step list --search "deserialization" [--names-only] [--scope builtin|global|repo]   # words in the name or contents
afl repo [list|add <path>|remove <repo>|set --default-workflow <id>]
afl agent [list|show <id>|detect|remove <id>]          # the AI systems that can do step work
afl agent add codex [--id codex-fast --model o4-mini]   # from a preset (detect lists them)
afl agent add --id mine --kind cli --command my-ai --arg=-p --arg "{{prompt}}" [--output json --output-path result]
afl agent add --id codex-mcp --kind mcp --server codex --tool codex --tool-arg "prompt={{prompt}}"
afl agent add codex --capability review --mcp-server github  # what it's good at, and MCP servers it always gets
afl agent test <id> [--prompt "Say hi"]
afl settings [show|set <key> [value]]   # default-agent, default-run-mode, default-selector, scratch-directory, agent-preference, notify-on
afl settings set notify-on needs-you,done,failed   # desktop notifications for these run states (none: off; empty: default); the desktop app also flashes its taskbar button / bounces its Dock icon for them
afl notify-test                         # show a test notification (terminal-notifier/osascript on macOS, notify-send on Linux, toasts on Windows)
afl capability [list|add <id> -d "..."|remove <id>]
```

### MCP servers

```
afl mcp [list|show <id>|remove <id>]
afl mcp add github --command npx --arg -y --arg @modelcontextprotocol/server-github --env "GITHUB_TOKEN={{secret.GITHUB_TOKEN}}"
afl mcp add docs --url https://mcp.example.com/sse --header "Authorization=Bearer {{secret.TOKEN}}"
afl mcp import [--from claude-code|claude-desktop|cursor|codex] [--server id ...]   # lists what it finds without --server
afl mcp tools|test <id>
afl mcp secret <id> <NAME>              # reads the value from a hidden prompt
```

afl is an MCP client. A server is used either as an agent (`--kind mcp --server <id> --tool <tool>`) or as extra
tools for the agent doing a step (a step's `mcpServers`, or an agent's `--mcp-server`). Secrets go to the OS secure
store under `mcp:<server>:<NAME>` and are referenced as `{{secret.NAME}}`; `AFL_MCP_SECRET_<SERVER>_<NAME>` overrides
one. Importing moves secret-looking env and header values into the secure store, and secrets are never printed.

### Choosing agents

A workflow step can name what it needs (`capabilities`, such as `review` or `edit-code`) instead of an agent, so it
works on any machine. Of the installed agents that have them, `select: first` takes the first by your preference
order, and `select: ai` asks the run's main agent to pick (with the `session` agent, the step waits at
`choosing-agent` until `afl run use-agent`). A step with `delegate` lets its agent hand sub-tasks to other agents
mid-step. The run mode (`--mode`, or `default-run-mode`) runs command-line agents in the background or in a
visible terminal window.

### GitHub and GitLab

```
afl host list                           # github and gitlab are built in; shows where each one's sign-in comes from
afl host add ghe --kind github --url https://ghe.acme.com        # GitHub Enterprise Server
afl host add work-gl --kind gitlab --url https://git.acme.com    # self-managed GitLab
afl host test|forget|remove <id>        # sign in and show the account | clear the token | remove (--force unlinks repos)
afl repo host                           # the project this repo's issues come from
afl repo host detect                    # link it from the git remotes (origin first)
afl repo host link work-gl platform/tools/app   # or a project URL; afl repo host unlink to undo
```

- **Sign-in.** `host add` reads a personal access token from a hidden prompt, or from stdin when piped, and checks it
  (skip with `--skip-verify`). Leave it empty to borrow `gh auth token` / `glab auth token` for that host, which afl
  holds in memory only; public projects can also be read without signing in. Tokens go to the OS secure store under
  `githost:<id>`, and `AFL_GITHOST_TOKEN_<ID>` overrides one (for github.com, `GH_TOKEN`/`GITHUB_TOKEN` also work).
  `AFL_GH` and `AFL_GLAB` point at other `gh`/`glab` executables.
- **Repo links.** A repo registered for the first time is linked to the project its remotes point at when the
  remote's host matches a connection. A bare `42` or `#42` means an issue on the linked project, and issue URLs from
  any connection's site are recognised.

### Jira

```
afl jira instance add work --kind cloud --url https://acme.atlassian.net --email me@acme.com
afl jira instance add corp --kind datacenter --url https://jira.corp.example
afl jira instance list|remove <id> [--force]
afl jira link --instance work [--project APP,WEB]   # link this repo (afl jira unlink to undo)
afl jira project add|remove APP [WEB …]  # the Jira projects whose tickets belong in this repo
afl jira projects | boards [--project P]
afl jira filter add mine --project P --assignee me --label backend [--board 12] [--team …] [--jql "…"] [--default]
afl jira filter list|remove <name>|default <name>|default --clear
afl jira                                # this repo's link and filters (* marks the default)
```

- **Tokens.** `instance add` reads the token from a hidden prompt, or from stdin when piped, and checks it
  against Jira (skip with `--skip-verify`). Cloud wants an API token plus `--email`. Data Center wants a
  personal access token. Tokens go to the OS secure store (Keychain, Credential Manager, Secret Service, or
  an encrypted file) under `jira:<id>`. Setting `AFL_JIRA_TOKEN_<ID>` (for example `AFL_JIRA_TOKEN_WORK`)
  overrides the stored token, which suits CI.
- **Saved filters.** Criteria are ANDed together. `--assignee me` means `currentUser()` and `unassigned` means
  empty. `--status` replaces the open/closed state filter. `--jql` is ANDed on as well, and an `ORDER BY` in
  it replaces the default `updated DESC`. `--board` lists through that board's issue endpoint. On Cloud,
  `--team` is the Teams field id; on Data Center, there's no standard team field, so `--team` takes raw JQL.
- **Repo projects.** A repo can be associated with Jira projects (`jira link --project`, `jira project add`, or
  the repo's settings in the UI). Jira listings that don't name a project, a board, or `project` in their JQL are then
  limited to those projects (`afl issues --all-projects` searches everything). A ticket key also finds its
  repo: `afl start WEB-7` outside any repo uses the repo associated with `WEB`, and starting a ticket in a repo
  it doesn't belong to prints a note naming the right one (the run still starts where you asked).
- **`afl issues`** applies the repo's default filter unless you pass `--filter <name>` or
  `--no-default-filter`. Options given on the command line override the filter's. Without `--source`, it
  uses Jira when you pass Jira-only options, or when the repo has no GitHub or GitLab link but is linked to Jira.

Add `--json` to any command for machine-readable output. Exit codes: 0 ok, 1 usage, 2 afl error, 3 run failed.
Set `AFL_CLAUDE` to use a different `claude` executable.

## afl UI

One window for every registered repo. The dropdown at the top of the sidebar picks **All repos** or a single repo
from `repos.json`, and the gear beside it opens that repo's settings. **Add repo…** at the bottom registers a folder,
as running `afl` in it would, and **Settings** (Cmd/Ctrl+,) opens the settings shared by every repo. The menu in
between has the pages:

- **Issues**: lists GitHub or GitLab issues (assigned to you or by label, if you like) or Jira tickets (through a saved filter) for the repo, or links one by number,
  key or URL. **Start workflow…** picks a workflow (the repo's default first), asks for its parameters, and starts
  a run.
- **Runs**: the runs and each run's steps. Approve, reject with feedback, mark a user step done, add a note, or
  cancel. Child runs are listed under their parent and on the step that started them (with **Open**), and a child
  links back to its parent. The UI drives runs in-process and streams AI output live. An interactive step opens `claude` in a new
  terminal window, and you mark it done when you finish.
- **Workflows**: the builder. It lists built-in, global and repo workflows. Edit steps (type, prompt or
  instructions, agent, permission mode, model, allowed tools), reorder them, and double-click a variable to copy it. The
  draft is checked as you type, and **Save** writes it as JSON to `~/.afl/workflows/<id>.json` (global) or
  `<repo>/.afl/workflows/<id>.json` (repo). Editing a built-in saves a global copy that overrides it. A step can be based on a common step (its values show
  as placeholders, and you fill in its parameters), include another workflow, or start child runs; the workflow's
  parameters and requirements have their own section; and **Save as common step…** / **Copy into workflow** move a
  step between the two.
- **Common steps** and **Skills**: the same builder for the common steps workflows `use` (saved to `steps/`) and the
  skills AI steps are given (saved to `skills/`). Leaving any of the three pages with unsaved edits asks first. Each
  page's list can be searched by name or contents, and filtered by tags and scope; a draft's tags are edited beside its
  description.
- **Repo settings** (the gear): the repo's name, path and default workflow, **Remove repo…**, the GitHub or GitLab
  project its issues come from (pick a site and project, or **Detect from remotes**), and its Jira link. Linking an instance lists its projects and boards: tick the projects that belong to the repo, and pick a
  project, board and assignee (`me`, `unassigned`) for saved filters. If Jira can't be reached, type project keys and
  board ids instead.
- **Settings**, in tabs:
  - **Jira**: instances (the token goes to the system keychain after a connection test).
  - **Git hosts**: github.com and gitlab.com plus any Enterprise or self-managed sites; **Test & save** checks a
    token before it goes to the keychain (shown only as set / not set), and with none afl uses the gh or glab sign-in.
  - **Agents**: add one from a preset (installed tools are marked), edit it in a form built from its kind (or as
    JSON), set its capabilities and MCP servers, **Test** it, and order your preference.
  - **MCP servers**: add or **Import…** servers, set secrets (shown only as set / not set), **Test** to list tools,
    and **Create agent from tool**.
  - **Execution**: the default main agent, run mode and selector, and your own capabilities.

  **Start workflow…** asks which agent runs the AI steps once you have more than the built-ins, and which run mode
  to use. The builder's step editor has capabilities, select, delegate and MCP servers, and the Runs page shows who
  chose each step's agent, a picker while a step is choosing, and the step's delegations.

The UI watches `~/.afl`, so runs the CLI or `/afl` changes show up on their own.

## Extending

- **New skill**: `plugin/skills/<name>/SKILL.md`. Add `disable-model-invocation: true` to make it `/name`-only.
- **New command**: `plugin/commands/<name>.md` → `/smidja:<name>`.
- **New agent**: `plugin/agents/<name>.md`.
- **New workflow**: the UI's Workflows page, or `afl workflow new <id>` (global) or `--in-repo` (committed with
  the repo). Either way it is a JSON file. Built-ins live in `workflows/` and are embedded at build time.
- **New common step**: the UI's Common steps page, or `afl step new`. Built-ins live in
  `workflows/steps/`.
- **New common skill** (instructions afl gives a step's agent, e.g. `caveman`): the UI's Skills page,
  `afl skill new`, or `afl skill import` an existing `SKILL.md`. Built-ins live in `workflows/skills/`.
- **New UI page**: an `AbstractAflPage<TDataContext>` with `[PageRegistration]` (give it a `SortKey` to place it in
  the menu, or `TopLevel = false` to open it only from code) and `[PageIcon]`, and an `AbstractPageDataContext`. The Niddy generator registers it and the sidebar menu
  picks it up.
- **New step type, AI back-end or ticket system**: subclass `AbstractStepHandler`, `AbstractStepExecutor` or
  `AbstractTicketSource<TFilter>` in `Afl.Core` and tag it with its registration attribute. See `CLAUDE.md`.
- **New AI tool**: usually no code. `afl agent add --kind cli` (any command) or `--kind mcp` (a tool on any MCP
  server), or add a preset to `agents/presets.json`. A new *kind* of agent is an `AbstractStepExecutor`.
- **New terminal for interactive steps**: subclass `AbstractTerminalLauncher` with
  `[TerminalLauncherRegistration]`.
- **Hooks / MCP servers**: add `plugin/hooks/hooks.json` or `plugin/.mcp.json`.

## Develop

```bash
cd tool && dotnet build && dotnet test
claude plugin validate .
```

To save a screenshot of each UI page (rendered headlessly over sample data), set `AFL_UI_SCREENSHOTS`:

```bash
cd tool && AFL_UI_SCREENSHOTS=/tmp/afl-shots dotnet test tests/Afl.UI.Tests
```
