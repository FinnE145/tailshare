# For the next `/flow harvest`

Not a phase file: notes for the flow skill (`FinnE145/Repo-setup`), raised at the next `/flow harvest`, settled with Finn there and then moved into the skill. Harvest clears each item once it has landed or been turned down.

What goes here: anything learned in this repo that other repos should start with (a rule, a trap, a better way to run a phase or a kind of work), and anything Finn asks to have raised at the next harvest. Strip nothing yet; harvest does that. Facts about this project stay in the repo's own docs.

Each note goes under a dated heading saying where it came from (the step and phase, or "Finn"), with a proposed home in the skill if one is obvious.

## 2026-11-16 — setup
- **`two-checkouts` assumes the other machine is reachable over SSH** for the letter check (`ssh <machine> 'cd … && git branch'`). Here the other checkout is a personal Mac with no SSH set up, so the Git Workflow asks Finn for `git branch` instead. Proposed home: `modules/two-checkouts/MODULE.md`, offering both forms.
- **`laptop`'s Smart App Control bullet is stale.** On 2026-11-16 the policy read Off (`VerifiedAndReputablePolicyState` = 0) and a locally built .NET exe ran; Finn had this repo's copy rewritten to say so. Proposed home: `modules/laptop/MODULE.md`.
- **`frontend`'s thousands-separator bullet assumes server-rendered pages updated live.** It was left out here (native desktop UI, nothing server-rendered). Proposed home: `modules/frontend/MODULE.md`, marking that bullet as web-only.

## 2026-11-16 — B, plan (Mac)
- **The flow skill wasn't installed on the second machine.** `/flow plan` on the Mac found no skill; the session cloned `FinnE145/Repo-setup` to a temp folder to follow it, then Finn installed it with `git clone https://github.com/FinnE145/Repo-setup ~/.claude/skills/flow`. Proposed home: `modules/two-checkouts/MODULE.md` (setup asks whether the skill is installed on both machines and gives that command).
- **Plan's "cut from up-to-date `main`" assumes `main` exists.** Here the remote had only the first step's branch (no `main` yet, nothing landed), and the second step needed that branch's spec, so B was cut from A's branch tip, then re-cut from `main` once A merged mid-Verify (carrying the uncommitted plan across with a stash; one `CLAUDE.md` conflict, where A had changed the port). Proposed home: `phases/plan.md`, branch section.

## 2026-11-16 — B, implement (Mac)
- **Mutation runs in a copy of a Swift package must start from a clean `.build`.** Copying a package with its `.build` makes every build fail with "PCH was compiled with module cache path…" (absolute paths baked in), which a script reads as "mutant killed". Copy the sources only and warm-build once. Proposed home: `lessons/testing.md`, mutation section.
- **A mutant that turns a timeout into a hang costs the runner's whole timeout.** Mutating away a timer or a hang-up watcher leaves the test waiting forever; give the mutation runner a per-mutant timeout and count it as killed. Proposed home: `lessons/testing.md`.
- **"Standard controls only" produced an ugly Windows app.** Finn's call on 2026-11-16: UI should be tasteful and easy to use (native controls, laid out with care), not bare OS defaults. Proposed home: `modules/frontend/MODULE.md`, softening any "defaults only" wording.
