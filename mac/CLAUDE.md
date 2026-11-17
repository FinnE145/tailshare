# mac/ — the Mac app

The root `CLAUDE.md` applies here too, except its *Commands* and *Laptop (Windows)* sections, which are the laptop's. This checkout (`~/Projects/tailshare` on the Mac) owns `mac/` and never edits anything outside it.

## Stack versions
Record these from this Mac the first time a session runs here; never assume them.
- **Xcode / Swift:** TBD
- **Minimum macOS:** TBD — bounded by my own Mac (a couple of versions behind), not the target Mac, which is brand new.

## Commands
- **Run:** none yet — record it here verbatim once the Xcode project exists.
- **Dev port:** 47821, with the same rule as the root *Commands*: it's the port every peer connects to, so if it's occupied, **stop and ask me to free it**; never reassign it.
- **Test:** none yet — XCTest via `xcodebuild test` once the Xcode project exists; record it here verbatim then.
- **Stray processes:** a session stops what it started, by PID, never `pkill -f`.

## Git
This checkout shares the repo with the laptop's (root `CLAUDE.md`, *Git Workflow*):
- pull the branch before starting a phase;
- a step's branch is pushed at the end of Plan, after asking;
- push again only when I say, so the other machine can pull.
