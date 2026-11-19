# tailshare — Roadmap

**Status: planning, not a spec.** This is the standing, ordered plan of what gets built next. Each lettered step becomes its own `/flow plan` session and its own `docs/specs/<feature>.md`. Nothing here is implementation-ready as written.

**How this doc works.** It's append-only in spirit: finished steps stay, marked ✅ DONE and pointing at the spec that is authoritative for what actually shipped. New work is added as a new lettered step (continue the letters — they're labels, not an order; the order is the diagram below. Before giving one, check every branch's name too: a branch can hold a letter before this file shows it). Anything measured goes under *Verified facts* with the date it was measured, so a later session can trust it or re-measure it deliberately. Loose ideas that aren't commitments yet live in `feature_ideas.md` beside this file.

---

## Spec index

What each spec in `docs/specs/` actually covers, and the code it's authoritative for. This table exists so "which spec introduced X?" is a lookup rather than a re-derivation.

**Every new spec gets a row here as it is written** (the Plan phase's second commit). Verify corrects the row if what shipped differs.

| spec | scope | primary code | step |
|---|---|---|---|
| `docs/specs/windows-app-A.md` | Wire protocol v1, the Windows app, the test peer | `windows/` | A |
| `docs/specs/mac-app-B.md` | The Mac menu-bar app and Share Extension (protocol v1 from A) | `mac/` | B |
| `docs/specs/ios-shortcuts-E.md` | Protocol v1 additions (optional `bytes`, estimated size, hub mark, pickup), the hub, pickup targets in both apps, the iPhone/iPad Shortcuts | `windows/Tailshare.Hub/`, both Cores and apps, `shortcuts/` | E |

---

## Verified facts

Measurements a later session should trust rather than re-derive — each under a dated subsection. Don't trust a number that contradicts one of these without re-measuring it deliberately.

### 2026-11-16 — Finn's Mac
- macOS 14.8.7 on a MacBook Pro (M1). Only the Command Line Tools were installed (Swift 6.0.3); the newest Xcode for macOS 14 is 16.2.
- Tailscale is the App Store build, 1.104.2. `/Applications/Tailscale.app/Contents/MacOS/Tailscale status --json` works without admin (`/usr/local/bin/tailscale` is a wrapper script for it). Port 45611 was free.

### 2026-11-16 (step A's Verify)
- A 1 GB file from the Linux server to the laptop over the tailnet took 16 s (about 60 MB/s); 60 MB took 1 s. The receive side was the Windows app, with the progress notification.
- Windows' notification centre shows a progress notification's **Cancel** button only once the notification is expanded.
- `AppNotificationProgressData(0)` throws "The parameter is incorrect": progress sequence numbers start at 1.

### 2026-11-17 — the Mac app's first real run
- macOS App Transport Security refuses plain `http://` inside an app bundle even to a tailnet IP address (error `-1022` on every request); unit tests outside a bundle never see it. The app carries `NSAllowsArbitraryLoads` for this reason.
- The Tailscale App Store binary is both the GUI and the CLI and guesses which from how it was started. Run from the Mac app when it was launched from Launchpad or Spotlight (not from Terminal) it exited 0 and printed "The Tailscale GUI failed to start" instead of JSON. `TAILSCALE_BE_CLI=1` forces CLI mode.
- A Mac Release build with `xcodebuild` took 1 min 23 s on a first build and 16 s to 26 s after small changes; the 95-test Core suite runs in about 4 s.

---

## Order

```
A (protocol + Windows app) -> B (Mac app) -> E (iPhone/iPad Shortcuts and the hub)
C (visual identity and assets) -> D (Windows UI restyle)
```

**A has landed** — wire protocol v1, the Windows app and the test peer. **B is implemented and merged into `main` but not verified** (no `/flow verify` has run for it yet, Finn, 2026-11-18). <!-- Verify keeps this line current: "A, B and C have landed — …" -->

B builds against A's wire protocol, which A's spec defines and A's test peer exercises, so A comes first.

E comes next, before C and D (Finn, 2026-11-18): it changes both apps and the protocol, and its Verify also runs B's.

C and D follow A and B. D takes its colour and icons from C, so C comes first; neither touches the wire protocol.

---

## A — Wire protocol and Windows app ✅ DONE

**Specced → `docs/specs/windows-app-A.md`.** That spec is authoritative; the summary below is the shape, not the detail.
**Time:** estimated implement about 5 h (30 min hands-on) and verify about 1.5 h (45 min hands-on); took plan about 45 min (21:30 to the spec commit at 22:14), implement about 50 min (to 23:05, one session), verify about 1 h (23:05 to about 00:05), all 2026-11-16. Finn's hands-on time: about 35 min of clicking through rounds in Verify, plus the certificate commands during Implement.

The wire protocol every tailshare app speaks, the Windows tray app (share target, popup, notifications, first-contact trust), and a headless test peer for verifying against a second device before the Mac app exists.

## B — Mac app

**Specced → `docs/specs/mac-app-B.md`.** Implemented and merged into `main`, **not verified yet**: its `/flow verify` is still to run, on the Mac (`mac/` is the Mac checkout's).

A menu-bar app plus a Share Extension with the same behaviour as A's Windows app: register in the share sheet, send text or files to a tailshare device, receive into the receive folder (Downloads by default), notify on receive (click opens the folder for files, copies text, opens links with a Copy action), and ask before trusting a new device. It implements A's wire protocol v1 as written in A's spec, and the settings file's shape.

Known going in:
- The minimum macOS is bounded by Finn's own Mac (a couple of versions behind), not the target Mac, which is brand new. SwiftUI's `MenuBarExtra` needs macOS 13 or later.
- A Share Extension is always sandboxed; it likely hands the send to the main app (an app group) rather than doing network I/O itself.
- The Mac's Tailscale CLI location depends on which Tailscale build is installed (App Store or standalone); `tailscale status`/`whois` are what A uses.
- The protocol's offer-then-upload shape was chosen partly so a plain `URLSession` client works (no `Expect: 100-continue`).

## E — iPhone and iPad through Shortcuts, with a hub

**Specced → `docs/specs/ios-shortcuts-E.md`** (not landed). Implemented in three places on one branch: the laptop (Core, the hub, the Windows app), a session on the home server (deploying the hub), then the Mac (the Mac app and the Shortcuts).

**Progress:** the laptop's part is implemented (2026-11-18: Core's protocol additions and pickup store, the hub and its deploy files, the Windows app). Still to do, in order: the server deploy session, then the Mac's Implement (TailshareCore, the Mac app, the Shortcuts), then Verify, which also runs B's. The spec's *Where the implementation departed* list is the thing the other two sessions read first.

Two Shortcuts let an iPhone or iPad send to any tailshare computer from the share sheet and pull items a computer is holding for it, across Tailscale accounts. A hub on the always-on home server keeps a registry of phones and iPads (each registers on its first Shortcut run) so every computer can offer them as targets. E's Verify also runs B's, which never had one.

## C — Visual identity and assets

**Not specced.** Own `/flow plan` session.

Decide what tailshare looks like, once, so both apps share it: an accent colour, and a few simple icons or logos: the app icon (the Mac app has none yet, so it shows the generic one; the Windows package ships three logo PNGs in `windows/Tailshare.App/Assets/`, which the plan session should check for being the template's placeholders), the tray and menu-bar icon, and whatever the share sheets show for the app.

Decisions already made, or constraints that come with the work:
- Simple, not fancy or over-designed (Finn, 2026-11-16): a personal tool.
- **Finn is red-green colourblind (deuteranomaly):** the accent and any status colours must separate on blue/orange or on lightness, never rely on red vs green (the root `CLAUDE.md` Frontend rules).
- The Mac menu-bar icon should be a template image (one shape, the system tints it): today it is the SF Symbols `paperplane` and, while receiving, `arrow.down.circle`, two different shapes rather than two colours. A new icon keeps that property, in both its states.
- The repo is public: the assets are made by Finn or licensed to be here, and nothing in them is personal.
- Where the source files and the exported sizes live (one place both apps read from, or each app's own folder) is for the plan session to settle.

Output: a spec with the colour values, the icon set and where each file goes, plus the assets themselves committed. Applying them to the apps is D (Windows) and a small follow-up on the Mac.

## D — Windows UI restyle

**Not specced.** Own `/flow plan` session, after C.

Bring the Windows app's windows up to the Mac app's standard (Finn, 2026-11-17: the plain WinUI defaults came out ugly and hard to understand, and B's UI is the one he likes): tasteful and easy to use, native controls laid out with care, still not fancy and nothing custom-drawn.

What B's UI does, as the reference (read `docs/specs/mac-app-B.md`, *Where the implementation departed*, and `mac/Tailshare/`): a device list with rows that show the selection with a checkmark, a **Files** / **Text** switch, a drop zone and a removable list of picked items, one prominent `Send to <device>` button, a status line with an icon and a progress bar, a first-contact window with the device name and what it wants to send, and a send window for the share sheet that lists what is being sent. Windows keeps its own conventions where they differ (tray icon, Windows notifications, its share target).

Known going in:
- The windows are built in code and the only XAML file is `App.xaml` (A's departures list says why); the restyle works within that.
- Meaning never rides on colour alone, and the copy stays terse and plain (root `CLAUDE.md`, *Frontend*).
- Updating A's spec and the root *Frontend* wording to match is part of the step; the root line already carries the 2026-11-16 note.
- Lessons from B's testing that may apply: a scroll view inside a window that sizes itself to its content has no height of its own, and a popup that closes on focus loss can't accept a file dragged in from another window.

---

<!--
Step section shape. Copy for each new step; the letter is a label, not an order.

## X — Short title

**Not specced.** Own `/flow plan` session.

What it is and why, the decisions already made, anything measured (dated), and the traps already known.

When it lands, Verify rewrites the header and first line as:

## X — Short title ✅ DONE

**Specced → `docs/specs/<feature>-X.md`.** That spec is authoritative; the summary below is the shape, not the detail.
**Time:** estimated <implement / verify>; took <plan / implement / verify, from commit times>, <Finn's hands-on time>.
-->

---

## Cross-cutting notes

Things true across many steps that don't belong to any one of them.
