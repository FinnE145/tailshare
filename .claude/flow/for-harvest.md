# For the next `/flow harvest`

Not a phase file: notes for the flow skill (`FinnE145/Repo-setup`), raised at the next `/flow harvest`, settled with Finn there and then moved into the skill. Harvest clears each item once it has landed or been turned down.

What goes here: anything learned in this repo that other repos should start with (a rule, a trap, a better way to run a phase or a kind of work), and anything Finn asks to have raised at the next harvest. Strip nothing yet; harvest does that. Facts about this project stay in the repo's own docs.

Each note goes under a dated heading saying where it came from (the step and phase, or "Finn"), with a proposed home in the skill if one is obvious.

## 2026-11-16 — setup
- **`two-checkouts` assumes the other machine is reachable over SSH** for the letter check (`ssh <machine> 'cd … && git branch'`). Here the other checkout is a personal Mac with no SSH set up, so the Git Workflow asks Finn for `git branch` instead. Proposed home: `modules/two-checkouts/MODULE.md`, offering both forms.
- **`frontend`'s thousands-separator bullet assumes server-rendered pages updated live.** It was left out here (native desktop UI, nothing server-rendered). Proposed home: `modules/frontend/MODULE.md`, marking that bullet as web-only.
