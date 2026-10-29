---
name: afl
description: Run an afl workflow against a GitHub or GitLab issue or a Jira ticket (or resume a run) — this session does the AI steps, the user approves gates. Only runs when the user invokes /afl.
argument-hint: "[<#issue | PROJ-45 | ticket URL> [--workflow <id>] | --name <work> --workflow <id> | <run id> | runs | issues | author <skill|step|workflow> <description>]"
disable-model-invocation: true
allowed-tools: Bash(afl:*)
---

# afl

`afl` runs a **workflow** of steps against a **ticket**: a GitHub or GitLab issue (`42`, `#42`, `owner/repo#42`,
`group/project#42` or an issue URL, cloud or self-hosted) or a Jira ticket (`PROJ-45` or a Jira URL such as `https://acme.atlassian.net/browse/PROJ-45`). Each step is done by the AI, by the user, or both. Run state lives in `~/.afl` and is
shared with the afl CLI and desktop UI, so the user can pick a run up anywhere.

You drive runs with the **session agent**: afl never calls Claude itself. It hands you each AI
step's prompt, you do the work here, and you hand the result back. A step can name its own agent
(`currentStep.agent`, e.g. `codex`); afl runs those steps itself, so you only see `awaiting-session` for
steps whose agent is `session`. A step can also be done by several agents side by side
(`currentStep.agents`, with `currentStep.combine` saying how their results become one); if `session` is one
of them, `currentStep.candidates` lists each agent's result and you submit yours with
`afl run submit <id> --agent session --output - --json`. `afl agent list --json` shows the agents the user has set up,
with what each can do (`capabilities`) and whether it's installed here (`available`). A step may instead ask for
capabilities (e.g. `review`) and let afl pick any installed agent that has them; `currentStep.agentChoice` then says
which agent was picked, by whom and why.

Arguments: `$ARGUMENTS`

## Setup check

If `afl` is not found, tell the user to run `scripts/install-cli.sh` from the smidja repo and stop.

If a Jira ref fails because no instance is set up, no token is stored, or the repo isn't linked, tell the
user the command from the error (`afl jira instance add …` or `afl jira link --instance <id>`) and stop.
The token is read from a hidden prompt, so the user must run `jira instance add` themselves; never ask for
the token in chat.

If a GitHub or GitLab ref or listing fails because the repo isn't linked or afl can't sign in, tell the user the
command from the error and stop: `afl repo host detect` or `afl repo host link <connection> <project>` to link the
repo, `afl host add <id> --kind github|gitlab --url <site>` for an Enterprise or self-managed site, or signing in with
`gh auth login` / `glab auth login` (or a token via `afl host add`, which reads it from a hidden prompt). `afl host
list --json` shows the connections and where each sign-in comes from. Never ask for a token in chat.

## Start or pick up a run

