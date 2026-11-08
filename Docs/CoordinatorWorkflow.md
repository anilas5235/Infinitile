# Coordinator workflow for this repository

How the owner and a coordinating agent drive a spec phase to completion with one agent session
per task. Adapted on 2026-11-08 from a multi-repo outline (PageMark) to the constraints of a
single Unity project: one checkout, one Editor, Burst, and tests that only run inside that
Editor. The spec ([WorldGenSpec.html](WorldGenSpec.html)) stays the single source of truth;
this file only says how work on it is organised.

## 1. Roles

| Role | Who | Does |
|---|---|---|
| Owner | the human | decides (always offered as options), approves anything risky, merges every PR, play-tests, keeps the Editor and Unity Hub running, restarts the Editor when asked |
| Coordinator | one long-lived agent session | slices a phase into tasks, writes briefs, starts and ends task sessions, relays questions and results, verifies merges, cleans up branches, keeps memory |
| Task session | one fresh agent session per task | reads the spec section and the code, works on the phase branch in the main checkout, opens ONE PR, batches questions into its wrap-up |
| Review | `/code-review` in the task session, then "review fixes" commits in the same PR | finds correctness bugs before the owner merges; a separate review session only for a phase with more than two PRs |

The coordinator never does task work itself beyond quick checks. A task session never merges,
never starts the next task, and never asks the owner mid-task except through its wrap-up.

**One writer at a time.** There is one checkout and one Editor, so at most one session edits
code at any moment. Read-only sessions (research, spec drafting, log analysis) may run beside
it with `use_worktree: false` on a cheaper model. Do not create Unity worktrees per task: each
one needs its own `Library` import (minutes to tens of minutes, many GB) and the CLI tools
drive the one open Editor. The phase branch replaces the worktree.

## 2. Documents that carry the state

Everything is in English; the repository has no second language.

| Document | Where | Purpose |
|---|---|---|
| Spec (plan, requirements, decisions) | [Docs/WorldGenSpec.html](WorldGenSpec.html) | Part I generation (§1–14), engine rules (E1–E3), Part II cleanup, ideas and phases G/O/K (§15–19), Part III research (§20–23). Requirement IDs `GEN-`, `VEG-`, `TST-`, `PERF-`, `INT-`, `TOOL-`; decisions `DEC-nn` in §14; ideas in §16. Amended in place with dated notes, old wording kept |
| Phase plan | the phase's section in the spec (e.g. §19 for Phase K) plus §13 / §23 phase tables | the tasks of a phase, their acceptance criteria and open decisions |
| Pipeline docs | [Assets/Engine/Docs](../Assets/Engine/Docs) | how the engine works today; updated by the task that changes the behaviour, with a version line (e.g. "Version 1.11 (Caves)") |
| Progress | the PR description, and the spec's phase section once merged | what a task built, proved and left open; a separate `Docs/Progress/phase-<ID>.md` only when a phase spans more than two PRs |
| Benchmarks | [Assets/Engine/Tests/Performance/Baselines](../Assets/Engine/Tests/Performance/Baselines) | recorded baselines, one JSON per run, named `<date>-<phase>-<topic>-<mode>-baseline.json` |
| Memory | `C:\Users\nikla\.claude\projects\N--UnityProjects-voxel-game\memory\` | one `phase<ID>-decisions-<date>.md` per phase (owner decisions, PR number, what is accepted or deferred), plus tooling notes; indexed in `MEMORY.md` |

Every decision gets a `DEC-nn`, every finding a PR number or a spec ID, so a later task can
close it with one sentence.

## 3. Lifecycle of a phase

1. **Spec first.** A `spec/<phase>` PR adds or amends the phase section: goal, tasks, tests,
   open decisions with options and a recommendation (as PR #42 and #49 did). The owner merges
   it before any code.
2. **Owner decides** all open points in ONE batch. The coordinator asks them as options with
   the recommendation first (the owner prefers this to free-form questions). Answers become
   `DEC-nn` entries with the date, in the spec and in the phase memory.
3. **Run tasks one at a time** on the phase branch (section 4). A phase with independent parts
   gets one PR per part (G1…G5, 20.3+20.4, 20.5–20.7); never start the next PR before the
   previous one is merged, unless the owner says so.
4. **Play-test last.** The owner flies through the world with the shipped settings; the
   coordinator prepares a checklist (where to look, what to compare, which captures to take)
   and records the owner's verdicts as decisions. Tuning values stay placeholders until then.
5. **Close the phase**: pipeline-doc version bump, benchmark baseline if generation cost
   changed, phase table row in the spec, phase memory with the PR number, deferred items into
   §16 ideas or a dated "owed" memory (e.g. the coverage run, the gallery tuning).

## 4. Lifecycle of one task (the loop the coordinator repeats)

1. **Precondition check**: previous PR merged (`gh pr view <n> --json state,mergeCommit`),
   local `master` pulled, previous phase branch deleted, previous session archived. Editor open
   on the project, Unity Hub running, `unity cmd console_status` shows no compile failure,
   `Editor.log` shows no `Burst error` and no `Crash!!!`.
2. **Gather facts for the brief**: the spec section with its IDs, the pipeline doc, the files
   the task will touch, the memory notes that apply (tooling traps, accepted costs), the
   current benchmark baseline. Ten minutes of reading saves the session an hour of guessing.
3. **Branch**: `git checkout -b phase/<ID> origin/master` in the main checkout (a follow-up PR
   of the same phase branches from the merged master again). Never commit to `master`.
4. **Start the session**: fresh context, `cwd` = the main checkout, `use_worktree: false`,
   strong model for engine or generation work. The brief has two parts (template in
   section 5): background (what exists, which decisions bind, house rules) and prompt (the
   task, files to read first, deliverables, proofs, what is forbidden, "done means").
5. **Relay**: read the full wrap-up, then verify yourself: PR state and diff stat, a spot check
   of the critical change, `TestResults.xml` for the run the session claims (count, duration,
   failures), `Editor.log` for Burst errors, the baseline JSON if a benchmark was recorded.
   Report to the owner: outcome, what you verified, what is not verified, the session's
   questions with your recommendation first, the next step.
6. **Owner answers**: forward them to the SAME session with `SendMessage`; it has the context.
   "Update the PR to master" means merge `origin/master` into the branch and push; never
   rebase and force-push (denied in this setup).
7. **Close out**: confirm the squash-merge commit on `origin/master`, `git checkout master &&
   git pull`, `git branch -D phase/<ID>`, archive the session, write the phase memory (merged
   commit, decisions, what is owed), then wait for the owner's word on the next task unless
   they pre-approved an automatic start.

## 5. Brief template (copy and fill)

```
Title: Phase <ID> T<n> – <short name>
Background:
  - project: Infinitile voxel engine, Unity 6000.3, URP, Burst; checkout N:\UnityProjects\voxel-game,
    remote origin, default branch master at <commit>
  - spec: Docs/WorldGenSpec.html §<n> (IDs ...), decisions DEC-<nn>..DEC-<mm> that bind this task
  - pipeline doc to update: Assets/Engine/Docs/<Doc>.md (current version line)
  - what earlier tasks delivered: PR #<n> (<one line>), baseline <file>
  - facts the session cannot derive: accepted costs (e.g. "+11–13 % DistanceBlend accepted, DEC-xx"),
    placeholder values, what the owner already rejected
  - house rules: section 6 of Docs/CoordinatorWorkflow.md, in particular the Editor rules and the test tiers
  - commit trailer "Co-Authored-By: <the model doing the work> <noreply@anthropic.com>",
    PR footer "🤖 Generated with [Claude Code](https://claude.com/claude-code)"
  - "read the spec section and the code first; make routine choices yourself; batch real questions
    into your wrap-up as options with a recommendation"
