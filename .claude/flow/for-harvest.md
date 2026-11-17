# For the next `/flow harvest`

Not a phase file: notes for the flow skill (`FinnE145/Repo-setup`), raised at the next `/flow harvest`, settled with Finn there and then moved into the skill. Harvest clears each item once it has landed or been turned down.

What goes here: anything learned in this repo that other repos should start with (a rule, a trap, a better way to run a phase or a kind of work), and anything Finn asks to have raised at the next harvest. Strip nothing yet; harvest does that. Facts about this project stay in the repo's own docs.

Each note goes under a dated heading saying where it came from (the step and phase, or "Finn"), with a proposed home in the skill if one is obvious.

## 2026-11-16 — setup
- **`two-checkouts` assumes the other machine is reachable over SSH** for the letter check (`ssh <machine> 'cd … && git branch'`). Here the other checkout is a personal Mac with no SSH set up, so the Git Workflow asks Finn for `git branch` instead. Proposed home: `modules/two-checkouts/MODULE.md`, offering both forms.
- **`laptop`'s Smart App Control bullet is stale.** On 2026-11-16 the policy read Off (`VerifiedAndReputablePolicyState` = 0) and a locally built .NET exe ran; Finn had this repo's copy rewritten to say so. Proposed home: `modules/laptop/MODULE.md`.
- **`frontend`'s thousands-separator bullet assumes server-rendered pages updated live.** It was left out here (native desktop UI, nothing server-rendered). Proposed home: `modules/frontend/MODULE.md`, marking that bullet as web-only.

## 2026-11-16 — step A, Verify
- **Grep for methods that are defined but never called.** `Notifier.Register()` (the notification click hookup) existed, read correctly, and was never called, so every click was lost; Implement's "it launches and stays responsive" check couldn't see it. A per-symbol "has a caller" sweep over the host/UI layer is cheap. Proposed home: `phases/verify.md`, Review.
- **A packaged (MSIX) app can't be reinstalled at the same version with different contents** (0x80073CFB), so an install command that worked on the first install fails on every later one. Record the version bump as a step of the run command. Proposed home: a Windows/MSIX module, or `modules/laptop/MODULE.md`.
- **Real-network rounds found three bugs the unit suite and 70 mutants couldn't**: a progress notification that threw (only for transfers over 20 MB), a received file left hidden by its temp file's attribute, and windows opening behind others. Mutation measures the suite; only driving the app measures the app. Proposed home: `phases/verify.md` or `lessons/testing.md`.