- **A ticket ref** (optionally with `--workflow <id>`):
  `afl start <ref> [--workflow <id>] --agent session --json`
  Agents that run a command do so in the background; add `--mode terminal` only if the user wants to watch them
  in a terminal window.
  With no `--workflow`, afl uses the repo's default. Outside a repo, a Jira key starts in the repo associated
  with its project; if afl prints a note that the ticket's project belongs to another repo, tell the user and
  offer to cancel and restart it there with `--repo <id>`. If unsure which workflow fits, show
  `afl workflow list` and ask.
  A workflow can take parameters (`afl workflow show <id> --json` lists them under `parameters`, each with an
  `id`, `label`, `description` and `default`). Ask the user for the ones without a default, and offer to change
  the others, then pass each as `--param id=value`. If afl says to pass `--param`, ask for that value.
  A workflow can also have optional steps (`afl workflow show <id>` lists them as "optional steps", each in or
  out by default, and `--json` shows each step's `optional` with its `help`). List them for the user with their
  defaults and ask which to include, then pass `--include <ids>` and `--exclude <ids>` (comma-separated) for the
  ones they change.
  **Skills.** Before starting, run `afl skill recommend [--workflow <id>] --json`. Show the user `defaults` (the
  workflow's skills, used unless changed) and each of `recommended` (`name` and `reason`), and ask which to tick,
  untick or add (`afl skill list` has the rest). Pass their final list as `--skills a,b` (`--skills ""` for none), or
  pass nothing to keep the defaults. Never add a recommendation they didn't pick. When `needsStack` is true (an
  empty repo), first ask what the project will be built with, offering `stackChoices` (`languages`, `frameworks`)
  but taking any answer; never guess. Save it with `afl skill recommend --language a,b --framework x,y --json`,
  which returns the new recommendations.
  **Permissions.** Runs default to `ask`: a refused command pauses the step (`awaiting-permission`, below). Pass
  `--permissions strict` (carry on without it) or `--permissions auto` only when the user asks. Full auto lets agents
  do anything but the guard rails, so before passing `--permissions auto --yes-auto`, tell the user that and get an
  explicit yes.
- **`feature-flow` or `debug-flow`** (intent → research → specs I → spikes/prototypes → findings, looping back to
  research → specs II → tickets → build with reviewers): ask the user for the ticket.
  If they have none, ask for a short name for the work and start it with
  `afl start --name "<name>" --workflow <id> --agent session --json` (or `local:<name>` as the ref). Its documents
  go in the repo's untracked scratch directory (`.scratch/<name>/`). The build implements tickets in parallel, each
  in its own git worktree; if the user wants everything done one at a time, add `--sequential` (or `--max-parallel <n>` to cap how many run at once). The run stops for approval on the
  spikes and prototypes it plans to do, and again on the findings: show the user the documents (and each
  prototype's branch from its report) and pass on what they accept or reject as feedback. Reviewers default to
  `standards, specs, style`; offer `--param reviewers=...` if they want others, e.g. `security` (each needs a
  `review-<name>` common step). After the build come the optional steps: security review, harden and opening the
  PR (in by default), release plan, observability and retro (out by default).
- **`research` or `architecture`** (a question to answer, or an architecture decision to make): ask for the ticket or
  a short name, as for `feature-flow`. Both stop for approval on the spikes and prototypes and on the findings, as
  above. `research` ends with `ANSWER.md` for approval. `architecture` asks the user for their preferences in its
  first form, stops for approval on `OPTIONS.md` and ends with `RECOMMENDATION.md` (an ADR) for approval; its
  optional `adr` step (out by default) saves the ADR in the repo. Never fill in the user's preferences or pick an
  option for them.
- **`shape`** (the high-level shape of a new app, package or feature, rather than one decision): ask for the ticket
  or a short name, as above. Its first form asks what it is, what matters, what's likely to change, and which of the
  user's other folders and GitHub users or repos to learn their habits from; ask the user for those, never guess
  them. Four research steps then run in parallel (this codebase, patterns, prior art, the user's practices), and
  any of them may ask questions. It stops for approval on `SHAPES.md`, on the spikes and prototypes and on the
  findings, and ends with `SHAPE.md` for approval; its optional `save` step (out by default) saves the design in
  the repo. Never pick a shape for the user.
- **A run id**: `afl run resume <id> --json`
- **`issues`, or the user wants to pick a ticket**: list them with `afl issues --json` (the repo's GitHub or GitLab project), or
  `afl issues --source jira [--filter <name>] --json` (Jira; the repo's default saved filter applies unless
  a `--filter` is named, see `afl jira filter list`, and listings stay within the repo's associated Jira
  projects unless `--all-projects` is passed). Show a short list and ask which to start.
- **No arguments or `runs`**: `afl runs --json`, summarise the runs still in flight and ask which to continue.
  The user can narrow it with `--tag a,b` (every tag), `--folder <path>`, `--status needs-you,failed` (any of
  needs-you, running, waiting, failed, done, cancelled; a finished state includes finished runs) or
  `--search <words>` (every word, in the title, ticket, id, tags, folder, workflow, repo or state). Each run's
  `state` in the JSON says what it is doing; `needs-you` runs are the ones to raise first.
- **Organising runs**, when the user asks: `afl run rename <id> <name> --json`, `afl run tag <id> <tags...>
  [--remove a,b] --json`, `afl run move <id> [<folder>] --json` (only a run started directly; its child runs follow
  it), `afl run folders --json` and `afl run folder create|rename|remove ... --json`. Removing a folder moves its runs
  up a level; it never deletes them.

## The loop

Every command above and below prints the run as JSON. Look at `currentStep.status` and act:

| `currentStep.status` | What you do |
|---|---|
| `awaiting-session` | Do exactly what `currentStep.prompt` asks (including any `## Skill: <name>` sections at its end, which say how to work and write for that step only), in this repo, or in the run's `workingPath` when it has one (a child run with its own git worktree: read and edit files there, not in the main checkout, and don't commit or switch branches; afl commits and merges it). Submit your result (the answer, plan, or summary of changes you made) on stdin with a quoted heredoc: `afl run submit <id> --output - --json <<'AFL_OUTPUT'` … `AFL_OUTPUT`. Continue the loop. If you can't do it (something it needs is missing or broken, and asking the user won't fix it), don't submit a pretend result: fail it with `afl run fail <id> --reason "<why>" --json`, which follows the step's failure arrows (or fails the run). |
| `awaiting-choice` | Several agents did the step (`currentStep.agents`) and the user picks the result. Show each of `currentStep.candidates` (agent, status, a short summary of `output` or the `error`) and **ask** which to use, whether to have an agent merge them, or what to change. Then `afl run choose <id> <agent> --json` (add `--output - <<'AFL_OUTPUT'` … with their edited version), `afl run merge <id> [--agent <agent>] [--note "..."] --json`, or `afl run reject <id> --feedback "..." --json` (every agent redoes it). Never choose on your own. |
| `choosing-agent` | Several installed agents can do the step and you, as the run's main agent, pick one. `currentStep.prompt` describes the step and each candidate (`currentStep.agentCandidates`). Pick the one that fits best and run `afl run use-agent <id> <agent> --by main-agent --reason "<one sentence>" --json`. If the user has said they want to pick agents themselves, show the candidates and ask instead, then use `--by user`. Continue the loop. |
| `awaiting-input` | The step needs answers before it can go on: before it starts, or because the AI asked part-way through (`currentStep.aiAsking`; its work so far is in `currentStep.output`). Show the user what the step is (and, when the AI is asking, a short summary of its output), ask `currentStep.inputs` (below), then `afl run answer <id> [--input id=value ...] --json`. Continue the loop. |
| `awaiting-permission` | The AI was refused tool calls (`currentStep.permissionRequests`: each `index`, `denial.tool`, `denial.summary` (the exact command, URL or path) and `denial.suggestedRules`, most specific first). Show every request with its exact command and suggested rules, and **ask** for each: allow once, always allow (which rule, and saved for the `run`, the `repo` (default) or `global`), or deny; or whether to keep the output as is. Then `afl run permit <id> --once N[=rule] --always N=<rule> --deny N [--scope run\|repo\|global] --json` (or `--all once\|always\|deny`, or `--accept-output`). afl resumes the agent, which retries. Never grant, deny or pick a rule or scope on your own, and never `--all always` without the user saying so. Calls a guard rail or deny rule blocks aren't asked about; they show as a step note ("blocked by …"), and only changing the guard rails or deny rules helps, so tell the user that rather than suggesting an allow. Saved rules: `afl permission list\|add\|remove --json`; guard rails: `afl permission guards --json`. |
| `awaiting-approval` | Any step type can wait here (its `advance` is `approve`). Show the user `currentStep.output` (concisely) and **ask** whether to approve. On yes, ask the step's inputs (below), then `afl run approve <id> [--input id=value ...] --json`. On changes: `afl run reject <id> --feedback "<their words>" --json` sends the step back: an AI step hands you a revised prompt, a user step reopens for them. When `currentStep.openQuestions` isn't empty, the AI ended its output with questions for the user: put each one (`number`, `plainText`) to the user and send their answers with `afl run reject <id> --answer <number>="<their answer>" [--answer ...] [--feedback "<anything else>"] --json` (questions they skip keep the AI's assumption). Never answer them yourself, and never approve on your own. |
| `awaiting-user` | Tell the user what `currentStep.prompt` asks of them. If they say it's done here, ask the step's inputs and run `afl run done <id> [--note "..."] [--input id=value ...] --json`; otherwise tell them they can finish it later with that command (or in the afl UI). Stop. |
| `awaiting-children` | A `workflow` step started child runs (`currentStep.childRuns`: `runId`, `workflowId`, `ticketKey`, `item`, `status`) and waits for them. Tell the user which children it started, then take each child that isn't `done` or `cancelled` in turn: `afl run resume <child run id> --json` and do the same loop on it. When a child finishes, afl carries the parent on by itself; go back to it with `afl run resume <parentRunId> --json` (a child run's `parentRunId` and `parentStepId` say where it came from) and continue the loop there. |
| `awaiting-paths` | The step's arrows split the run into parallel paths, each run one step at a time as a path run (`paths`: `runId`, `branch` (the step the path began at), `status`); afl routes them and starts the steps they lead to by itself. The steps waiting on you in them are in `pathSteps` (each with its `runId`), and their commands are in `next` with `--step <id>`. Handle each waiting step as its own status says, always passing `--step <id>` (when several steps wait, afl refuses a command without it), e.g. `afl run approve <id> --step lint --json`. Never pick one for the user: show every waiting step. |
| `failed` | Show `currentStep.error` (for a `workflow` step it names the child run that failed). Offer `afl run retry <id> --json`, which retries the failed children rather than starting new ones. If the error says a child's branch couldn't be merged (a worktree merge conflict), show the branch and worktree it names and **ask** how to resolve it: the user resolves it (or asks you to, with `git merge <branch>` in the repo and fixing the conflicts), then retry; or just retry to let afl try the merge again. Never resolve a conflict without asking. Stop until they answer. |

### Step inputs

A step may list questions in `currentStep.inputs` (`id`, `label`, `type`, `required`, `options`, `default`,
`help`, `accepts`, `when`): the questions to ask **now**, and only when `currentStep.asksInputs` is true. That's
when a step waits for answers (`awaiting-input`: questions asked before it starts, or by its AI part-way through,
including the AI's own questions with ids `ask-1`, `ask-2`, …), or when it's let through (approved, or marked
done) and asks questions then. Ask them yourself, never answer them on the user's behalf, even when you could
guess the answer from the AI's output:

- `choice`, `multichoice`, `confirm`: ask with AskUserQuestion, one question per input, using `label` as the
  question and `options` (or Yes/No for `confirm`) as the choices; set `multiSelect` for `multichoice`. Put the
  `default` first and mark it "(default)".
- `text`, `multiline`, `number`: ask in chat and wait for the reply.
- Optional inputs (`required: false`) may be skipped: say so when asking.

Pass every answer as `--input id=value` (quote the value; join `multichoice` picks with commas; `confirm` is
`yes`/`no`; use `--input id=-` with a quoted heredoc on stdin for multi-line text, at most one per command). If afl rejects an answer
(exit code 2), show its message and ask that question again. Later steps see the answers as
`{{steps.<step>.inputs.<id>}}`. If a step you do (`awaiting-session`) tells you how to ask questions with an
`afl-ask` block, you may use it when you truly need the user's answer; afl then moves the step to
`awaiting-input`.

A step you do may also let you hand sub-tasks to other agents: its prompt then lists them, with their
capabilities, and explains the `afl-delegate` block. Use it only for self-contained pieces of work another agent
suits better. Submit your reply ending with the block as usual; afl runs the sub-tasks during that `afl run submit`,
and the step comes back as `awaiting-session` with a prompt holding their results. Carry on from there.
`currentStep.delegations` lists each sub-task (agent, prompt, status, output).

A step may also let you file tickets (follow-up work you find out of scope): its prompt then explains the
`afl-ticket` block. End your reply with it as usual; afl files the tickets during that `afl run submit` and the step
comes back as `awaiting-session` with their keys and links (or why one couldn't be filed). Mention them in your
reply. `createdTickets` on the step lists them. Filing on GitHub or GitLab uses the connection's stored token or the
`gh`/`glab` sign-in; if afl says it has no token, tell the user the sign-in command from the error to run themselves.

Some steps save their output as a document in the repo; the step's `artifactPath` says where (e.g.
`docs/afl/acme-app-12/DESIGN.md`). When such a step's prompt says your reply is saved as a document, write the
document itself, not a summary of it, and don't also write the file yourself.

When the run's `status` is `done`, summarise what was done and stop. The `next` array always lists the
commands that make sense from here.

### Showing and replaying steps

Each entry in the run's `steps` has `id`, `name`, `type`, `advance`, `status`, `output`, `replays`,
`iteration` and `maxRuns` (`replays` counts only replays, not loops or arrows back).
`afl run step <id> <step> --json` shows one step in full (prompt, output, answers, feedback, `askRounds`:
the questions the AI asked part-way through and your answers, and `history`: its earlier attempts, each with its
own `askRounds`). When the user wants to redo a step that has run, of any type and even on a finished run, show
them its output, ask what should change (optional), and run
`afl run replay <id> <step> [--feedback "<their words>"] --yes --json`. The run rewinds to that step and every
later step is redone; earlier attempts stay in `history`. With feedback an AI step resumes its conversation;
without it the step starts fresh. Then continue the loop. Never replay without the user asking.

### Composed workflows

A workflow can be built from shared pieces, which afl expands when the run starts, so the run's `steps` are
already the full list. A step can be based on a common step (`afl step list`, `afl step show <id>`), and a
workflow can include another workflow's steps, whose ids are prefixed with the include's id (e.g. `sec-scan` and
`sec-check` from an include called `sec`). To show the user what a workflow will actually do, use
`afl workflow show <id> --expanded [--param id=value ...]`. A workflow that `requires` another starts with a
`requires-<workflow>` step: it reuses a run of that workflow already done on the ticket, or starts one as a child
run (`awaiting-children` above).

### Paths and loops

A workflow is a graph of arrows: the run can split into parallel paths, go round conditional steps or loop back on its
own, so don't assume the next step is the next in the list: always act on `currentStep` (or, while it is
`awaiting-paths`, on `pathSteps`). Steps that were left out (with a note saying so) have status `skipped` (no
output); steps the run never came to (a condition sent it round them, or another path got there first) stay
`pending` with `notReached: true` in the run's step list once it's finished. A step a loop can
bring back has `maxRuns` and shows which time round it is as `iteration` (e.g. 2 of 5); mention it when it is
above 1 ("attempt 2 of 5"). The run's `path` lists every step visit in order (`stepId`, `iteration`, `at`, and `branch` for a visit made on a
parallel path), and
each `history` entry's `cause` (`replay`, `loop`, `route` or `retry`) and `reason` say why it was redone (`Loop iteration 2`, `Branch from 'check'`, or the replay
feedback). When a loop reaches its limit the step fails with "Loop limit reached"; show that and offer to replay
or retry. A step that fails may be retried by afl first (its `retry`) or fail with a timeout; a failure arrow can
then carry the run on, so a failed step doesn't always mean a failed run: go by the run's status. A step's inputs may decide where the run goes next, so ask them exactly as listed.

