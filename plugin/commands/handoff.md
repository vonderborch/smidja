---
description: Summarise this session into a hand-off note on an afl run
argument-hint: "<run id>"
allowed-tools: Bash(afl:*), Bash(git status:*), Bash(git diff:*), Bash(git log:*)
---

Write a hand-off note for afl run $ARGUMENTS so a future session (or person) can pick it up cold.

1. Run `afl run show $ARGUMENTS --json` to see the ticket, the current step and its existing notes.
2. Review what happened in this session, plus `git status` and `git diff --stat`.
3. Draft a note of at most ~8 lines covering: what was done, decisions made and why, what is left,
   and any open questions. Do not repeat what earlier notes or step outputs already say.
4. Show the draft to the user. Once they approve it, record it with
   `afl run note $ARGUMENTS "<note>" --json`. The note is attached to the run's current step.
