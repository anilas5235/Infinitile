# Coordinator workflow for this repository

How the owner and a coordinating agent drive a spec phase to completion with one agent session
per task. Adapted on 2026-11-08 from a multi-repo outline (PageMark) to the constraints of a
single Unity project: one checkout, one Editor, Burst, and tests that only run inside that
Editor. The spec ([WorldGenSpec.html](WorldGenSpec.html)) stays the single source of truth;
this file only says how work on it is organised. Updated on 2026-11-16 with the owner's rules
of 2026-11-08…16 (Opus for every session, Editor restarts, archiving, timing runs, test tiers).

## 1. Roles

| Role | Who | Does |
|---|---|---|
| Owner | the human | decides (always offered as options), approves anything risky, merges every PR, play-tests and checks UI and in-game behaviour, keeps Unity Hub running, focuses the Editor for timing runs |
| Coordinator | one long-lived agent session | slices a phase into tasks, writes briefs, starts and ends task sessions, relays questions and results, verifies merges, cleans up branches, archives finished sessions, keeps memory |
| Task session | one fresh agent session per task | reads the spec section and the code, works on the phase branch in the main checkout, opens ONE PR, batches questions into its wrap-up |
| Review | `/code-review` in the task session, or a review session for a phase with more than two PRs | finds correctness bugs before the owner merges; the findings go to the owner first, and only the ones the owner picks are fixed, as "review fixes" commits in the same PR or a fix PR |

The coordinator never does task work itself beyond quick checks. A task session never merges,
never starts the next task, and never asks the owner mid-task except through its wrap-up.

**One writer at a time.** There is one checkout and one Editor, so at most one session edits
code at any moment. Read-only sessions (research, spec drafting, log analysis) may run beside
it. Every session, writing or read-only, starts fresh, shares the main checkout
(`use_worktree: false`) and runs on Opus (`claude-opus-5-5`, effort high; owner, 2026-11-08
and 2026-11-15: "always use opus for subsessions"). Do not create Unity worktrees per task: each
one needs its own `Library` import (minutes to tens of minutes, many GB) and the CLI tools
drive the one open Editor. The phase branch replaces the worktree.

## 2. Documents that carry the state

Everything is in English; the repository has no second language.

| Document | Where | Purpose |
|---|---|---|
| Spec (plan, requirements, decisions) | [Docs/WorldGenSpec.html](WorldGenSpec.html) | Part I generation (§1–14), engine rules (E1–E3), Part II cleanup, ideas and phases G/O/K (§15–19), Part III research (§20–23). Requirement IDs `GEN-`, `VEG-`, `TST-`, `PERF-`, `INT-`, `TOOL-`; decisions `DEC-nn` in the decision tables of §14, §15.4, §17.8, §17.11, §18.6, §19.9 and §E1.1 (DEC-109 is the last one in v1.10); ideas in §16 (16.24 the last). Amended in place with dated notes, old wording kept; the version line, the "Decisions recorded" chip and the §0 decisions bullet change with each version |
| Game spec (later work) | [Docs/GameSpec.html](GameSpec.html) | game mechanics, worlds and persistence, content, polish, UI, audio and input that wait outside the focus (decision 2026-11-17). Item IDs `GAME-nn`, all status later; the persistence decisions of 2026-11-17 in §3.1. An item scheduled by the owner gets its phase section there, engine-side parts in WorldGenSpec |
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
   `model: claude-opus-5-5`, `effort: high`, for every task. An "offer" card leaves the model
   to the card: check the started session and switch it to Opus if it is not. The brief has two parts (template in
   section 5): background (what exists, which decisions bind, house rules) and prompt (the
   task, files to read first, deliverables, proofs, what is forbidden, "done means").
5. **Relay**: read the full wrap-up, then verify yourself: PR state and diff stat, a spot check
   of the critical change, `TestResults.xml` for the run the session claims (count, duration,
   failures), `Editor.log` for Burst errors, the baseline JSON if a benchmark was recorded.
   Report to the owner: outcome, what you verified, what is not verified, the session's
   questions with your recommendation first, the next step.
6. **Owner answers**: forward them to the SAME session with `SendMessage`; it has the context.
   "Update the PR to master" means merge `origin/master` into the branch and push; never
   rebase and force-push (force-push is blocked in this setup).
