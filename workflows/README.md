# Workflows

The built-in workflows used by afl (`tool/`). A workflow is a graph of
**steps** run against a ticket (a GitHub issue or Jira ticket): each step says where
the run goes next (see [Flow](#flow-arrows-paths-and-loops)). These files are
embedded into `Afl.Core` at build time.

Workflows are looked up in three layers, and a later layer overrides an earlier one
with the same `id`:

1. built-in (these files)
2. global: `~/.afl/workflows/<id>.json`
3. repo: `<repo>/.afl/workflows/<id>.json` (commit it to share it with the team)

`afl workflow new <id> [--in-repo]` copies a starting point into the global or repo
layer. `afl workflow validate <file>` checks one. The UI's Workflows page edits and
validates the same files and saves them as JSON to either layer; editing a built-in
saves a global copy.

Common steps (see [Common steps and composition](#common-steps-and-composition)) are layered the same way:
`steps/*.json` here, then `~/.afl/steps/<id>.json`, then `<repo>/.afl/steps/<id>.json`.

`workflow.schema.json` describes the format for editors: start a file with
`"$schema": "<path or URL to workflow.schema.json>"` for completion and checking as you type (afl ignores the key).
The built-ins point at it with `"$schema": "./workflow.schema.json"`; common steps have `steps/common-step.schema.json`.

## Built-in workflows

| `id` | For |
| --- | --- |
| `feature` | A ticket from understanding to an approved pull request, implemented in an interactive session. |
| `bugfix` | Reproduce, diagnose, fix with a regression test, verify, open a PR. |
| `fix-until-green` | The AI fixes and re-runs the tests until they pass; you check it and can send it back round. |
| `epic` | Break a large ticket into sub-tickets (filed in Jira or GitHub), run `feature` on each in turn as a child run (parameter `child-workflow`), then the [`code-review` panel](#reviewers) checks how the pieces fit together. |
| `hotfix` | The fast path for a production problem: root cause, smallest safe fix with a regression test, a [`code-review` panel](#reviewers) (sent back to the fix if needed), follow-up tickets for the proper fix, and a release and rollback plan. |
| `feature-flow` | A piece of work (a ticket, or just a name) from intent to merged, reviewed tickets. 1. **Intent**: you fill in a form (`INTENT.md`). 2. **Research**: the AI reads the existing docs (`RESEARCH.md`). 3. **Specs I**: a first draft of `SPECS.md`. 4. **Spikes and prototypes**: the AI lists what is still unknown as research spikes (`spike-NNN-*.md`) and throwaway prototypes (`prototype-NNN-*.md`) in `SPIKES.md`, or says `NONE`; you approve what gets built. 5. Each spike is researched (`ticket-spike`) and each prototype built on its own unmerged branch (`ticket-prototype`), in parallel, each leaving a `<ticket>_REPORT.md`. 6. **Findings**: the AI sums up the reports in `FINDINGS.md` and you accept or reject the prototypes; the work then goes back to research, round and round until there is nothing left to find out (at most 4 research rounds, then it moves on). 7. **Specs II**: the final `SPECS.md`, which folds in every finding and settles the open questions, refined in a question loop until nothing is left to ask. 8. **Tickets**: numbered `<kind>-NNN-<name>.md` tickets with a `TICKET_ORDER.md`. 9. **Build**: the tickets wave by wave, each through implement → review. 10. **[Optional steps](#optional-steps)** you pick when the run starts: a security review of everything merged (`security`, on by default; skipped anyway when the research finds no security impact), `harden` (on), a release plan `deploy` (off), `observe` (off), opening the PR `pr` (on) and a `retro` that updates `CLAUDE.md` (off). Every document goes in the [scratch directory](#the-scratch-directory); you can give feedback on each planning step. Parameters: `style` (the [skill](#skills) the AI steps use, default `caveman`; empty for none), `kinds` (default `feature, bug, chore`) and `reviewers` (default `standards, specs, style`; see [reviewers](#reviewers)). |
| `debug-flow` | The same shape for a bug: the intent asks for symptoms, expected behaviour and how to reproduce, research forms root-cause hypotheses, prototypes are reproductions and experiments, and tickets default to `bug, chore`. It has the same optional steps. |
| `research` | Answers a question or settles a set of unknowns without changing your code. 1. **Question**: you say what you need to know, why it matters, what you already know, any constraints and whether to use spikes, prototypes or both (`INTENT.md`). 2. **Research**: the AI reads the docs and code and answers as far as they go (`RESEARCH.md`). 3. **Spikes and prototypes**: what's still open becomes spikes and prototypes in `SPIKES.md` (or `NONE`), which you approve; they run in parallel as in `feature-flow`. 4. **Findings**: you accept or reject the prototypes in `FINDINGS.md`, and the work loops back to research (at most 4 rounds). 5. **Answer**: `ANSWER.md`, with the answer, how sure it is, the evidence, each prototype's branch and the next steps, for you to approve or send back. Parameter: `style`. |
| `architecture` | Explores the options for an architecture decision and recommends one by your preferences. 1. **Preferences**: you describe the decision and pick what matters (simplicity, performance, cost, team familiarity and so on), rank or weight them, and list technologies you like, ones to avoid, hard constraints and expected scale (`INTENT.md`). 2. **Research**: the AI maps the architecture as it bears on the decision (`RESEARCH.md`). 3. **Options**: distinct options, including keeping or extending what's there, each with a sketch, code examples in this codebase, strengths, weaknesses, effort, fit against each preference and the unproven claims it rests on (`OPTIONS.md`, for you to approve). 4. **Spikes and prototypes** check the claims that could change which option wins, and the findings loop back to the options (at most 4 rounds). 5. **Evaluation**: a weighted score of each option against your preferences, with a sensitivity check (`EVALUATION.md`). 6. **Recommendation**: an ADR (`RECOMMENDATION.md`) for you to approve or send back. 7. The optional step `adr` (off by default) saves it as an ADR in the repo. Parameters: `style` and `options` (how many options, default `3 to 5`). |
| `shape` | Works out the high-level shape of something new (an app, a package or a feature): its main parts, their boundaries, how data and control flow, and where change lands. Where `architecture` settles one decision in an existing system, `shape` designs the whole of a new thing from patterns, prior art and your own habits. 1. **Brief**: you describe what it is, who uses it, what matters (picked and weighted as in `architecture`), what's likely to change, hard constraints, scale, your other code (folders on this machine, one per line) and your code online (GitHub users, organisations or repos) (`INTENT.md`). 2. **Four researchers, side by side**: this codebase (`RESEARCH.md`, via `grill-docs`), the general patterns and anti-patterns for this kind of thing (`PATTERNS.md`, with web search), similar libraries, projects and features and whether to use or borrow from them (`PRIOR_ART.md`, with web search), and your habits, read from this repo, the folders you listed (given to that step with [`directories`](#documents-optional-tools-other-folders-and-tickets), read-only) and your GitHub through read-only `gh` (`PRACTICES.md`). 3. **Shapes**: distinct candidate shapes, each with a mermaid sketch, code examples in your style, its parts, where each likely change lands, what it's built on, its fit with your practices and the unproven claims it rests on (`SHAPES.md`, for you to approve). 4. **Spikes and prototypes**, where a prototype is usually a walking skeleton of one shape, and the findings loop back to the shapes (at most 4 rounds). 5. **Comparison**: a weighted score against your priorities, each likely change walked through each shape, and a sensitivity check (`COMPARISON.md`). 6. **Recommendation**: the design to build from (`SHAPE.md`: parts, seams, key decisions, what's left out on purpose, first steps), for you to approve or send back. 7. The optional step `save` (off by default) saves it in the repo's design docs. Parameters: `style` and `shapes` (how many shapes, default `3 to 4`). |
| `ticket-spike` | Used by the flows above: researches one spike ticket (read-only, with web search) and writes its `<ticket>_REPORT.md`. |
| `ticket-prototype` | Used by the flows above: builds one prototype ticket, then writes its `<ticket>_REPORT.md` with a "How to try it" section naming its branch. |
| `ticket-wave` | Used by `feature-flow` and `debug-flow`: implements a comma-separated list of tickets (`tickets`) at the same time, each with `ticket-implement` in its own git worktree. |
| `ticket-implement` | One ticket in a fresh session: implement, then the [`code-review` panel](#reviewers) with every reviewer in `reviewers` (one subagent each); any `VERDICT: CHANGES` sends it back to implement (at most 4 passes), and it ends with a `<ticket>_REPORT.md`. |
| `reviewer` | One reviewer, by `lens`: runs the common step `review-<lens>`. The built-in lenses are `standards` (unwritten conventions, design smells, judgement), `specs` (does it do what was asked, and no more), `style` (the written style guides) and `security` (how the change could be exploited). Ends with `VERDICT: PASS` or `VERDICT: CHANGES`. |

The two flows' build runs one wave after another and the tickets in a wave in parallel. To force everything to run
one at a time, start the run with `--sequential` (see [Parallel paths](#parallel-paths-and-what-a-step-waits-for)).

## Steps

Each step has a `type` (`ai` when unset):

| `type` | What happens |
| --- | --- |
| `ai` | The AI runs `prompt` headless. |
| `interactive` | An interactive `claude` session opens with `prompt`. You mark the step done when you're finished, and the AI summarises the session as the step output. |
| `user` | You follow `instructions` and mark the step done (optionally with a note, which becomes the output). |
| `workflow` | Starts one or more child runs of another workflow and (by default) waits for them; see [Child runs](#child-runs-and-fan-out). |

afl also has an internal `author-draft` type, used only by the AI helper that drafts skills, common steps and
workflows (`afl author-new`, **New with AI…**). It isn't in the schema or the builder, and a workflow or common step
that uses it is refused.

Workflows from before schema 3.0 used `actor` and `mixedMode` instead of `type`. afl won't load them; it names the
file and says how to update it (`mixed` + `approve` is now `"type": "ai", "advance": "approve"`, and `mixed` +
`interactive` is `"type": "interactive"`).

Any step can set `advance`: what happens once its work is done.

- `auto` (the default): the run moves on.
- `approve`: the step waits for you to approve its output or send it back with feedback. Sending back redoes
  it: an AI step re-runs in the same conversation with the feedback, a `user` step reopens with the feedback
  under its instructions, an `interactive` step reopens its session.

Any step that has run can also be replayed later, even on a finished run (`afl run replay`, or Replay… on the UI's
Runs page): the run rewinds to it, its earlier attempts are kept in its history, and it and every later step run
again. Feedback is optional; with it an AI step continues its conversation, without it the step starts fresh.

Optional fields for AI steps are:

- `permissionMode`: `acceptEdits` (the default), `default` (read-only when headless), or `bypassPermissions`.
  `plan` is allowed only on `interactive` steps: headless, Claude writes a plan file outside the repo and can't
  leave plan mode.
- `allowedTools`: tools the AI can use without asking, e.g. `["Bash(dotnet test:*)"]`.
- `model`: which model to use; defaults to the agent's own `model`, then the tool's default.
- `agent`: which AI agent does the step (an id from `afl agent list`, e.g. `codex`). Unset, the step uses the
  run's agent (`afl start --agent`, default `claude`). Agents are defined per machine in `~/.afl/agents.json`,
  never in a workflow or repo. An agent that ignores `permissionMode`/`allowedTools` uses its own settings, and the
  run gets a note saying so; one that can't continue a conversation gets the earlier output and feedback
  in a fresh prompt when a step is sent back.
- `agents`: two or more agent ids that each do the step, side by side, instead of one `agent` (not on
  `interactive` or `user` steps). They work in the same checkout, so this suits read-only work (plans, reviews,
  answers) best; use `"permissionMode": "default"` unless you mean them all to edit. Each agent's own result is
  available to later steps as `{{steps.<id>.candidates.<agent>}}`. If some agents fail, the rest carry on; if all
  fail, the step fails. Sending the step back or replaying it reruns every agent.
- `combine`: how their results become the step's output.
  - `choose` (the default): the step waits (`awaiting-choice`) for you to use one result (editing it if you like,
    `afl run choose`), have an agent merge them (`afl run merge`), or send it back.
  - `merge`: the merge agent merges them straight away.
  - `all`: every result is kept, one after another under its agent's heading.
- `mergeAgent`: the agent that merges them; the step's first agent by default.
- `mergePrompt`: the prompt that merges them, where `{{candidates}}` is every result under its agent's heading.
  Unset, it is the step's prompt followed by merge instructions and the results.

A chosen or merged result still goes through the step's `advance`, so `"advance": "approve"` lets you review it.

```json
{ "id": "plan", "name": "Plan", "type": "ai", "prompt": "Plan a fix for {{ticket.title}}",
  "agents": ["claude", "codex"], "combine": "choose", "mergeAgent": "claude", "permissionMode": "default" }
```

### Choosing agents by what they can do

A workflow that names `agent` ids works only where those agents are set up. A step can instead say what it needs,
and afl picks from the agents installed on the machine running it (format 3.1; 3.0 files load unchanged):

- `capabilities`: what the step needs, e.g. `["review", "read-only"]`. An agent qualifies when it's available here
  (its command is installed, or its MCP server is registered) and has every one of them. Capabilities are the
  built-in ones (`afl capability list`: `edit-code`, `run-commands`, `read-only`, `review`, `plan`, `test`, `web`,
  `long-context`, `fast`, `cheap`, `local`, `interactive`, `resume`, `permissions`, `mcp-tools`) or your own tags
  (`afl capability add`, or the app's Settings). Agents declare theirs in `~/.afl/agents.json`; some come from the
  agent's kind (e.g. `resume` for agents that can continue a conversation). With `capabilities` set, `agent` becomes
  a preference: used when it's available, otherwise afl chooses. A run can't start if no installed agent qualifies;
  the error lists what's missing.
- `select`: how afl chooses when several agents qualify. `ai`: the run's agent reads the step and each candidate's
  description and capabilities and picks one, saying why (when the run's agent is the calling Claude session, the
  step waits at `choosing-agent` for it, or you, to pick with `afl run use-agent`). `first`: the first in your
  preference order (`afl settings set agent-preference …`). Unset, the global setting (`default-selector`) applies.
  You can always pick yourself: `afl run use-agent`, or the Runs page while the step is choosing.
- `delegate`: `true` lets the AI hand self-contained sub-tasks to other available agents while it works (`ai` steps
  done by one agent). afl lists those agents in the prompt; the AI ends its reply with an `afl-delegate` block, afl
  runs the sub-tasks side by side (each in `<step>.delegate-N.log`, with the step's permission mode) and hands the
  results back to the AI in the same conversation. At most 3 rounds per attempt.
- `delegateCapabilities`: capabilities every agent the AI hands work to must have.
- `mcpServers`: MCP servers (ids from `afl mcp-list`) whose tools the step's agent gets (agents that can take MCP
  servers: `claude`, and `cli` agents with `mcpArgs`). Server secrets stay in the secure store; afl writes them to a
  private, short-lived config file only while the agent runs.

```json
{ "id": "review", "name": "Review", "type": "ai", "prompt": "Review the change for {{ticket.key}}",
  "capabilities": ["review", "read-only"], "agent": "claude", "select": "ai",
  "delegate": true, "delegateCapabilities": ["read-only"], "mcpServers": ["docs"], "permissionMode": "default" }
```

### Documents, optional tools, other folders and tickets

Four more step fields:

- `artifact`: a file in the repo the step's output is saved to once the step is done, e.g.
  `"docs/afl/{{ticket.slug}}/DESIGN.md"` (template variables allowed; `ticket.slug` is the ticket key as a safe
  file name, e.g. `acme-app-12`). Later steps read its path as `{{steps.<id>.artifact}}`, so they can point the AI at
  the document instead of pasting it. The path must stay inside the repo (not absolute, no `..`, not in `.git`); one
  that renders outside it, or can't be written, is noted on the step rather than failing it. Each replay or loop
  overwrites the file. Tell the AI its reply is saved as the document, so it writes the document rather than a
  chatty summary.
- `optionalMcpServers`: like `mcpServers`, but nice-to-have: each server is given to the step's agent when it's
  set up on this machine and the agent can take servers; otherwise the step runs without it, with a note. Unlike
  `mcpServers` they never stop a run from starting or narrow which agents qualify. A server can't be in both lists.
- `directories`: folders outside the repo the step's agent may also work in, such as your other projects for it to
  learn from. Each entry is a template and may render to several folders, one per line (so a `multiline` input can
  list them: `"directories": ["{{steps.brief.inputs.look}}"]`); `~` is your home folder, a relative path is from the
  repo, and a folder that doesn't exist is skipped with a note. The agent's permission mode and `allowedTools` still
  apply there, so a read-only step only reads them.
- `createTickets`: `true` lets the AI file tickets while it works (`ai` steps done by one agent), e.g. follow-up
  work it finds out of scope. It ends its reply with an `afl-ticket` block (`{"tickets": [{"title", "body",
  "labels", "type": "bug" | "feature" | "task"}]}`); afl files them, records each on the step (key, link, or why
  it couldn't be filed and where to file it by hand), and the AI carries on with the keys. At most 10 per step.
  Tickets go to the run's ticket source (the same GitHub repo or Jira project as the ticket), else the repo's
  linked Jira project (`afl jira project add`), else its GitHub repo. Jira issue types are mapped from `type` (bug →
  Bug, feature → Story, task → Task; Task when the project lacks one). GitHub needs a token: `GH_TOKEN`,
  `GITHUB_TOKEN`, or `gh auth login`; afl reads it when filing and never stores it.

```json
{ "id": "security", "name": "Security review", "type": "ai", "permissionMode": "default",
  "prompt": "Review the change for security issues. Your whole reply is saved as the report.",
  "artifact": "docs/afl/{{ticket.slug}}/SECURITY.md",
  "optionalMcpServers": ["semgrep"], "createTickets": true }
```

Steps can ask the user questions with `inputs`. Each input's `when` says when it's asked:

| `when` | Asked | Allowed on |
| --- | --- | --- |
| `before` | Before the step's work starts (every time it starts, including loops; a retry keeps the answers). Its own prompt can use them as `{{inputs.<id>}}`. | Any step. |
| `after` (default) | When the step is let through: approved, or marked done when it doesn't wait for approval. | Steps a person lets through: `advance: approve`, `user` and `interactive`. |
| `ai` | Only if the AI decides it needs the answer, part-way through its work. Never asked otherwise (no answer: empty text, and an `input` condition on it doesn't hold). | `ai` steps done by one agent. |

An `ai` step done by one agent can also set `"ask": true` to let the AI ask its own questions (`text`, `choice`,
…), answered as `ask-1`, `ask-2`, … in `steps.<id>.inputs`. The AI asks by ending its reply with an `afl-ask`
block; afl tells it how. The run then waits (`awaiting-input`) and, once answered, the AI carries on in the same
conversation (or, for agents that can't resume, from a prompt with its earlier output and the answers). It may
ask at most 3 times per attempt; past that, or if its block can't be read, the step finishes with its output
and a note saying so.

Questions are asked by the CLI prompt (`afl run answer`, `approve` or `done`), the UI's form, or the `/afl`
skill, and must all be answered before the run continues.

Questions that don't block the step are left at the end of its output instead. Every AI step that waits for
approval (`advance: approve` or `approve-final`) is told to end its reply, when it has any, with a last
`## Open questions` section: a numbered list, each question in bold followed by the assumption the AI made. The
Runs page shows an answer box for each one, and `afl run reject <run> --answer 2="..."` answers them from the CLI;
the step is redone with the answers as feedback, and an unanswered question keeps its assumption. You don't need to
ask for this in a prompt; if a document template has an `## Open questions` section, keep it as the last one. A `user` step needs `instructions`, `inputs`, or both.

```json
"inputs": [
  { "id": "scope", "label": "How much should it cover?", "type": "choice",
    "options": ["Just the toggle", "Toggle and settings page"], "default": "Just the toggle" },
  { "id": "notes", "label": "Notes for the implementer", "type": "multiline", "required": false },
  { "id": "area", "label": "Which part of the app?", "when": "before" },
  { "id": "style", "label": "Match the existing style or modernise?", "type": "choice",
    "options": ["Match", "Modernise"], "when": "ai" }
]
```

| Field | Meaning |
| --- | --- |
| `id` | Lowercase letters, digits and dashes; unique within the step. |
| `label` | The question. |
| `type` | `text` (default), `multiline`, `number`, `choice`, `multichoice`, or `confirm` (yes/no). |
| `options` | The choices, for `choice` and `multichoice` only. `multichoice` options can't contain commas. |
| `required` | Defaults to `true`. An optional input left blank has no answer (an empty string in templates). |
| `default` | Used when no answer is given. It may use template variables (e.g. `{{steps.plan.inputs.scope}}`, or `{{inputs.<id>}}` for this step's `before` inputs in an `after` or `ai` input), rendered when the question is asked; one that renders blank is no default. |
| `help` | Shown under the question. |
| `when` | `before`, `after` (default) or `ai`; see above. |

Answers are stored in canonical form: `choice` as the option's own spelling, `multichoice` as the picked options
in option order joined with `, `, `confirm` as `yes` or `no`. A `user` step's output is its note followed by one
`Label: answer` line per answer.

Headless steps can't answer permission prompts, so what a step's `permissionMode` and `allowedTools` don't cover is
refused. What happens next is the run's permission policy (see Permissions below).

## Permissions

How a run's agents get permission to run commands is chosen per run, not per workflow: `afl start --permissions
ask|strict|auto`, the UI's start dialog, or the default in Settings → Permissions (`ask` or `strict`; full auto is
never a default).

| Policy | When an agent is refused something |
|---|---|
| `ask` (the default) | The step stops at **Needs permission**, listing each refused call (the command, URL or file) with suggested rules, most specific first: e.g. `Bash(dotnet test tool/Afl.slnx)` then `Bash(dotnet test:*)`. For each, allow it **once** (the next turn only), **always** (saves the rule for the run, the repo or every repo) or **deny** it; afl then resumes the same conversation and the agent retries. "Accept output as is" keeps what it did instead. After 5 rounds the step stops asking and notes the refusals. |
| `strict` | Nothing pauses: the step carries on without what was refused, with a note naming it. |
| `auto` | Full auto: every agent step except `plan`-mode ones runs with `bypassPermissions`, after a warning. Only the guard rails are refused. |

Saved rules are Claude Code permission rules (`Tool` or `Tool(specifier)`, e.g. `Bash(npm test:*)`,
`WebFetch(domain:github.com)`, `Edit(src/**)`, `mcp__jira__search`), allow or deny, kept on the run, on the repo or
globally (`~/.afl/permissions.json`). Allow rules are added to every step's `allowedTools`; deny rules go to
`--disallowedTools`, which Claude Code applies even under `bypassPermissions`. The **guard rails** are deny rules
every run gets under every policy: by default `git push`, `sudo`, `rm -rf /` or `~`, and reading `~/.ssh` or
`~/.afl` (editable in Settings, with Reset to defaults).

Pausing covers single-agent `ai` steps (and `use:` steps that resolve to one). Multi-agent steps and child runs
get the saved rules, guard rails and full auto, but keep the note on refusal; `interactive` steps ask in their own
terminal. Agents that ignore `permissionMode`/`allowedTools` ignore the policy too.

```
afl start <ticket> --permissions ask|strict|auto [--yes-auto]
afl run permit <run> [--step <id>] --once N[=rule] | --always N[=rule] [--scope run|repo|global] | --deny N
               | --all once|always|deny | --accept-output
afl permission list | add <rule> [--deny] [--scope repo|global] | remove <rule> [--scope …]
afl permission guards [--add <rule>] [--remove <rule>] [--reset]
```

## Tags and search

Workflows, common steps and skills can have `tags` (format 3.5 or later for workflows, 1.1 for common steps and skills): up
to 20 lowercase words, digits and hyphens, such as `review` or `planning`. The built-ins are tagged.

```json
"tags": ["review", "security"]
```

In the UI, each library page has a search box, a **Contents** box, a scope filter, and a button per tag (with its
count) above the list. Selecting tags shows only documents with all of them. The search needs every word to be in
the id, name, description or tags. With **Contents** ticked, a word may instead be in the prompts, instructions or
files, and the list shows the line it was found on. Matches by name come first. The CLI does the same:
`afl workflow list | step list | skill list` take `--tag` (repeatable), `--search <text>`, `--names-only` and
`--scope`. The Tags field in the draft takes a comma-separated list, normalized when saved (`Big Change` →
`big-change`).

## Common steps and composition

Schema 3.3 adds four ways to build on steps and workflows that already exist. `use`, `include` and `requires` are
resolved when a run is created: the run keeps the fully expanded workflow, so `afl run show`, the Runs page and
every template see ordinary steps. `afl workflow show <id> --expanded [--param id=value]` prints what a run would
follow.

### Parameters

A workflow or common step can declare `parameters`. Each has an `id`, an optional `label` and `description`, and a
`default`; one with no `default` is required. `{{params.<id>}}` is replaced as text wherever it appears, before the
run starts, so a value can itself be a template that is rendered later (e.g. `"{{steps.triage.output}}"`).

Values come from `with` wherever the thing is used (a `use` or `include` step, a `workflow` step, a requirement), or
for the workflow a run starts from `afl start --param id=value` (repeatable) or the fields the UI asks for when you
start it. Validation reports a required parameter with no value, a `with` key the target doesn't declare, and a
`{{params.x}}` the definition doesn't declare.

### Common steps: `use`

A common step is a file holding one step and the parameters it takes:

```json
{ "schemaVersion": "1.0", "id": "lint-review", "name": "Lint review",
  "parameters": [{ "id": "focus", "label": "What to look for", "default": "bugs and missing tests" }],
  "step": { "id": "review", "name": "Review the change", "type": "ai", "permissionMode": "default",
            "prompt": "Review the uncommitted changes. Look for {{params.focus}}." } }
```

A workflow step uses it with `use`, fills its parameters with `with`, and can override any other field:

```json
{ "id": "review", "use": "lint-review", "with": { "focus": "unused imports" }, "advance": "approve" }
```

The common step is copied with its parameters filled in, then every field the using step sets replaces the common
step's; empty or unset fields inherit, and lists replace rather than merge. `type` always comes from the common
step, and `id` defaults to the common step's own. The built-ins are:

- `code-review`, the [reviewer panel](#reviewers) (`hotfix`, `epic` and `ticket-implement` use it), and the
  reviewers `review-standards`, `review-specs`, `review-style` and `review-security`;
- `harden`, `release-plan`, `observe`, `open-pr` and `retro`, the later lifecycle steps (`feature-flow` and `debug-flow` use them, and
  `feature-flow` and `debug-flow` as [optional steps](#optional-steps)); each takes `docs`, the directory the work's
  documents are in (default `docs/afl/<ticket>`);
- `grill-docs`, `to-specs`, `to-spikes`, `review-findings`, `specs-review` and `to-tickets` (`feature-flow` and
  `debug-flow` use them, and `research`, `architecture` and `shape` use the first, third and fourth; each takes `work`, the wording for the kind of work).
  `to-spikes` and `review-findings`, like the `ticket-spike` and `ticket-prototype` workflows, also take `brief`: the
  document in the scratch directory the spikes serve (default `SPECS.md`; `research` uses `RESEARCH.md` and
  `architecture` uses `OPTIONS.md` and `shape` `SHAPES.md`).

 `afl step list|show <id>|validate <file>` work like their workflow
counterparts, and `afl step new <id> --from-workflow <wf> --step <step> [--in-repo]` saves an existing step as a
common step.

### Reviewers

Code review is done by the `code-review` common step, a `workflow` step that runs one reviewer per name in its
`reviewers` parameter (default `standards, specs, style`) at once, each as its own `reviewer` child run, i.e.
its own subagent. Its output has each reviewer's findings under its own heading, so a loop arrow can send the work
back while any of them says `VERDICT: CHANGES`. `hotfix`, `epic` and `ticket-implement` use it (`feature-flow`,
`debug-flow` and `ticket-wave` pass their `reviewers` through to `ticket-implement`). Its other parameters are
`scope` (what to review and where to look), `context` (what the change is meant to do), `extra` (anything else every
reviewer should check) and `style` (a skill for the reviewers):

```json
{ "id": "review", "name": "Review", "type": "workflow", "use": "code-review",
  "with": { "reviewers": "standards, specs, style, security", "extra": "This is a hotfix: keep it small." } }
```

A reviewer is just a common step named `review-<name>` with `scope`, `context` and `extra` parameters, whose output
ends with `VERDICT: PASS` or `VERDICT: CHANGES`; the built-in ones are `standards`, `specs`, `style` and
`security`. To add one, say a performance reviewer, save a common step `review-performance` (globally or in the repo;
copy `review-standards` with `afl step show review-standards --json` as a start) and add `performance` to
`reviewers`. `review-security` also works on its own as a step, e.g. as a [conditional step](#conditional-steps) that
only runs when a change touches security. Validation checks that each listed reviewer has a common step.

### Including a workflow: `include`

```json
{ "id": "sec", "name": "Security pass", "include": "security-pass", "with": { "area": "auth" } }
```

The included workflow's steps (expanded, with its parameters filled in) take the place of the `include` step. Their
ids become `<include id>-<step id>` (so `sec-review`) and their names "<include name>: <step name>", and every
reference among them (`{{steps.<id>.…}}`, arrows' `to`, `waitFor`, a condition's `step`) is rewritten to match.
Arrows into the include step go to the included workflow's `start`, and every step that ends the included workflow
(including `"to": "end"` inside it) takes the include step's own `arrows` instead (or ends its path when it has
none). In the including workflow, `{{steps.<include id>.output}}` and `.artifact` mean its last step, and an arrow
`"to": "<include id>"` its start.

### Child runs and fan-out

A `workflow` step starts separate runs of another workflow, each with its own steps, approvals and history:

| Field | Meaning |
| --- | --- |
| `workflow` | The child workflow's id (may use `{{params.…}}`). |
| `with` | Values for its parameters; `{{item}}` is the ticket key or line the child is for. |
| `forEach` | Where the children come from, `{ "kind": …, "step": …, "tickets": […] }`; unset is one child on this run's ticket. |
| `children` | `wait` (the default): the step waits (`awaiting-children`) until every child is done. `start`: it finishes once they've started. |
| `childrenTogether` | `true` drives the children at the same time instead of one after another. With the default `shared` workspace they share the checkout, so keep that for read-only work, or give each its own `workspace`. A `--sequential` run drives them one at a time anyway, and `--max-parallel n` at most n at once. |
| `workspace` | Where the children work: `shared` (the default, this run's checkout) or `worktree` (see below). |
| `mergeBack` | With a `workspace` of their own: `true` (the default) merges each finished child's work back; `false` keeps it on its branch, unmerged (for spikes and experiments). |
| `reuse` | `true` reuses a run of the same workflow on the same ticket: a finished one as it is, an unfinished one by waiting for it. |
| `childOutput` | The child step whose output becomes this step's (default: its last step with output). |
| `agent` | The child runs' main agent (default: this run's). |

`forEach.kind` is one of:

| Kind | One child per |
| --- | --- |
| `ticket` | This run's ticket (the default). |
| `tickets` | Ticket in `tickets` (keys or URLs; templates allowed). |
| `created-tickets` | Ticket filed by the earlier step `step` (`createTickets`). |
| `lines` | Non-empty line of the earlier step `step`'s output, all on this run's ticket. With `pattern` (a regex) only matching lines count, and each gives its first capture group (or the whole match); with `separator` each value is split again, e.g. `"pattern": "^Wave \\d+: (.+)$", "separator": ","`. |
| `items` | Value in `items`, a comma- or newline-separated list (templates allowed, e.g. `"{{params.tickets}}"` or `"standards, specs, style"`), all on this run's ticket. |

A step starts at most 20 children, and runs nest at most 4 deep. With one child the step's output is the child's;
with several it is each child's under a `## <ticket> (<run id>)` heading, so `artifact`, conditions and loops work as
usual. When a child waits on you (an approval, an input), the parent waits too: finish the child (`afl run show
<child>`, or Open on the Runs page) and the parent carries on by itself once its children are done. A failed child
fails the step; retrying it (`afl run retry`) retries the failed children, while replaying it starts new ones. Cancelling a run cancels the children
it started.

```json
{ "id": "each", "name": "Work on each area", "type": "workflow", "workflow": "feature",
  "forEach": { "kind": "lines", "step": "plan" }, "with": { "area": "{{item}}" } }
```

#### Worktrees

With `"workspace": "worktree"` each child gets its own git worktree under `~/.afl-worktrees/<run id>` (beside the afl home,
which the guard rails keep agents out of), on a new branch `afl/<ticket slug>/<item>` from the parent checkout's current
`HEAD`, made just before the child first runs (so children run one after another each start from the work merged
before them). Its agents work there, and so do
the child's own child runs (a ticket's reviewers look at its worktree). The [scratch directory](#the-scratch-directory)
stays in the main checkout and is given to the agents as an extra directory.

Once a child is done afl commits its changes (`<item>: <ticket title> (afl run <id>)`), merges the branch into the
parent checkout's current branch with `--no-ff`, and removes the worktree and branch; with `mergeBack: false` the
branch is kept. A merge that conflicts is aborted and fails the step, naming the branch and worktree: resolve it with
`git merge <branch>` yourself and retry the step (`afl run retry`), or just retry to let afl try again. Cancelling a
run removes its children's worktrees but keeps their branches.

```json
{ "id": "build", "name": "Build each ticket", "type": "workflow", "workflow": "ticket-implement",
  "forEach": { "kind": "items", "items": "{{params.tickets}}" }, "with": { "ticket": "{{item}}" },
  "childrenTogether": true, "workspace": "worktree" }
```

### Dependencies: `requires`

```json
"requires": [{ "workflow": "feature", "with": { "area": "the API" } }]
```

Each requirement becomes a leading `workflow` step, `requires-<workflow>`, with `reuse: true`: a finished run of that
workflow on the same ticket is used as it is, an unfinished one is waited on, and otherwise one is started.

Cycles (through `include`, `workflow` steps or `requires`) and nesting more than 5 levels deep are rejected when the
workflow is validated or saved.

## Skills

Format 3.4 adds common skills: instructions an AI step's agent follows, such as `caveman` (terse replies to save
tokens; the documents a step asks for stay complete). A step lists them in `skills`, and a workflow's own `skills`
are added to each of its `ai` and `interactive` steps (including included ones). Empty entries are dropped and an
unknown skill fails validation. A step can leave out some of the workflow's skills with `skipSkills`, e.g.
`"skipSkills": ["caveman"]` on a step that writes a document for people (`feature-flow` does this on its specs
steps, so the specs aren't written in `caveman`).

### Picking skills at start

A workflow's `skills` are the ones **picked to start with**: whoever starts a run can change them. The UI's start
dialog lists them, each with Remove, above a dropdown of every other skill with Add, and shows the skills
recommended for the repo with a check box each (ticking one adds it). From the CLI, `afl start --skills a,b`
replaces them (`--skills ""` for none), and `--add-skills`/`--drop-skills` adjust them. The pick is kept as
`Run.Skills` (null keeps the workflow's own), replaces the workflow's skills in every step (steps' own `skills` and
`skipSkills` still apply), and reaches child runs and paths.

### Recommended skills

A skill can say what kind of repo it suits with a `recommend` block (skill format 1.2):

```json
"recommend": {
  "files": [{ "glob": "pyproject.toml", "contains": "fastapi" }],
  "languages": ["python"], "frameworks": ["fastapi"],
  "reason": "FastAPI service"
}
```

afl takes a quick look at the repo (`git ls-files`, or a capped walk that skips build and dependency folders;
nothing is run and only files up to 256 KB are read, for `contains`). A skill is recommended when one of its
`files` matches ("FastAPI service: found pyproject.toml"), or when the repo's stack has one of its `frameworks` (or,
for a skill naming none, one of its `languages`). The stack is what the matched skills name, plus what's saved on
the repo. A repo with nothing but docs, licences and dotfiles counts as empty: then afl asks what it will be built
with (languages and frameworks, offered from every skill's `recommend`, any other can be typed) and saves the answer
on the repo, editable in Repo settings → Project. General skills (`caveman`, `test-first`, …) have no `recommend`;
workflows pick those. Recommendations are only offered: nothing is added unless it's ticked or passed with `--skills`.

```
afl skill recommend [--workflow <id>] [--language a,b] [--framework x,y] [--clear-stack] [--json]
```

Skills are layered like workflows: built-in (`workflows/skills/*.json`), then `~/.afl/skills/<id>.json`, then
`<repo>/.afl/skills/<id>.json`. A skill file has an `id`, `name`, `description`, `content` (the SKILL.md body, in
markdown) and optional `files` (relative path to text, for supporting files); `skill.schema.json` describes it.
Claude gets them as a session plugin (`afl-skills:<id>`) and is told to invoke them; other agents get each skill's
content in the prompt.

```
afl skill list | show <id> [--json] | validate <file>
afl skill new <id> [--in-repo] | delete <id> [--in-repo]
afl skill import <SKILL.md or folder> [--in-repo] | export <id> <dir>
```

The UI's Skills page edits them.

The built-in skills:

| Skill | What it's for |
|---|---|
| `caveman` | Terse replies to save tokens; the documents a step asks for stay complete. |
| `test-first` | A failing test before the change, favouring integration tests over real collaborators (and Gherkin scenarios where the repo has them), with unit tests for dense logic. |
| `bdd-scenarios` | Behaviour as Gherkin scenarios that business people can check, bound with Reqnroll (.NET) or pytest-bdd (Python); comes with an `example.feature`. |
| `review-checklist` | Reviewing a change: correctness, errors, security, tests, design and operations, with findings ranked by severity. |
| `commit-and-pr` | Small focused commits with Conventional Commit messages, and PR descriptions that say what, why and how it was tested. |
| `csharp-style` | C# conventions: nullable on, 0 warnings, file-scoped namespaces, `this.`, XML docs, and generic/abstract base classes over interfaces and tiny classes. |
| `async-correctness` | Async .NET: async all the way, `ConfigureAwait(false)` in libraries, never block on tasks, cancellation tokens, safe concurrency. |
| `python-env` | Python projects with uv: `pyproject.toml`, `uv.lock`, `uv add`/`sync`/`run`, never the system interpreter. |
| `typing-and-lint` | Typed Python: hints everywhere, ruff for lint and format, `ty check` clean. |
| `fastapi-service` | FastAPI services: app factory and lifespan, routers, Pydantic models, `Depends`, settings from the environment, integration tests with httpx against the real app. |
| `python-raw-sql` | Plain SQL from Python: SQLAlchemy only for the engine and pool, `text()` with bound parameters, rows mapped to Pydantic models, migrations as SQL files. |
| `testcontainers` | Integration tests against real Postgres, Redis and so on in throwaway containers (.NET and Python), one per session with clean state per test. |
| `playwright-e2e` | Browser end-to-end tests with Playwright (.NET and Python): role locators, waiting assertions, traces on failure; pairs with `bdd-scenarios`. |
| `mutation-testing` | Measuring test strength with Stryker.NET and mutmut, scoped to changed code, turning surviving mutants into assertions. |
| `csharp-refactoring` | Behaviour-preserving C# refactors: green before and after, symbol-aware renames, small steps, behaviour changes kept separate. |
| `test-smells` | Auditing tests for ones that can't fail, hide failures, flake, test the mocks or pin the implementation, as a ranked report. |
| `property-based-testing` | Properties over generated inputs with Hypothesis (Python) or FsCheck/CsCheck (.NET), keeping shrunk failures as examples. |
| `security-diff-review` | Security review of a change: trust boundaries, removed checks, injection, secrets, blast radius traced through callers. |
| `benchmarkdotnet` | Measuring .NET performance with BenchmarkDotNet: Release builds, a baseline, memory diagnostics, results with their error. |
| `msbuild-hygiene` | Reviewing project and build files: no hard-coded paths, incremental targets, private analyzers, portable conditions. |
| `shared-build-config` | `Directory.Build.props`/`.targets` and Central Package Management, including converting a solution safely. |
| `msbuild-binlog` | Capturing and reading binary logs to explain build failures, rebuilds and property or package surprises. |

## Work without a ticket

`afl start --name "dark mode" --workflow feature-flow` (the same as the ticket `local:dark mode`), or New work… on
the Issues page, starts a run on a ticket made from the name: its key and `ticket.slug` are the name as a slug
(`dark-mode`), its title the name, and its body empty. Local tickets can't be filed, so `createTickets` on such a
run files to the repo's linked Jira project or GitHub repo.

## The scratch directory

Documents that shouldn't be committed go in the scratch directory: `.scratch/<ticket slug>/` in the repo, set
globally with `afl settings set scratch-directory <dir>` or on the UI's Settings page (a run keeps the setting it
started with). It's made the first time a run uses a `scratch.` variable, and its root holds a `.gitignore` that
ignores everything in it, itself included, so it is never tracked and the repo's own `.gitignore` is left alone.

## More step fields (3.4)

- `outputTemplate`: the step's output, rendered from this template once its work is done, with its answers as
  `{{inputs.<id>}}` and its own output as `{{output}}`. A `user` step can turn a form into a document this way.
- `"advance": "approve-final"`: for a step with a loop arrow, move on by itself while the loop will go round again,
  and ask for approval only on the pass where it won't (its `until` holds or the limit is reached). Sending it back works as with
  `approve`.

```json
{ "id": "intent", "name": "Intent", "type": "user", "artifact": "{{scratch.dir}}/INTENT.md",
  "inputs": [{ "id": "goal", "label": "Goal", "type": "multiline" }],
  "outputTemplate": "# Intent: {{ticket.title}}\n\n## Goal\n\n{{inputs.goal}}" }
```

## Optional steps

A step with `optional` (format 3.7) is one whoever starts a run decides on: the UI asks with a checklist after the
parameters, and `afl start` takes `--include <ids>` and `--exclude <ids>` (comma-separated or repeated).
`afl workflow-show <id>` lists a workflow's optional steps and their defaults.

```json
{ "id": "retro", "use": "retro", "optional": { "default": false, "help": "Records the lessons in CLAUDE.md." } }
```

`default` (true when unset) says whether the step is included when nobody says otherwise; `help` is shown next to it
when the run starts. The choice is made on the expanded workflow, so optional steps from common steps and includes
count, and naming a step that isn't optional is an error. A left-out step is skipped when the run reaches it, with a
note saying so (the run's `leftOutSteps` lists them), and its output reads as empty to the steps after it.

## Flow: arrows, paths and loops

Format 5.0 workflows are a free graph of steps joined by **arrows**. The order steps are listed in only decides how
they're shown; where the run goes is said by each step's `arrows`:

- `start` (on the workflow): the step the run begins at. It defaults to the first listed step.
- `arrows`: where to go once the step ends, each `{ "to": "<step id>" | "end", … }`. A step none of whose arrows fire
  **ends its path**; there is no end step. `"to": "end"` ends the path on purpose (useful with `follow: first`).

Each arrow has:

| Field | Meaning |
| --- | --- |
| `to` | The step it goes to, or `end`. |
| `on` | Which ending fires it: `success` (the default), `failure`, `timeout` (a step that ran over its `timeout`; with no timeout arrow, its failure arrows fire) or `always`. A `failure` or `timeout` arrow **handles** the failure: the run carries on and can end done. An `always` arrow fires but doesn't handle it, like `finally`: the run carries on and ends failed. |
| `when` | A [condition](#conditions): the arrow is taken only when it holds. |
| `until` | For a loop: taken while the condition *doesn't* hold. Not with `when`. |
| `max`, `onLimit` | Only on an arrow that goes back (see [Loops](#loops)). |
| `label` | Shown on the arrow (in the graph and `afl status`) instead of its condition. |

A step may not have two arrows with the same `to` and `on`. `afl workflow validate` also warns about steps nothing
leads to.

### Following arrows: `follow`

Once a step ends, its arrows that go back (a loop) are checked first, in order, and the first that fires wins: the
step goes round again and none of its forward arrows are followed. Then the forward arrows, by the step's `follow`:

- `all` (the default): every arrow that applies is taken. Two or more start **parallel paths**.
- `first`: the conditional arrows are checked in order and only the first that holds is taken; when none holds, every
  unconditional one is. This is an if / else-if / else.

```json
{ "id": "triage", "name": "Triage", "type": "ai", "prompt": "Is it a bug or a feature? Say BUG or FEATURE.",
  "follow": "first",
  "arrows": [
    { "to": "reproduce", "when": { "kind": "output", "contains": "BUG" }, "label": "bug" },
    { "to": "design" }
  ] }
```

### Parallel paths and what a step waits for

```json
{ "id": "plan", "name": "Plan", "type": "ai", "prompt": "Plan it.", "arrows": [{ "to": "lint" }, { "to": "test" }] },
{ "id": "lint", "name": "Lint", "type": "ai", "prompt": "Lint it.", "arrows": [{ "to": "ship" }] },
{ "id": "test", "name": "Test", "type": "ai", "prompt": "Test it.", "arrows": [{ "to": "ship" }] },
{ "id": "ship", "name": "Ship", "type": "ai", "prompt": "Ship: {{steps.lint.output}} / {{steps.test.output}}" }
```

`lint` and `test` run at the same time, each as a hidden **path run** that runs one step and hands back. The run itself
does all the routing, so a path's step can wait for approval or input like any other. A step with forward arrows in
from two or more steps decides what it waits for with `waitFor`:

| `waitFor` | Starts when |
| --- | --- |
| `"all"` (the default) | Every arrow in that's still live has fired. |
| `"any"` | One arrow in has fired. |
| `"lint"` | The arrow from `lint` has fired. |
| `{ "all": [...] }`, `{ "any": [...] }` | A group of the above, nested as deep as you like. |
| `{ "atLeast": 2, "of": [...] }` | At least that many of the items hold. |

```json
{ "id": "ship", "name": "Ship", "type": "ai", "prompt": "Ship it.",
  "waitFor": { "any": [{ "all": ["build", "test"] }, "lint", { "atLeast": 2, "of": ["docs", "spec", "demo"] }] },
  "others": "finish" }
```

Once `waitFor` holds, `others` says what happens to the paths still heading only here: `cancel` (the default) cancels
them (noted "another path got there first"), `finish` lets them run to the end and ignores them. Every item of
`waitFor` must be a step with a forward arrow into this one; an arrow in that `waitFor` leaves out can never start it
(validation warns).

**Dead paths.** Each forward arrow into a step ends up *fired*, *dead* (its step finished without taking it, or will
never run) or still pending. Dead arrows are left out of `waitFor`: `all` waits only for the live ones, and a rule
that can no longer hold makes the step **not reached** and its own arrows dead in turn. That's how arrows that split
on conditions meet again without waiting forever. Once no path is running, every arrow still pending is dead, so a run
never deadlocks; it ends once every path has.

Steps after a step that waits for only some of its arrows should use the optional form for the others' output,
`{{steps.lint.output?}}`, which is empty when that step didn't run. Validation tells you when a step might not have
run on every way in.

On the step whose arrows split, `workspace` and `mergeBack` work as for child runs (see [Worktrees](#worktrees)):
`shared` (the default) has every path work in the run's checkout; `worktree` gives each its own, merged back where the
paths meet, in arrow order (a cancelled path's is discarded and its branch kept). A merge conflict fails the step;
fix it and retry. Lanes nest: a path that splits again with its own `workspace: worktree` gives each new path a worktree
branched from its own, merged back into it, and its lane stays open until those inner paths are merged. A step where
paths of one lane meet stays in that lane, so the work after a join inside a worktree carries on there; replaying a
step that ran in a path runs it in that path's worktree again. A `workspace` only makes sense on a step whose arrows
split the run (two or more going forward on the same outcome), and validation says so otherwise.
`afl start --max-parallel n` runs at most n paths (and child runs) at once, and `--sequential` (or the start
dialog's Single-threaded choice) one at a time.

A failed path doesn't stop the others. Cancel cancels them all. When more than one step is waiting on you,
`afl run show` lists them and the run commands need `--step <id>` to say which (`afl run approve <run> --step lint`).
The Runs page shows each path under its step ("⑂ path x").

### Conditional steps

There's no `skipWhen`. To run a step only sometimes, put the condition on the arrows into it and add a **bypass
arrow** past it with the opposite condition:

```json
{ "id": "research", "name": "Research", "type": "ai", "prompt": "…",
  "arrows": [
    { "to": "security", "when": { "kind": "not", "conditions": [{ "kind": "output", "contains": "SECURITY IMPACT: NONE" }] } },
    { "to": "build", "when": { "kind": "output", "contains": "SECURITY IMPACT: NONE" } }
  ] },
{ "id": "security", "name": "Security review", "use": "review-security", "arrows": [{ "to": "build" }] },
{ "id": "build", "name": "Build", "type": "ai", "prompt": "Build it. {{steps.security.output?}}" }
```

When the bypass is taken, the arrow into `security` dies, so `build` (waiting for `all`) doesn't wait for it. The
Workflows page does this in one go: right-click a step, **Make this step conditional…**.

### Loops

An arrow to the same step or an earlier one is a **loop**. Loops are checked before the forward arrows, in order:

- `max`: how many times the arrow's target may run (default 5). An arrow back to the step itself and one back to an
  earlier step keep separate counts.
- `onLimit`: at `max`, `fail` (the default) fails the step, so you can replay or fix it; `continue` doesn't take the
  arrow and goes on with the forward ones, with a note.

```json
{ "id": "fix", "name": "Fix", "type": "ai",
  "prompt": "Attempt {{loop.iteration}} of {{loop.max}}. Last time:\n{{steps.fix.previous}}\nEnd with RESULT: PASS or RESULT: FAIL.",
  "arrows": [
    { "to": "fix", "until": { "kind": "output", "matches": "(?m)^RESULT: PASS\\s*$" }, "max": 5 },
    { "to": "ship" }
  ] }
```

The earlier attempts of every step that runs again are kept (as with replay), and each records why it reran
(`Loop iteration 2`). `afl run show` and the Runs page show each step's `iteration n/max` and the path the run took,
e.g. `fix > fix #2 > check > fix #3`. Replaying a step resets the loop counts of the steps after it.

### Failures, retries and timeouts

- `retry` (on a step): `{ "max": 2, "delay": "30s" }` runs the step again, up to `max` times, waiting `delay`,
  before any failure arrow is considered.
- `timeout` (on a step): e.g. `"30m"`. A step that runs over is stopped and fails with a timeout, which fires its
  `on: timeout` arrows (or else its failure arrows).
- A failure no arrow fires on fails the run, as ever; fix it and `afl run retry`. `onFailure` on the workflow names a
  **catch step** such a failure goes to instead: it runs like an `always` arrow, so the run still ends failed. No
  arrow may lead into it.
- `maxSteps` on the workflow (default 100): once a run has done that many step runs in all, counting every loop and
  path, it fails, which stops a runaway spread across several loops.

### Conditions

A condition is an object with a `kind`:

| Kind | Holds when | Fields |
| --- | --- | --- |
| `input` | The answer to an input equals a value (ignoring case; `confirm` answers are `yes`/`no`). | `input`, `equals`, `step` |
| `output` | The output `contains` some text (ignoring case) or `matches` a regular expression. | `contains` or `matches`, `step` |
| `ai` | An agent answers yes to a yes/no `question` about the output. It runs read-only, and its answer is noted on the step. | `question` (can use template variables), `agent` (default: the step's), `step` |
| `all`, `any` | All / any of `conditions` hold. | `conditions` |
| `not` | Its one condition doesn't hold. | `conditions` |
| `always` | Always. | |

`step` is the step the arrow leaves by default, or an earlier one. An `ai` condition needs an agent afl runs itself
(not `session`), and one that can't be checked (e.g. the judge fails) fails the step. To let a person decide, give a
`user` step (or an `approve` step) a `confirm` or `choice` input and put conditions on its arrows:

```json
{ "id": "check", "name": "Check", "type": "user", "instructions": "Is it right?",
  "inputs": [{ "id": "good", "label": "Is the change right?", "type": "confirm" }],
  "arrows": [{ "to": "fix", "when": { "kind": "input", "input": "good", "equals": "no" } }, { "to": "ship", "when": { "kind": "input", "input": "good", "equals": "yes" } }] }
```

The built-in `fix-until-green` workflow puts conditions and loops together; `ticket-implement` loops implement and
review; `feature-flow` splits into spikes and prototypes side by side and makes its security review conditional.

### Editing the flow

The Workflows page draws the graph. Drag from a step's handle onto another step to add an arrow; click an arrow to
edit it (where it goes, what fires it, its condition, a loop's limits, its label), or drag the dot at its end onto
another step to repoint it. Each step's Flow section lists its **Arrows out** (with the Follow choice once it has two)
and, when two or more arrows come in, what it **Waits for** (All, Any or Custom, a group editor; conditions are one
level of `all`/`any` there, deeper nesting is shown read-only, to edit as JSON). Box-select or ⇧-click steps to move,
delete, copy, paste or duplicate them; ⌘Z / ⇧⌘Z undo and redo. A step moved by hand saves its place as `position`
(`{ "x": …, "y": … }`; runs ignore it), and **Tidy** lays the graph out again and clears them.

**Simulate** runs the flow without running any step: pick how each step ends (success, failure, timeout, left out)
and whether each conditional arrow is taken, and the graph shows which arrows fire or die, which steps run and how
many times, and how the run ends. It uses the run's own routing code, so what it shows is what a run does. The Runs
page's **Graph** tab draws a run the same way, live; clicking a step there finds it in the timeline.

## Template variables

`prompt`, `instructions` and `artifact` can use `{{variable}}`:

| Variable | Value |
| --- | --- |
| `ticket.key`, `ticket.title`, `ticket.body`, `ticket.url`, `ticket.source`, `ticket.status` | The ticket being worked on. |
| `ticket.slug` | The ticket key as a file-name-safe slug, e.g. `acme-app-12` or `web-42`. |
| `repo.name`, `repo.path`, `repo.github` | The repo. |
| `steps.<id>.output` | The output of an earlier step. |
| `steps.<id>.artifact` | The repo-relative file an earlier step's output was saved to (its `artifact`); empty if it has none or it wasn't saved. |
| `steps.<id>.candidates.<agent>` | One agent's own result on an earlier step done by several `agents`. |
| `steps.<id>.inputs.<input>` | The answer to an earlier step's input. |
| `inputs.<input>` | The answer to one of this step's own `before` inputs. |
| `steps.<id>.previous` | The output of any step's most recent earlier attempt (before a loop or replay); empty if there was none. |
| `loop.iteration`, `loop.max` | How many times this step has run, counting this one (1 the first time), and the limit of the loop that brings it back. |
| `arrived.from`, `arrived.fromName` | The id and name of the step whose arrow started this one. |
| `steps.<id>.output?` | Like `steps.<id>.output`, but allowed for a step that might not have run (empty then). |
| `feedback` | Your latest rejection feedback on this step. |
| `params.<id>` | A parameter's value, filled in before the run starts (see [Parameters](#parameters)). |
| `item` | In a `workflow` step's `with`: the ticket key or line the child run is for. |
| `scratch.root` | The repo-relative [scratch directory](#the-scratch-directory) root, e.g. `.scratch`. |
| `scratch.dir` | This ticket's repo-relative scratch directory, `<root>/<ticket.slug>` (for `artifact`). |
| `scratch.path` | The same directory as an absolute path in the main checkout, so agents working in a worktree can reach it. |
| `inputs.<input>`, `output` | In `outputTemplate`: the step's answers and its own output. |

A step that was skipped has no output, so `steps.<id>.output` for it is empty text.