## AI drafts: skills, common steps and workflows

When the user asks for a new skill, common step or workflow written by an AI (or `/afl author ...`), afl's own agent
drafts it in a hidden run; you don't do the drafting, and these runs never show in `afl runs`. Starting one uses the
user's AI usage, so confirm the description and the kind first.

- Start: `afl author-new <skill|step|workflow> --describe "<their words>" [--repo <id>] [--agent <agent>] --json`, or
  `afl author-improve <kind> <id> [--describe "..."] --json` for an existing one (a built-in is saved as a copy).
  Both drive the draft until it needs the user. `afl author-list [--kind k] --json` and `afl author-show <draft> --json`
  show drafts; read `status` (`working`, `needs-you`, `review`, `failed`, `saved`, `ended`), `check` (the draft's
  `id`, `name`, `description`, `tags`, `detail` and `problems`), `step` and `next`.
- `needs-you` with `step.permissionRequests`: the agent was refused something, usually fetching a new site. Show each
  request's exact URL or command and its suggested rules and **ask** for each: once, always (saved for this draft,
  `run`, or `global`) or deny. Then `afl author-permit <draft> --once N | --always N[=rule] | --deny N [--scope
  run|global] --json` (or `--all once|always|deny`, or `--accept-output`).
- `needs-you` with `step.inputs`: the agent has questions. Put each to the user and send their answers with
  `afl author-answer <draft> --input <id>="<their answer>" ... --json`.