7. **Close out**: confirm the squash-merge commit on `origin/master`, `git checkout master &&
   git pull`, `git branch -D phase/<ID>`, archive the session without asking (owner,
   2026-11-15: "always archive"; never delete a session), write the phase memory (merged
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
  - proofs: plain EditMode run while iterating; PlayMode if the renderer, scheduler or HUD
    changed; explicit tier last, before the PR; timing runs (benchmark rows against <baseline>)
    only after the owner's go for each run (section 6); UI and in-game behaviour are listed for
    the owner to check, never claimed
  - forbidden: committing to master, `unity cmd eval_file` (and avoid `eval`), plain `unity recompile`,
    `unity pipeline upgrade`, rebasing or force-pushing, merging, stash or reset, printing the
    Editor.log header, editing WorldGenSpec.html directly unless asked, changing shipped tuning
    values without a DEC, fixing review findings before the owner picked them
  - PR title pattern "Phase <ID>: <topic> (§<n>)", footer as above, do not merge
  - done means: <checklist: tests green with counts, doc version bumped, PR open, wrap-up lists
    decisions taken, costs measured, questions for the owner>
```

## 6. House rules that proved necessary here

Git and PRs

- Never commit to `master`; work on `phase/<ID>` (or `spec/<name>`), one PR per task, the owner
  squash-merges. Update a PR by merging `origin/master` into it, never by force-push.
- Stacked branches (one cut from an unmerged PR branch) need no rebase after the lower PR is
  squash-merged: merge `origin/master` into the upper branch and push; the squash commit holds
  the same changes, so the merge is clean and the PR diff shrinks to the branch's own commits.
- Review findings go to the owner first, as a list with a recommendation each. Nothing is fixed
  before the owner decides which findings to fix.
- Commit trailer names the model that did the work; the PR description ends with the agreed
  footer. Spec and doc dates are absolute (2026-11-08, not "today"). Code comments carry no
  spec IDs, dates, owner quotes or project history (rule R8); that belongs in the spec.
- Amend the spec, do not rewrite it: keep the original wording, add a dated note. The HTML is
  about 350 KB; a task session hands spec text to the coordinator instead of editing the file,
  unless the brief says otherwise.

The Editor

- The Editor stays open; `unity test` refuses while it is. Drive it with `unity cmd …`:
  `recompile` + `recompile_status`, `console_status`, `run_tests`, `test_status`. Never the
  plain `unity recompile` (unknown command, 30 s timeout) and never `unity pipeline upgrade`.
  `unity cmd recompile` answers "up to date" when Unity already refreshed on its own; then check
  the assembly's time stamp and `error CS` in `Editor.log`.
- Never `unity cmd eval_file`, and avoid `eval`: after an `eval_file`, the next refresh or
  domain reload has hung the Editor for good. Ad-hoc checks go into an EditMode test with `TestContext.WriteLine`.
- Never send unknown or guessed commands while a test runs: every Editor error log fails the
  running test. Do not poll `unity status` during PlayMode runs.
- Never print the header of `Editor.log`: it contains an access token. When quoting the log,
  filter out lines with `token`, `bearer` or `authorization`.
- Agents restart the Editor themselves when a restart is really needed (owner, 2026-11-14;
  this replaces the approval TOOL-05 asked for): a crash (`Crash!!!` in `Editor.log`, e.g.
  `DomainUnloadAutoFree`), a hung pipeline where every `unity cmd` times out, stale Burst that
  the toggle does not fix. `unity close N:\UnityProjects\voxel-game`, then `unity open
  N:\UnityProjects\voxel-game`; if `close` fails on a hung Editor, end the big `Unity.exe` (the
  PID `unity close` or `tasklist` shows). `unity close` does not save: if the Editor still
  answers, check for unsaved edits of the owner first, and never close it while the owner
  works in it. Wait until `unity cmd` answers again, delete a leftover
  `Assets/InitTestScene<guid>.unity`, and tell the owner afterwards.
- Keep Unity Hub running; without it the Editor sits in "Package Manager (busy…)" after a
  domain reload. Start the Hub before `unity open`.
- Order work as edit → recompile → EditMode → PlayMode → explicit tier → play-test, so crashes
  and hangs hit as late as possible.

Burst

- After adding or changing `[BurstCompile]` code, grep `Editor.log` for `Burst error`: a
  compile error there is silent in the tests and makes every job run managed (25× slower).
  Direct-call methods return no structs and take no `bool`.
- If a job behaves like old code after a struct change, Burst is stale: toggle
  `EnableBurstCompilation` off and on; if that fails, restart the Editor straight away.
- Never allocate, grow or free `Allocator.Domain` memory inside a job (spec PERF-19): lists
  made on a worker thread crash the next domain reload. Use `Allocator.Persistent` there.
- New generation passes go after `PrepareChunkMaps`, never inside it (35 % regression once),
  and every generation change is benchmarked. Temp allocations inside a job live until the job
  completes; chunk-per-index jobs run through `SlicedParallelFor`.

Tests and benchmarks

- EditMode runs start detached (`unity cmd --detach run_tests -- --mode …`); wait in a
  background shell for `Run finished` in `Editor.log`. PlayMode runs use
  `--async_tests true` instead (`--detach` runs 0 PlayMode tests); their state is in
  `Temp/pipeline_test_status.json`. Read every result from `TestResults.xml`
  (`<test-run total passed failed skipped>`), never from `test_status` (it times out while a
  run blocks the main thread) or a log line alone.
- Tiers: the plain EditMode run while iterating (761 tests on 2026-11-16) and the regular
  PlayMode tier when the renderer, scheduler or HUD changed (57 tests). The explicit EditMode
  tier (`--include_explicit true`, 877 passed and 2 skipped on 2026-11-16; 327–406 s on 2026-11-14) runs last: the
  pipeline cancels it at its timeout even with `--timeout 900` and then answers no command,
  while the run itself finishes into `TestResults.xml`. Plan an Editor restart after it.
  Flora tests run with caves off (DEC-72).
- Timing runs (benchmarks, `PipelineMeasurementTests`, any frame-time recording) need the
  owner's go before EVERY run: name the runs and the total minutes. The owner sits with a
  focused Editor meanwhile (unfocused reads 10–40 % slow) and cannot use the machine. The
  moment the last run of a batch ends, say first "Timing batch done, the Editor is free
  again", before any analysis.
- Compare two variants in ABBA order (A, B, B, A) so drift hits both alike. If all rows shift
  alike, suspect focus before the code. Editor timings before commit `28ffdb0` are not
  comparable with later ones (the priority queue's heap check ran on every dequeue until then,
  spec §E1.1). Frame times are reported as p95, p99, max and the frames over 8.33 ms (PERF-13).
- "Found N leak(s)" after PlayMode runs is an artifact: `LeakDetectionSetup` switches tracking
  off before the world frees its memory, so everything alive at the end is reported. To check
  for a real leak, comment out the restore line for one run (do not commit), run the fixture,
  force a reload and expect 0 reports.

Play-tests

- UI and in-game behaviour are the owner's to verify in the Editor: a session lists what to
  check and never claims it works in the game. For small gameplay or UI edits, say it is ready
  to try instead of recompiling unasked. Engine and generation changes get the normal runs.
- Captures: `unity cmd screenshot` writes a Game-view PNG to `Temp/pipeline-screenshots/`
  (outside `Assets`, no cleanup). Move with spectator mode (G) instead of teleporting the
  walking player.

## 7. Owner interaction pattern

- Questions in one batch, each with 2–4 options, the recommendation first and marked. If the
  owner asks for more detail on an option, explain and re-ask.
- Report in this order: outcome, what you verified yourself, what is not verified or not done,
  owner questions with recommendations, the exact next step with its duration.
- Commands the owner should run go in their own code blocks, one command per block.
- Review findings and timing go-requests are owner questions like any other: findings as a
  list to pick from, timing runs with names and minutes (section 6).
- "Merged" is checked, not believed: `gh pr view` before any cleanup. Then archive the
  finished session without asking.
- A play-test needs the owner present. If they say "not today", the session parks (nothing
  running, no pending recompile) and the coordinator records the open checklist in memory.

## 8. Proof pattern (before a phase closes)

- Tests: explicit EditMode tier green (count and duration noted in the PR), PlayMode green when
  the renderer, scheduler or HUD changed, Performance suite recorded as a baseline when
  generation or meshing cost changed (after the owner's go, section 6). Baselines carry the
  date and the phase in the name. Scheduler or renderer changes also report the frame-time
  goal rows (PERF-13: p95, p99, max, frames over 8.33 ms) at draw distance 6 and 12.
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

- Native tools want `N:\…` paths; Git Bash wants `/n/…`. Use absolute paths: a `cd` in one
  call can leave the shell elsewhere for the next. Run `unity cmd eval -- '…'` from Bash,
  PowerShell loses the inner quotes (and see section 6 on avoiding `eval`).
- Python scripts: write them to the scratchpad and run `python -I <path>`; `-I` ignores
  `PYTHONIOENCODING`, so call `sys.stdout.reconfigure(encoding='utf-8')` in the script; write
  files with `newline='\n'`.
- Logs and results: `%LOCALAPPDATA%\Unity\Editor\Editor.log` (run finished, Burst errors,
  crashes), `%USERPROFILE%\AppData\LocalLow\anilas5235\Infinitile\TestResults.xml` (latest
  run only), `Temp\pipeline_test_status.json` for async PlayMode runs.
- Syntax check without the Editor: `dotnet build Engine.Runtime.csproj -v:q -clp:ErrorsOnly
  -o <scratchpad>\build` (3–4 s, no Burst, no tests).
- `gh pr view <n> --json state,mergeCommit` is the merge check; `gh pr list --state all` the
  history. The repository has no CI, so every proof is local and goes into the PR description.