Prompt:
  - branch phase/<ID> in the main checkout, no worktree, you are the only writer
  - read first: <files>
  - deliverables, numbered (code, tests with their TST-IDs, pipeline doc, spec notes as a separate
    short section for the coordinator to merge into the HTML, baseline if cost changes)
  - proofs: plain EditMode run while iterating; explicit tier before the PR; PlayMode if the
    renderer, scheduler or HUD changed; benchmark rows against <baseline> with a focused Editor
  - forbidden: committing to master, closing or restarting the Editor, `unity cmd eval_file`,
    `unity pipeline upgrade`, rebasing or force-pushing, merging, editing WorldGenSpec.html directly
    unless asked, changing shipped tuning values without a DEC
  - PR title pattern "Phase <ID>: <topic> (§<n>)", footer as above, do not merge
  - done means: <checklist: tests green with counts, doc version bumped, PR open, wrap-up lists
    decisions taken, costs measured, questions for the owner>
```

## 6. House rules that proved necessary here

Git and PRs

- Never commit to `master`; work on `phase/<ID>` (or `spec/<name>`), one PR per task, the owner
  squash-merges. Update a PR by merging `origin/master` into it, never by force-push.
- Commit trailer names the model that did the work; the PR description ends with the agreed
  footer. Spec and doc dates are absolute (2026-11-08, not "today").
- Amend the spec, do not rewrite it: keep the original wording, add a dated note. The HTML is
  290 KB; a task session hands spec text to the coordinator instead of editing the file,
  unless the brief says otherwise.

The Editor

- The Editor stays open; `unity test` refuses while it is, and closing it needs the owner's
  approval (TOOL-05). Drive it with `unity cmd …`: `recompile` + `recompile_status`,
  `console_status`, `run_tests`, `test_status`. Never the plain `unity recompile` (unknown
  command, 30 s timeout) and never `unity pipeline upgrade`.
- Never `unity cmd eval_file`, and avoid `eval`: it has hung the Editor for good twice. Ad-hoc
  checks go into an EditMode test with `TestContext.WriteLine`.
- Never send unknown or guessed commands while a test runs: every Editor error log fails the
  running test. Do not poll `unity status` during PlayMode runs.
- Keep Unity Hub running; without it the Editor sits in "Package Manager (busy…)" after a
  domain reload. If every command times out with the Editor idle, the main thread is blocked:
  ask the owner to look at the Editor, do not keep polling.
- After a domain reload crash (`Crash!!!` in `Editor.log`, `DomainUnloadAutoFree`), ask for a
  restart. Order work as edit → recompile → EditMode → PlayMode → play-test, to meet it as
  rarely as possible.

Burst

- After adding or changing `[BurstCompile]` code, grep `Editor.log` for `Burst error`: a
  compile error there is silent in the tests and makes every job run managed (25× slower).
  Direct-call methods return no structs and take no `bool`.
- If a job behaves like old code after a struct change, Burst is stale: toggle
  `EnableBurstCompilation` off and on; if that fails, ask for an Editor restart straight away.
- New generation passes go after `PrepareChunkMaps`, never inside it (35 % regression once),
  and every generation change is benchmarked. Temp allocations inside a job live until the job
  completes; chunk-per-index jobs run through `SlicedParallelFor`.

Tests and benchmarks

- Start every run longer than a minute detached and wait on `Editor.log`; read results from
  `TestResults.xml`, never from `test_status` alone. PlayMode runs use `--async_tests true`.
- Plain EditMode run while iterating (about 70 s); `--include_explicit true` (about 290 s)
  before every PR. Flora tests run with caves off (DEC-72).
- Benchmarks only with the Editor focused; an unfocused Editor reads 10–40 % slow on every
  row. If all rows shift alike, suspect focus before the code.
- "Found N leak(s)" after PlayMode runs is pre-existing noise: compare counts, do not chase.

Play-tests

- Small gameplay or UI edits are the owner's to verify in the Editor; say it is ready to try
  and ask before recompiling. Engine and generation changes get the normal runs.
- Captures go under `Assets/Temp/…`, are moved out afterwards and the folder plus `.meta`
  deleted. Teleporting needs the `CharacterController` disabled first.

## 7. Owner interaction pattern

- Questions in one batch, each with 2–4 options, the recommendation first and marked. If the
  owner asks for more detail on an option, explain and re-ask.
- Report in this order: outcome, what you verified yourself, what is not verified or not done,
  owner questions with recommendations, the exact next step with its duration.
- Commands the owner should run go in their own code blocks, one command per block.
- "Merged" is checked, not believed: `gh pr view` before any cleanup.
- A play-test needs the owner present. If they say "not today", the session parks (nothing
  running, no pending recompile) and the coordinator records the open checklist in memory.

## 8. Proof pattern (before a phase closes)

- Tests: explicit EditMode tier green (count and duration noted in the PR), PlayMode green when
  the renderer, scheduler or HUD changed, Performance suite recorded as a baseline when
  generation or meshing cost changed. Baselines carry the date and the phase in the name.
- Costs: every accepted regression is a `DEC-nn` with the measured percentage (e.g. DEC-46,
  forest light 1.06 ms). Unmeasured cost is not accepted cost.
- Play-test checklist first: locations with coordinates (spawn is under water; Forest at
  (216, 424), Plains Oaks at (4, 140, 562)), what to compare, which captures. The owner's
  verdict per item becomes a decision or a §16 idea.
- Only after the proofs: pipeline-doc version bump, spec phase row, READMEs, phase memory.

## 9. Memory discipline for the coordinator

One `phase<ID>-decisions-<date>.md` per phase, appended per event, and one line in
`MEMORY.md`. It always states: PR number and merge state, decisions with `DEC` numbers, what
is accepted at which cost, what is deferred and where it is recorded (§16, an "owed" memory),
and the next step. Tooling traps get their own `reference` memory when they will recur. Update
memory at every state change, before the wrap-up, so the next session continues from the
memory, the spec and the repository alone.

## 10. Tooling notes (Windows / Git Bash)

- Native tools want `N:\…` paths; Git Bash wants `/n/…`. Run `unity cmd eval -- '…'` from Bash,
  PowerShell loses the inner quotes (and see section 6 on avoiding `eval` at all).
- Python scripts: write them to the scratchpad and run `python -I <path>`; `-I` ignores
  `PYTHONIOENCODING`, so call `sys.stdout.reconfigure(encoding='utf-8')` in the script; write
  files with `newline='\n'`.
- Logs and results: `%LOCALAPPDATA%\Unity\Editor\Editor.log` (run finished, Burst errors,
  crashes), `%USERPROFILE%\AppData\LocalLow\DefaultCompany\voxel-game\TestResults.xml` (latest
  run only), `Temp\pipeline_test_status.json` for async PlayMode runs.
- Syntax check without the Editor: `dotnet build Engine.Runtime.csproj -v:q -clp:ErrorsOnly
  -o <scratchpad>\build` (3–4 s, no Burst, no tests).
- `gh pr view <n> --json state,mergeCommit` is the merge check; `gh pr list --state all` the
  history. The repository has no CI, so every proof is local and goes into the PR description.
