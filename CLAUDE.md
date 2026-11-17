## Overview
tailshare — AirDrop-style sharing of files and text between devices over Tailscale.

A Windows app and a Mac app that register in each OS's share sheet and live in the system tray / menu bar, letting you send files or text to any device on the tailnet running tailscale. Received files land in Downloads; every receive raises a notification (open Downloads for a file, copy to clipboard for text). It exists because Taildrop doesn't work between different accounts on one tailnet, isn't on every device, and can't send plain text.

## Tech Stack
- **Windows app:** C# on .NET 10 (SDK 10.0.404), WinUI 3 on Windows App SDK **1.8.260921001**, tray icon by **H.NotifyIcon.WinUI 2.4.1**, receiver on ASP.NET Core Kestrel (the shared framework, no package), windows built in code (the only XAML file is `App.xaml`). Packaged as MSIX, self-contained: Windows only offers packaged apps (package identity) as share targets.
- **Mac app:** Swift and SwiftUI, a menu-bar app plus a Share Extension, built in Xcode on the Mac. Swift/Xcode versions and the minimum macOS are recorded in `mac/CLAUDE.md` from the Mac; the minimum is bounded by my own Mac, which is a couple of versions behind, not the target Mac. Libraries (all inside the local Swift package `mac/TailshareCore`, versions pinned by `Package.resolved`): **Hummingbird 2.17.0** (receiver; the newest release Xcode 16.2's Swift 6.0 can build, 2.18 and later need Swift tools 6.2) and **ZIPFoundation 0.9.20** (zip, Zip64). Everything else is Apple's frameworks.
- **Network:** Tailscale (1.104.5 on the laptop, App Store build 1.104.2 on the Mac). Peers come from the local Tailscale install; transfers go directly between devices over the tailnet on port 45611. The wire protocol is defined by roadmap step A's spec, and both apps must match it.
- **Tests:** xUnit (`dotnet test`) on Windows, XCTest (`xcodebuild test`) on the Mac.
- **Storage:** none beyond each app's own settings; received files go to the user's Downloads folder.
- Fill in remaining choices as features are specced. Never assume an unstated version or library.

## Codebase Map
- `docs/` — specs and reference. Feature specs at `docs/specs/<feature>.md`; per-feature extra files (sub-specs, notes, verification reports) in `docs/<feature>/`; planning docs in `docs/Planning/`.
- **"The roadmap" always means `docs/Planning/roadmap.md`** — the standing, ordered plan of what gets built next. Each lettered step becomes its own `/flow plan` session and its own spec; landed steps stay, marked ✅ DONE and pointing at the spec that is authoritative for what actually shipped (read that spec, never the roadmap summary, before touching landed work). It also carries dated measurements that shouldn't be re-derived. Its sibling `docs/Planning/feature_ideas.md` is the loose backlog the roadmap draws from — ideas, not commitments.
- `.claude/flow.json` — which workflow mode, modules and template version this repo was set up with (`/flow update` reads it). `.claude/flow/<phase>.md`, where present, holds this repo's additions to a phase.
- **`.claude/flow/for-harvest.md` is where notes for the flow skill go.** When a session learns something other repos should start with (a rule, a trap, a better way to run a phase or a kind of work), or I ask to have something raised at the next `/flow harvest`, add it there under a dated heading. Not anywhere else: not the roadmap, not a doc of its own.
- `windows/` — the Windows app and its tests. Owned by the laptop's checkout; never edited from the Mac.
- `mac/` — the Mac app, its Share Extension and its tests. Owned by the Mac's checkout, never edited from the laptop; has its own `mac/CLAUDE.md`.
- `windows/Tailshare.Core/` — protocol, receiver, sender, bundles, names, settings, Tailscale wrapper, notification text. Invariants: **only Core's receiver writes into the receive folder, and only inside it; only Core's settings store writes the settings file; Core never references a Windows-only API** (the test peer runs it on Linux).
- `windows/Tailshare.App/` — the WinUI 3 tray app, MSIX-packaged. `windows/Tailshare.TestPeer/` — headless peer over Core, for Verify only. `windows/Tailshare.Tests/` — xUnit over Core. `windows/NuGet.Config` pins nuget.org (this laptop has no NuGet source configured). `windows/Tailshare.sln` holds all four projects.
- (Rest TBD — update this map as directories are created.)

## Keep It Simple
- KISS. The goal is code that is **done, understandable, and works** — not production-grade or clever. AI tends to overdo complexity; don't. Reach for the simplest thing that fully solves the problem.
- **Security is the one exception to KISS — never do the bare minimum here.** Everything security-related must be done *fully and properly*, not just "right": secure coding practice (never leak tokens/secrets) **and** the implementation of things like login, auth, and session handling. Do those thoroughly.
- Beyond security, favor simplicity over robustness — the rest just needs to be done right.

## Code Hygiene
- **Comments explain *why*, not *what*** — most of the valuable ones record a failure that actually happened. Never strip them to "clean up"; a refactor should leave comment density where it was or higher.
- **Module invariants are stated, and kept.** What a module never touches, what it alone writes, what it never commits — written in the Codebase Map. Moving code between modules is exactly what violates one by accident, so check them when you do.
- **Placeholders for personal identifiers in docs** — contact/sender names, Wi-Fi SSIDs, MAC addresses and the like. The repo, and anyone reading it, doesn't need them to understand anything.
- **The repo is public.** Never commit secrets, signing certificates or keys, or real tailnet names, device names or Tailscale IPs, in code, docs or test fixtures.
- **TODOs stay rare** (aim for none), with zero `FIXME`/`HACK`/`XXX` and no commented-out code.

## Commands
These are the laptop's commands. On the Mac this section doesn't apply: `mac/CLAUDE.md` has the Mac's own.
- **Run (signed install, from the Claude app's shell, worked 2026-11-16):** five steps in PowerShell, from the repo root. A running copy locks the exe, so stop it first, by PID:
  1. **Bump the last number of `Version` in `windows/Tailshare.App/Package.appxmanifest`** (1.0.0.4 → 1.0.0.5). Reinstalling the same version with different contents is refused with 0x80073CFB (2026-11-16).
  2. `Get-Process Tailshare.App -ErrorAction SilentlyContinue | Stop-Process`
  3. Build and sign: `dotnet build windows/Tailshare.App -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint>`. The thumbprint is `(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=Tailshare Dev').Thumbprint`. It writes `windows/Tailshare.App/AppPackages/`. About 5 seconds when nothing changed, about 1 minute on a first build (2026-11-16).
  4. Install the newest package: `Add-AppxPackage -Path (Get-ChildItem windows\Tailshare.App\AppPackages\*\*.msix | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion`. This updates in place, so `settings.json` (trusted devices) survives; `Remove-AppxPackage` followed by an add would delete it.
  5. Launch: `Start-Process "shell:AppsFolder\$((Get-AppxPackage Tailshare.App).PackageFamilyName)!App"`
- **Signing certificate** `CN=Tailshare Dev` (must equal the manifest's Publisher), made 2026-11-16, expires 2027-11-16, public part in `%USERPROFILE%\tailshare-signing\`, never in the repo. Made in a **normal** PowerShell, because the Claude app's shell redirects certificate-store writes: `$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Tailshare Dev" -KeyUsage DigitalSignature -FriendlyName "tailshare dev" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")`, then `Export-Certificate -Cert $cert -FilePath "$env:USERPROFILE\tailshare-signing\tailshare-dev.cer"`. Trusted from an **administrator** PowerShell: `Import-Certificate -FilePath "$env:USERPROFILE\tailshare-signing\tailshare-dev.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople`.
- **Test peer for the Linux server:** `dotnet publish windows/Tailshare.TestPeer -c Release -r linux-x64 --self-contained -o <out>`, about 15 seconds, 106 MB (2026-11-16).
- **Dev port:** 45611 (the default; `port` in the settings file overrides it, but the dev loop uses the default)
- **Port 45611 is not negotiable for the dev loop** — it's the port every peer connects to, so a dev build listening anywhere else can't receive from the other devices. If it's occupied (usually an installed copy of tailshare, or another chat's dev build) or the app is otherwise unreachable, **stop and ask me to free it**. Don't reassign the port, don't fall back to a headless workaround, and don't skip the verification.
- **Stray processes:** a session stops what it started (a dev server, anything in the background). Stop things by PID, never `pkill -f`: run through the shell tool, its pattern matches the tool's own shell and kills it (exit 144, no output).
- **Test:** `dotnet test windows/Tailshare.Tests`: 195 tests, about 10 seconds including the build, the suite itself about 6 (2026-11-17). `/flow implement` runs it before handing off and `/flow verify` gates the finish-up on it. If a full run can outlast a tool call's 2-minute default, give the call a longer timeout or run it in the background: a run killed mid-way skips its teardown.
- **Coverage:** none
- **Mutation:** no tool; hand-listed mutants over Core, one `dotnet test` each, run from a copy of `Tailshare.Core` and `Tailshare.Tests` (without `bin`/`obj`) so edits to the live tree can't touch the run. Implement ran 61, about 12 minutes; Verify ran 70, about 11 minutes, about 10 seconds a mutant (2026-11-16).
- Lint / format: none
- **Deploy:** none
- **Slow commands carry a measured time.** Any command here that takes more than a few seconds (the suite, a mutation run, a build, a flash) says how long it took and when that was measured, e.g. "about 3 minutes (2026-10-14)". Time it the first time it runs, or after it changes a lot, and write it here.

## Tests
The suite's whole value is whether a test can **fail**. A green test that a broken implementation would also satisfy is the most common defect there is — it turned up in every session of the test-health pass these rules come from, without exception. Two questions, always: *of each test*, what would a wrong implementation have produced here? *Of each module*, what does it produce — a stored column, a returned key, a branch — that no test reads at all? The second is not a refinement of the first, and the first cannot reach it. **Mutation is the only instrument that finds either**; coverage is structurally blind to the second, since code producing an unread value executes exactly as if it were read. `/flow implement` carries the rules for writing them and `/flow verify` for checking them.

## Time Estimates
- **Estimate from measurements, never from how long a person would take.** Your sense of time comes from people working by hand, and you can't see how long your own turns take. Build an estimate from what's recorded: the measured times in *Commands*, and the actual durations of past steps in the roadmap (each landed step records its own). Give two numbers: wall time, and my hands-on time (approvals, sudo, watching a device). Where nothing recorded covers the work yet, run `date` when you start and when you estimate, so you can see how far off you are and say so. I use estimates to judge whether something fits in this session, how long I need to be at hand, and sometimes how a whole step fits around my quota.

## The Workflow: Plan → Implement → Verify
Work moves through three phases, each in its own chat, each **question-driven**. Every phase's specific rules live in the `flow` skill — invoke it at the start of the chat:
- **Plan** → `/flow plan <brain-dump>` — question-driven spec authoring; output is a committed `docs/specs/<feature>.md`.
- **Implement** → `/flow implement [spec]` — build from that spec, asking live.
- **Verify** → `/flow verify` — review the diff against the spec, run the app, finish up.

If I'm clearly in one phase but didn't invoke it, infer and load that **one** phase (never all three; if the phase is ambiguous, ask which one). The phases are self-contained — this file holds only what's true across all three.

## No Assumptions & the Stop-and-Ask Rule
- Never assume behavior, versions, libraries, or intent I haven't stated. If something is undefined, ask.
- **Stop-and-ask (token reduction):** the moment you're unsure or catch yourself weighing alternatives, stop right there and ask — do not keep reasoning through the options first. Ask immediately, get my answer, then continue the train of thought.
- **One try, then ask.** If an approach doesn't work on the first attempt, stop and ask for direction. Do not try a second approach, and never layer hacky fixes (overrides, workarounds) to force something through. Surface the problem instead of digging deeper.
- Prefer asking me over reading large, token-expensive docs. Only open those if I point you to them or say broader context is needed.
- **What not to ask.** These are decided, so do them and say so in a line:
  - **The branch and its name** at the start of a phase (*Git Workflow* 2).
  - **Anything the repo, a command or a measurement can answer.** Look it up instead.
  - **Fixing what is plainly wrong or minor**: a test written wrong, a stale line in a doc that the code and its comment contradict, a typo. Fix it and list it in the report. Ask only when two things conflict and it isn't obvious which is right.

## When Asking Questions
- Always **number** questions (use sub-letters when nesting, e.g. 1, 2a, 2b) so I can reply item-by-item. This applies to any list I'll respond to point-by-point; plain prose replies don't need identifiers. Numbers run consecutively with no gaps: the app renders markdown lists sequentially, so a list numbered 40, 42, 43 shows up as 40, 41, 42 and my answers land on the wrong items. Re-asked leftovers get fresh consecutive numbers, never their old ones; sub-items stay letters inside a line, not a nested list.
- **Brief first, then ballot.** Findings, reasoning and recommendations go in prose *above* the numbered list; each numbered item is the decision itself, ideally one line. A question carrying its own justification inline is hard to scan and hard to answer item-by-item — the identical list, re-asked with the rationale lifted out, reads far more clearly.
- **Compressing a question moves its "why" up, never deletes it.** If a fact matters enough to state, it stays on the page — just above the list rather than inside it. This is a rule about placement, not about asking less: never drop a real consideration to make a list look tidy.
- **Mid-run questions go through the question tool.** When you stop partway through a run for one or two questions, ask them with the question tool so I get a notification — I'm often away while a phase runs. Long question batches (the start of a Plan session, setup) stay as a numbered list in chat; they're too long for the tool.
- **Commands go in the chat, never only in a doc.** I don't read specs, READMEs or tests, only the chat. Anything I have to run goes in the reply as the exact command, real paths filled in, one per fenced block, saying where to run it if it needs a TTY or my password. Docs still get updated, but they are never the delivery.
- **Text written beside the task list, the question tool or the wrap-up box never reaches me.** The desktop app draws those tools as their own interface and drops any text in the same message, with no way to open it. For the question tool and the wrap-up box, put the context in the tool itself (the question's own text, the recap); otherwise put it in a message of its own with no tool call. This is about where text goes, not when: say a thing when it's relevant, never hold it back for the end.

<!-- flow:modules — module sections are inserted here (frontend, external constraints, deploy, …) -->

## Frontend
- **Function over form.** This is a personal tool; a plain page that does everything I want beats a pretty, half-finished one. Don't spend effort on visual polish unless I ask. Use each OS's standard controls, tray/menu-bar conventions and notification styles; nothing custom-drawn. **Update 2026-11-16:** plain OS defaults came out ugly and hard to understand on Windows, so the UI must also be tasteful and easy to use: native controls, laid out with care (spacing, grouping, a clear primary action, icons). Still not fancy or over-designed.
- **UI copy must not read as AI-written.** Labels are terse and plain. No conversational notes, no em dashes. No loose hint text lying around the page: an explanation goes in an info popup next to its field or section, or in a defined help slot of the page's structure. Most things need no explanation at all: write one only for what the user couldn't tell from the page itself. A page titled "Settings" with a column of toggles needs no line, or popup, saying it is where settings are changed or that dangerous actions are red.
- **Never let red vs green alone carry meaning.** I'm red-green colourblind (deuteranomaly), so a red/green pair reads as one colour to me. Pair colour with an icon, label, shape or position, and prefer palettes that separate on blue/orange or on lightness.
- **A count in prose is rendered from the data, or it isn't stated.** A page once claimed "the 36 playlists" while the table held 37 — wrong silently, and nothing could have caught it. Either render the number or write the sentence without one.

## Laptop (Windows)
On the Mac this section doesn't apply.
- **The Claude desktop app is MSIX-packaged**: writes to AppData or HKCU from your shell tools are silently redirected and nothing else sees them. Never install a toolchain or library under AppData. Persistent settings (`setx`, registry) go to me as commands for the app's Terminal panel or a normal terminal, which aren't redirected.
- **Smart App Control is off on this laptop** (2026-11-16: `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` `VerifiedAndReputablePolicyState` = 0, and a locally built .NET exe ran directly). Locally built executables run here. If something is ever blocked with event log ID 3033 or 3077, it's back on: tell me rather than working around it.
- **Write edit scripts to a file with the file tool and run that**: through the shell tool's heredocs, a `\0` came out as a real NUL and made git treat a source file as binary, a `\n` became a real newline inside a string literal, and apostrophes broke the quoting twice.
- **Python's text mode rewrites every line ending of a CRLF file** (`open().read()` folds `\r\n`, and a stray `\r\r\n` into two lines; `write()` puts back `\n`). Read and write bytes, or check `git diff --stat` for a whole-file change before committing.

## Git Workflow
<!-- flow:git -->
Solo repo, two checkouts (no worktrees): `C:\Users\finne\Projects\tailshare` on the laptop for everything but `mac/`, and `~/Projects/tailshare` on the Mac for `mac/`. Work moves between them only through GitHub (`FinnE145/tailshare`, public): **pull the branch before starting a phase**. Plan runs on either machine; Implement and Verify run where the code is built and tested. Branch prefixes: **`feat/`** features, **`chore/`** tooling/docs, **`fix/`** fixes. A feature branch carries both the spec (its first commit) and the implementation commits stacked on top.

These are **always-on tripwires** — warn *before* acting, then do whatever I decide. Never silently proceed past one; never refuse once I've answered.
1. **No committing to `main`.** Before any commit, check `git branch --show-current`; if it's `main` and this isn't a tiny main-level fix, stop and flag it — the work belongs on a branch.
2. **Set the branch before new work.** A fresh session inherits whatever branch was last checked out, which is likely wrong for new work. Setting the branch is the *first* action of any new phase, before reading code: pull, check `git branch --show-current`, then cut the fresh branch off up-to-date `main` under the usual name (or switch to the right existing one) and say which in one line. **Don't ask me about the branch or its name**; ask only when it is genuinely unclear which existing branch the work belongs on.
3. **No premature merge/push.** Merging a branch into `main` and pushing happen only in the Verify finish-up — never during Plan or Implement. **The one exception: a step implemented on the other machine pushes its feature branch at the end of Plan** (asking first, the same way Verify does), so the other machine can pull it; later pushes of that branch happen when I say. Merging into `main` stays Verify's.
4. **Pull before saying something is missing.** If a spec or branch you were pointed at isn't there, `git fetch --all` and pull the branch, in this checkout, before telling me it's missing: it is usually on the other machine's push. The same before creating a spec or a branch, so its letter is checked against what's really taken. Each checkout also has local branches the other has never seen, so before giving a step, spec or branch a letter, ask me for `git branch` from the other checkout unless you can reach it yourself.

Commits: commit only when I ask, in logical units — one for the spec, a few for implementation as needed, optionally one or more from verify. **Logical units are decided at the file level, never by splitting a single file's hunks across commits** — commit whole files together (`git add <file>`, not `git add -p`). A normal plan/implement/verify session's diff is one coherent piece of work even when a "logical" grouping would put different hunks of the same file in different commits; splitting hunks costs time, tokens, and is an easy way to stage the wrong thing. The one exception is a real mistake — edits for a genuinely separate concern that shouldn't have landed in this session/branch at all — where untangling by hunk is the correct fix, not a workflow shortcut. Commit in my name only — no Claude/AI co-author or attribution line. Just leave it out, don't mention that you did; say so only if you deviate. **Don't push without being asked.** The sanctioned merge+push is the Verify finish-up: a **`git merge --ff-only`** into `main` (keeps history linear, no merge-commit clutter) then push. This repo is solo, so `--force-with-lease` is safe when a rewrite is the agreed fix, but only for a branch the other checkout hasn't pulled; if it has, ask first.
<!-- /flow:git -->
