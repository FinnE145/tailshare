# Mac app (B)

Step B of `docs/Planning/roadmap.md`.

**Estimate** (from A's commit times: A's Implement took about 50 minutes, 22:14 to 23:02 on 2026-11-16, against its 5-hour estimate; A's Verify hadn't finished when this was written, so Verify has no measurement yet):
- Implement: about 1.5 hours wall time (A's size, plus creating the Xcode project and the Share Extension, and two libraries new to this repo); about 30 minutes of Finn's hands-on time (adding his Apple ID to Xcode if it isn't yet, the first-run permission prompts, poking the UI).
- Verify: about 1 hour wall time; about 45 minutes hands-on (the share sheet, notifications, the first-contact window and the popup against A's Windows app).

## What this step delivers
A menu-bar app for macOS, plus a Share Extension, that does what A's Windows app does: sends text, files and folders to any tailshare device, receives into the receive folder, notifies on receive, and asks before trusting a new device. It speaks **wire protocol v1 exactly as `docs/specs/windows-app-A.md` defines it**, including that spec's *Where the implementation departed* section (dated 2026-11-16), which overrides the lines below it. That spec is authoritative for both apps and is not repeated here. A was merged to `main` mid-Verify, and A's remaining fixes land on `main`: Implement pulls `main` and re-reads A's spec before starting, and if A's spec has changed since this one was written, A's spec wins and the change is raised with Finn.

Also covered by A's spec and kept as written there, with only the Mac differences below: Tailscale integration (the two CLI calls, device names, the 10-second re-check and rebind), receiving (temp files, finishing, bundles and their caps), the settings file's shape and rules (including `port`), notification text, size formatting, sender statuses, and the security checklist. Where A's spec leaves a detail to the code, `windows/Tailshare.Core/` is the reference for what the Mac must match.

**Changes A made during its Implement that B follows** (from A's departures section): the port is **45611** by default, overridable by `port` in `settings.json`, re-read every 10 seconds with a rebind, and a sender connects to peers on its own configured port; `hello` runs `whois` too and answers `403` when it fails; a sender shows a blocked reply as declined; one invalid-character set on every platform; stricter bundle-entry rules; text notifications truncated without splitting a character; sender statuses `Preparing` and `Cancelled`.

Not in this step, by Finn's call: a UI for trusted/blocked devices and a recent-items list (both stay in `docs/Planning/feature_ideas.md`, as in A). Installing on the target Mac (Finn's girlfriend's) happens after Verify, by Finn; if it fails, he reopens the Verify session.

## Where the implementation departed
Dated 2026-11-16, during Implement. Each is also fixed in line where it applies.
- **Hummingbird 2.17.0, not the newest.** 2.18 and later declare Swift tools 6.2 and Xcode 16.2 has Swift 6.0; 2.17.0 is the newest that builds. ZIPFoundation 0.9.20. Both are pinned in `mac/TailshareCore/Package.resolved` and recorded in the root `CLAUDE.md`.
- **The UI is tasteful, not bare defaults** (Finn, 2026-11-16, after the Windows app came out ugly). Native SwiftUI controls and SF Symbols, laid out with care: device rows with a checkmark for the selection, a drop zone, a `Send to <device>` button, a status line with an icon. The "standard controls only" line of the Frontend rules is relaxed to that, in the root `CLAUDE.md` too. Nothing is custom-drawn and none of it carries meaning by colour alone.
- **Core has a `Node`** (not in the layout above): one `refresh()` is a whole 10-second re-check (Tailscale down, the address changed, `port` edited, a port that was in use retried). It is in Core so the *Tests / Port* clauses are testable; the app only calls it on a timer and when a popup or send window opens.
- **Who sent the hand-off link.** The app accepts a sender if Launch Services names the extension's bundle ID, or, failing that, if the sender's executable (`proc_pidpath`) is the extension binary inside this very app bundle. The extension waits half a second after opening the link so its process still exists when the app looks it up. Neither accepts anything beyond the extension. **Not yet seen with a real extension process**: the extension has to be switched on in System Settings → Extensions → Sharing first, so this is Verify's (a link opened from Terminal was checked and is ignored, with the sender PID delivered).
- **Bundles**: a symbolic link inside a sent folder is left out; a symbolic link entry in a received bundle fails the transfer (A's spec says neither). Names over 255 bytes get a collision suffix by trimming the stem, which A's 200-character cap never needed.
- **The receive notification for text** keeps the full text in memory, not in the notification, so a click after the app was restarted copies nothing.
- **No Xcode test target.** Core's tests run through the package (`xcodebuild test -scheme TailshareCore -destination 'platform=macOS'` in `mac/TailshareCore`, or `swift test`); the project's two targets have none. The project file was generated by a script kept outside the repo: edit it in Xcode from now on, and add new Swift files to it there (it lists files one by one).
- **Release builds carry no debugger-attach entitlement** (`CODE_SIGN_INJECT_BASE_ENTITLEMENTS = NO`); the extension has only the sandbox.
- **Known rough edge**: Browse and Folder close the popup (the panel takes focus), so after picking, the popup is reopened by clicking the icon; nothing is lost (the model holds it).

## Decisions (Finn, 2026-11-16)
- **Minimum macOS 14.** Finn's own Mac is a MacBook Pro (M1) on macOS 14.8.7; the target Mac is brand new.
- **Xcode 16.2**, the newest that runs on macOS 14 (16.3 and later need macOS 15). Installed 2026-11-16 (build 16C5032a, recorded in `mac/CLAUDE.md`).
- **No paid Apple Developer account.** Signing uses Finn's free Apple ID (Personal Team, "Apple Development"). There is no notarization, so a copied app needs Privacy & Security → Open Anyway on first launch on another Mac.
- **Menu-bar only**: no Dock icon (`LSUIElement`).
- **Launch at login** is requested on first run, as in A.
- **The share sheet hands off to the menu-bar app**, which opens a send window already filled in.
- **First contact is a window**, not a notification.
- **Receive progress goes in the popup**, with the menu-bar icon changing while receiving.
- **Libraries: Hummingbird** (receiver) and **ZIPFoundation** (zip). Everything else is Apple's: URLSession, UserNotifications, ServiceManagement, AppKit/SwiftUI. Record each exact version in the root `CLAUDE.md`'s Tech Stack when it's added. Nothing else without asking.
- **Settings file** at `~/Library/Application Support/tailshare/settings.json`, in A's shape (`receiveFolder`, `port`, `trusted`, `blocked`).

## Layout
All under `mac/`, one Xcode project:
- `mac/TailshareCore/` — a local Swift package (library): protocol client and receiver, bundle handling, file naming, the settings/trust store, the Tailscale CLI wrapper, notification text formatting, link detection, and parsing the share hand-off link. **No AppKit or SwiftUI**, so it's testable on its own. Like A's Core, it takes the receive folder, the settings file path and a "prompt the user" callback from its host and never decides those itself.
- `mac/Tailshare/` — the app target: menu-bar popup, send window, first-contact window, notifications, login item, the `tailshare:` link handler. Hosts Core.
- `mac/ShareExtension/` — the Share Extension target.
- `mac/TailshareCore/Tests/` — XCTest over Core.

Invariants (add them to the Codebase Map in `mac/CLAUDE.md` when the directories exist):
- Only Core's receiver writes into the receive folder, and only inside it.
- Only Core's settings store writes the settings file.
- Core never imports AppKit or SwiftUI.
- The Share Extension does no network I/O and never runs the Tailscale CLI.

Swift language mode 5 (the toolchain is Swift 6.0.3).

## Signing and the project file
- Both targets sign automatically with the Personal Team. **The team ID never enters the repo**: `DEVELOPMENT_TEAM` comes from an untracked `mac/Local.xcconfig` (added to `.gitignore`; a committed `mac/Local.xcconfig.example` shows the one line). Bundle IDs are `io.github.finne145.tailshare` and `io.github.finne145.tailshare.ShareExtension`.
- **No capability that needs a provisioning profile**: no App Groups, no iCloud, nothing else. A free account's profile covers only Macs Xcode has registered, so an app carrying one would not launch on the target Mac. This is why the hand-off below doesn't use App Groups.
- The main app is **not sandboxed**: it runs the Tailscale CLI, which a sandboxed process can't, and it reads shared files by path. The Share Extension is sandboxed (extensions always are), with no extra entitlements.
- Whether a Personal Team build with no profile launches on a Mac Xcode has never seen can't be checked from this Mac. Finn finds out when he installs on the target Mac; if it fails, that's a Verify follow-up, and he reopens the session.

## Tailscale on the Mac
GUI apps don't get the shell's `PATH`, so Core looks for the CLI at fixed paths, in order: `/Applications/Tailscale.app/Contents/MacOS/Tailscale` (both the App Store and the standalone build), `/usr/local/bin/tailscale`, `/opt/homebrew/bin/tailscale`. The first that exists is used. On this Mac (App Store build 1.104.2, 2026-11-16) running the bundle binary directly works without admin, and `status --json` gives the fields A's spec uses.

Everything else is A's: `status --json` and `whois --json <ip>` only, the same fields, machine names, the same down/logged-out handling.

## Receiving: Mac differences
- **Receive folder default**: the user's Downloads folder from `FileManager` (`.downloadsDirectory`), never a guessed path. macOS asks once for Downloads access; that's expected, not a failure.
- **Names**: sanitized exactly as A's `Names.cs` does, so every app names a received file the same way (A made this one set on every platform on purpose: a file may be copied to Windows later). That means the same invalid set (`< > : " / \ | ? *` and control characters, replaced with `_`), the same path stripping, trimming and Windows reserved-name prefix, and the same 200-character cap keeping the extension. The Mac adds one rule: the result is also cut to 255 UTF-8 bytes (APFS's limit), without splitting a character. Collision suffixes follow A exactly (`name (1).ext`, `Photos (1)`, and `.gitignore (1)` for a name with nothing before its dot).
- **Bundles**: extraction is ZIPFoundation. A's checks run on every entry before anything is written: absolute paths, drives, `..`, `\` or `:` in any segment, empty or `.` segments, and escaping the item's folder each fail the whole transfer. A's `totalSize` cap applies to what's written. ZIPFoundation must be a version with Zip64 (0.9.17 or later), since a bundle over 4 GB is Zip64. A bundle the Mac sends uses deflate at ZIPFoundation's default level; A's `Fastest` is a Windows setting, and any deflate level is valid on the wire.
- **Binding**: the receiver (Hummingbird) binds only to this device's Tailscale IPv4, on the configured port (45611 by default), never all interfaces. If the macOS firewall is on, it asks once about incoming connections.
- **Port in use**: a notification `Can't receive: port <port> is in use`, and the popup's status line says the same. Sending still works.

## Notifications
UserNotifications, with permission requested on first launch. Titles and bodies are A's, built by Core:
- **Received file or bundle**: click reveals the item in Finder (one top-level item) or opens the receive folder (several).
- **Received text**: click copies the full text.
- **Received link** (A's rule: one absolute `http`/`https` URL, nothing else): click opens it in the default browser; a **Copy** action copies it.
- **Send results from the share sheet**: `Sent to <device>`, `<device> declined`, `<device> didn't respond`, `Couldn't send to <device>`.
- The port-in-use notice above.

There is **no first-contact notification and no progress notification**: macOS notifications have no progress bar, and a banner disappears after a few seconds unless the user changes the app to Alerts, which the app can't do. Both are replaced below.

## First-contact window
When an unknown device makes an offer, the app comes to the front and opens a small standard window:
- Title `<device>`, the line `wants to send <what>` (A's `<what>`), buttons **Accept** and **Decline**.
- It stays until answered. It closes on A's 120-second timeout or when the sender withdraws (closes the connection), and the offer gets A's response for each case.
- Several offers at once get one window each.

## Menu-bar icon and popup
- **Icon**: an SF Symbol, `paperplane` normally and `arrow.down.circle` while any receive is in progress. The two differ in shape, not colour.
- **Clicking the icon** opens the popup (`MenuBarExtra`, window style). Standard controls only, per the Frontend rules. Contents, as A's tray popup:
  - The device list, refreshed whenever the popup opens, plus **Refresh**. States: the list; `No devices`; `Tailscale not connected`.
  - A **Files** / **Text** switch:
    - Files: **Browse** (multi-select files), **Folder** (pick a folder), and dropping files or folders onto the popup; a list of what's picked, each removable.
    - Text: a multi-line box.
  - **Send**, enabled once a device is selected and there's something to send.
  - A send status line with a progress bar: `Preparing` (zipping a bundle), `Waiting for <device>`, sending progress, `Sent`, `<device> declined` (also shown when the receiver blocked us), `<device> didn't respond`, `Failed`, `Cancelled`, with **Cancel** while preparing, waiting or sending.
  - A receive status line, shown while receiving anything over 20 MB (A's threshold): `Receiving <what> from <device>`, a progress bar and **Cancel**. The received notification follows when it's done.
  - **Quit** at the bottom (a `MenuBarExtra` has no right-click menu).
- **Known trap**: the popup closes when it loses focus, which includes opening the Browse or Folder panel. The popup's state (picked items, text, selected device, statuses) lives in a model owned by the app, not by the popup's view, so nothing is lost when it closes and reopens.

## Share Extension and the send window
- The extension registers for text, one web URL, and files (including folders). It has **no UI of its own**: it takes the shared items, hands them to the app, and completes at once.
- **Hand-off**: the extension opens `tailshare://share` with each file's path as a `file` query item (percent-encoded), or `text=1` with the text on a private named pasteboard. The app reads that pasteboard and clears it at once. A shared web URL is text, as in A. Opening the link launches the app if it isn't running.
- **Only the extension may hand off.** Any app or web page can open a `tailshare:` link, so the app checks the open-URL Apple Event's sender (`keySenderPIDAttr` → `NSRunningApplication` → bundle ID) and ignores the link unless it came from the extension's bundle ID. If the sender can't be determined in practice, Implement stops and asks; it doesn't fall back to accepting everything. As a second guard, the app never sends anything by itself: the user always picks a device and presses **Send**, in a window that lists every item.
- **The send window** (a small standard window, brought to the front): the device list with **Refresh**, the list of items being sent (file names, or the text's first line), **Send**, and the same status line and **Cancel** as the popup. It closes when the send ends, and the result is a notification (above). Paths that no longer exist are dropped from the list; if nothing is left, the window shows `Nothing to send`.
- macOS may ask once for access to Desktop or Documents the first time a file from there is shared, because the app reads the file by path.

## Sending
As in A: one plain file is `file`, any other set of files and folders is `bundle` (zipped to a temp file first, deleted afterwards), and text is `text`. URLSession is the client: `offer` with a 130-second timeout, then `upload` streamed from the file with `Content-Length` set (URLSession doesn't send `Expect: 100-continue`, which A's protocol shape relies on).

## Launch at login and single instance
- First run registers the app with `SMAppService.mainApp` and records that it did, so it never re-registers after the user turns it off in System Settings → General → Login Items.
- A Mac app is single-instance through Launch Services already; a hand-off link reaches the running app.

## Build, install, distribution
Implement records these in `mac/CLAUDE.md` verbatim, timed:
- **Build** a Release build with `xcodebuild` and **install** it by copying `Tailshare.app` to `/Applications`.
- **The dev loop and the installed copy can't both run**: they both need port 45611. Per the Commands rule, if the port is taken, stop and ask Finn to quit the other one.
- **The target Mac** gets a zipped copy of the Release build (AirDrop is fine): unzip into `/Applications`, open it once, approve it in System Settings → Privacy & Security → Open Anyway, then allow the notification, Downloads and login-item prompts. Each updated copy may need Open Anyway again. If the share sheet doesn't list tailshare, it's turned on in System Settings under Extensions → Sharing.

## Verify
On this Mac only, with the Release build installed in `/Applications`, against A's Windows app on the laptop (A's test peer on Finn's Linux server is the fallback if the laptop isn't at hand). In both directions: first contact (Accept, Decline, timeout), Cancel on each side, a big file with the popup's receive progress, a folder, several files, a link, plain text, and the share sheet from Finder (files and a folder) and Safari (a link). Plus a `tailshare://share` link opened from Terminal (`open 'tailshare://share?text=1'`), which must be ignored.

## Tests
XCTest over Core. There are no existing Mac rules for these clauses to replace. They are A's clauses, so both apps are held to the same behaviour; where A's Implement changed a rule, the clause pins A's new rule and names the old one it replaces, with the Mac's own additions marked:
- **Names**:
  - an existing `a.txt` yields `a (1).txt`, and `a (2).txt` when that is taken too; `.gitignore` yields `.gitignore (1)`;
  - names without an extension, and folders, follow the same pattern;
  - path components are stripped, with `/` and `\` both treated as separators;
  - each of `< > : " / \ | ? *` and a control character is replaced with `_` (A's one set on every platform, replacing "characters invalid on the platform", under which the Mac would have kept `<`, `?` and the rest);
  - `CON` and `con.txt` get the `_` prefix, trailing dots and spaces are trimmed, a name over 200 characters keeps its extension, and an empty name becomes `file`;
  - (Mac) a name over 255 UTF-8 bytes but under 200 characters (multibyte characters) is cut to fit without splitting a character.
- **Bundles**:
  - an entry with `..`, an absolute path, a drive, `\` or `:` in a segment, an empty or `.` segment, or one escaping its item's folder fails the transfer and leaves nothing in the receive folder (A's set, replacing the original "absolute, drive or `..`");
  - extraction past `totalSize` fails and leaves nothing;
  - folder and loose-file bundles land under collision-free top-level names;
  - a bundle the Mac writes is read back by the Mac's own extraction with the same entries.
- **Sender kind**: one file gives `file`; two files, a folder, or a mix gives `bundle`; text gives `text`.
- **Trust**:
  - an unknown device is prompted, a trusted device isn't, and a blocked device is rejected without a prompt even when it's also trusted;
  - Decline adds the device to neither list;
  - Accept adds it to `trusted` with its name;
  - a hand edit is seen on the next offer without a restart;
  - a malformed file prompts everyone, isn't overwritten, and an Accept while it's malformed doesn't persist.
- **Receive folder**: unset or null, set, and set to a missing folder (falls back to Downloads).
- **Port** (replacing the fixed 47821): missing, null, below 1024 or above 65535 gives 45611; a valid `port` is used both for listening and for connecting to peers; a changed `port` is picked up on the next 10-second re-check.
- **Tailscale CLI lookup**: the first existing path in the order above is chosen (with the paths injected).
- **Protocol**, against an in-process receiver on loopback with `whois` stubbed:
  - `hello`, and `hello` with `whois` failing → `403` (A's rule, replacing "no check at all" for `hello`);
  - `offer`: declined → `declined`, timed out (with a short injected timeout) → `timeout`, blocked → `blocked`, sender closing the connection withdraws the prompt;
  - the sender reports a `blocked` reply as declined, the same outcome as a `declined` one (A's rule, replacing a separate blocked outcome);
  - token single use, a token presented by another `StableID` → `403`, an expired token → `403`;
  - `Content-Length` not matching `bytes` → rejected with no file left;
  - `whois` failure → `403`;
  - text over 1 MiB → `413`;
  - a successful file, bundle and text round trip, client to receiver.
- **Link detection**: a single `https://` URL is a link; text containing a URL is text; `file:`, `javascript:` and other schemes are text.
- **Notification text**: text over 200 characters shows its first 200 plus `…`, never splitting a character (an emoji straddling the cut); size formatting at the B/KB/MB/GB boundaries; `and N more`; the folder form.
- **Hand-off link parsing**: `file` items decode to their paths (including spaces and non-ASCII); `text=1` is text; a link with both, neither, or another host is rejected; the sender check accepts only the extension's bundle ID.

The popup, windows, Share Extension, notifications, login item and the real Apple Event sender aren't unit-tested. Verify exercises them by hand, as above.