- `review`: show the agent's summary (`step.output`), the draft's name, description and tags, and any `problems`
  (it can't be approved until they're fixed). **Ask** whether to approve and where: `global` (`~/.afl`) or `repo`.
  Then `afl author-approve <draft> --scope global|repo --json`; if a document with that id already exists there,
  afl refuses and you **ask** before adding `--overwrite`. For changes: `afl author-changes <draft> --feedback
  "<their words>" --json`. `failed`: show the error and offer `afl author-retry <draft> --json`. Interrupted
  (`working` with nothing running): `afl author-resume <draft> --json`.
- `afl author-discard <draft> --json` deletes the draft's run and folder; only when the user asks.
- Never approve a draft, pick its scope, overwrite, answer its questions, or grant or deny its permission requests
  without the user's explicit yes.

## Rules

- Always pass `--json` and read state from it; never edit files under `~/.afl` directly.
- Never ask for, read, print or pass on secrets (Jira tokens, GitHub or GitLab tokens, MCP server keys or tokens) in chat. To set an MCP
  server's secret, tell the user to run `afl mcp-secret <server> <NAME>` in a terminal (it reads the value from a
  hidden prompt) or to use the afl app's Settings.
- afl shows a desktop notification when a run needs the user, finishes or fails (states in `afl settings-show --json`'s
  `notifyOn`). If the user asks to change which, use `afl settings-set notify-on needs-you,done,failed --json` (`none`
  turns them off, an empty value resets); `afl notify-test --json` shows a test one and names the `notifier`.
- Only delete a run (`afl run delete <id> --json`) when the user asks for that run to be deleted; it can't be undone.
- Respect each step's scope: if a step's prompt says only to plan or investigate, don't change code.
- Keep submitted output focused — it is fed into later steps' prompts.
- If the user stops midway, the run keeps its state; they can resume it later with `/afl <run id>`,
  `afl run resume <id>` or the afl UI.
