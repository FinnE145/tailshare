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

---

## Order

```
A (protocol + Windows app) -> B (Mac app)
```

**A has landed** — wire protocol v1, the Windows app and the test peer. <!-- Verify keeps this line current: "A, B and C have landed — …" -->

B builds against A's wire protocol, which A's spec defines and A's test peer exercises, so A comes first.

---

## A — Wire protocol and Windows app ✅ DONE

**Specced → `docs/specs/windows-app-A.md`.** That spec is authoritative; the summary below is the shape, not the detail.
**Time:** estimated implement about 5 h (30 min hands-on) and verify about 1.5 h (45 min hands-on); took plan about 45 min (21:30 to the spec commit at 22:14), implement about 50 min (to 23:05, one session), verify about 1 h (23:05 to about 00:05), all 2026-11-16. Finn's hands-on time: about 35 min of clicking through rounds in Verify, plus the certificate commands during Implement.

The wire protocol every tailshare app speaks, the Windows tray app (share target, popup, notifications, first-contact trust), and a headless test peer for verifying against a second device before the Mac app exists.

## B — Mac app

**Specced → `docs/specs/mac-app-B.md`** (planning; not landed). Implemented and verified on the Mac (`mac/` is the Mac checkout's).

A menu-bar app plus a Share Extension with the same behaviour as A's Windows app: register in the share sheet, send text or files to a tailshare device, receive into the receive folder (Downloads by default), notify on receive (click opens the folder for files, copies text, opens links with a Copy action), and ask before trusting a new device. It implements A's wire protocol v1 as written in A's spec, and the settings file's shape.

Known going in:
- The minimum macOS is bounded by Finn's own Mac (a couple of versions behind), not the target Mac, which is brand new. SwiftUI's `MenuBarExtra` needs macOS 13 or later.
- A Share Extension is always sandboxed; it likely hands the send to the main app (an app group) rather than doing network I/O itself.
- The Mac's Tailscale CLI location depends on which Tailscale build is installed (App Store or standalone); `tailscale status`/`whois` are what A uses.
- The protocol's offer-then-upload shape was chosen partly so a plain `URLSession` client works (no `Expect: 100-continue`).

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
