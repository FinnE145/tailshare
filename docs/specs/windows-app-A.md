# Windows app and wire protocol (A)

Step A of `docs/Planning/roadmap.md`.

**Estimate** (no recorded measurements exist yet; planning started 2026-11-16 21:30 EST and reached this spec at about 22:30):
- Implement: about 5 hours wall time, likely over more than one session; about 30 minutes of Finn's hands-on time (certificate commands in a normal terminal, one admin command, first install, poking the UI).
- Verify: about 1.5 hours wall time; about 45 minutes hands-on (clicking through notifications, the share sheet and the tray popup against the test peer).

## What this step delivers
1. **The wire protocol v1** (below) that every tailshare app speaks. Step B's Mac app implements the same protocol from this section; it is authoritative for both.
2. **The Windows app**: a packaged (MSIX) WinUI 3 tray app that receives into the receive folder, sends from a tray popup and from the Windows share sheet, raises notifications, and asks before trusting a new device.
3. **A headless test peer** built from the same protocol code, run on Finn's Linux server over the tailnet, so A can be verified in both directions before the Mac app exists.

Not in this step: the Mac app (step B); a UI for trusted/blocked devices and a recent-items list (both in `docs/Planning/feature_ideas.md`, by Finn's call).

## Where the implementation departed
Dated 2026-11-16, during Implement. Each is also fixed in line below.
- **Port: default 45611 (was 47821), overridable by a `port` field in `settings.json`** (Finn's call mid-implementation). Every device that should talk must use the same port: the sender connects to peers on its own configured port. The app re-reads it every 10 seconds and rebinds. The manifest's firewall rule covers 45611 only, so a different port needs its own rule. A missing, null or out-of-range (below 1024 or above 65535) `port` means 45611.
- **`hello` also runs `whois`** and answers 403 when it fails, since the spec says every request does; it still does no trust check.
- **A blocked sender is shown "declined"** by the sender app, so a block reveals nothing a decline doesn't.
- **Invalid-character set is the same on every platform** (`< > : " / \ | ?` `*` and control characters), so the Linux test peer names files like the Windows app.
- **Bundle entries containing `\` or `:` in any segment, or an empty or `.` segment, fail the transfer** (the spec named absolute, drive and `..`).
- **Text longer than 200 characters is shown as its first 200 plus `…`**, never cutting a surrogate pair.
- **Sender status gains `Preparing` (zipping) and `Cancelled`.**
- **Every window is built in code; the only XAML file is `App.xaml`**, which merges the stock control styles (merging them from code failed at launch: `Application.Resources` threw in the constructor, and a hand-built `XamlControlsResources` could not find its own brushes). Startup runs from `Program.Main` rather than `OnLaunched`, so a share-sheet activation and a normal launch reach the same handler.
- **The firewall rule is declared at Package level** in the manifest (MakeAppx rejects it under the Application).
- **The manifest declares a COM server and toast activation (one new GUID)**, which `AppNotificationManager.Register` needs in a packaged app (without them it fails with "No COM servers are registered for this app").
- **State at the end of the first Implement session: Core, its tests and the test peer are done. The signed MSIX was built, installed from the Claude app's shell with `Add-AppxPackage`, and launched (2026-11-16): it starts, stays responsive, shows the popup, and listens on the Tailscale address, port 45611, with its firewall rule and startup task registered. Nothing was clicked: no send, no receive, no notification, no share sheet, no tray menu. That, and the test peer on the Linux server, is Verify's.**
- **Dated 2026-11-17, during Verify:** the tray popup is brought to the front with focus when it opens, and the share window is always on top (Finn's call: both opened behind other windows). Each reinstall bumps the package version's last number (an install of the same version with different contents is refused). The manifest's minimum is 10.0.17763, the Windows App SDK floor, below the 10.0.19041 named here; only this Windows 11 laptop runs it.
- Windows App SDK **1.8.260921001** and H.NotifyIcon.WinUI **2.4.1**; the app is self-contained. `windows/NuGet.Config` pins nuget.org because this laptop has no NuGet source configured.

## Layout
All under `windows/`, one solution:
- `windows/Tailshare.Core/` — `net10.0` class library: protocol client and receiver, transfer and bundle (zip) handling, file naming, the settings/trust store, the Tailscale CLI wrapper, notification text formatting. **Uses no Windows-only API**, because the test peer runs it on Linux. Takes the receive folder, the settings file path and a "prompt the user" callback from its host; it never decides those itself.
- `windows/Tailshare.App/` — the WinUI 3 app (Windows App SDK, packaged as MSIX): tray icon, popup, share target, notifications, startup task, single-instance handling. Hosts Core.
- `windows/Tailshare.TestPeer/` — `net10.0` console app over Core; never shipped, only for Verify.
- `windows/Tailshare.Tests/` — xUnit tests over Core.

Invariants (add them to CLAUDE.md's Codebase Map when the directories exist):
- Only Core's receiver writes into the receive folder, and only inside it.
- Only Core's settings store writes the settings file.
- Core never references Windows-only APIs.

Libraries (Finn approved 2026-11-16): **H.NotifyIcon.WinUI** for the tray icon, **ASP.NET Core Kestrel** (shared framework, `Microsoft.AspNetCore.App`) for the receiver, **Windows App SDK app notifications** (`AppNotificationManager`) for notifications, **`System.IO.Compression`** for zip, **xUnit** for tests. Record each exact version in CLAUDE.md's Tech Stack when it's added. Nothing else without asking.

Minimum Windows version: the manifest declares 10.0.17763, the Windows App SDK floor (the plan said 10.0.19041; only this laptop, on Windows 11, runs it today).

## Tailscale integration
Core shells out to the Tailscale CLI (`tailscale` on PATH, else `C:\Program Files\Tailscale\tailscale.exe` on Windows; on Linux the test peer uses PATH). Two calls only:
- `tailscale status --json` — this device's Tailscale IPv4 (`Self.TailscaleIPs`, the address in 100.64.0.0/10) and its peers (`Peer.*`: `ID` is the stable node ID, `DNSName`, `TailscaleIPs`, `Online`).
- `tailscale whois --json <ip>` — who is behind an incoming connection: `Node.StableID`, `Node.ComputedName`, `UserProfile`.

Field names were checked against Tailscale 1.104.5 (laptop) and 1.104.2 (Linux server) on 2026-11-16. Both calls work there without admin or root.

**Device name** shown everywhere is the Tailscale machine name: the first label of `DNSName` for peers, `Node.ComputedName` from whois. Never a name the remote device sends about itself.

**Tailscale down or logged out:** the receiver doesn't listen, the popup's device list shows "Tailscale not connected", and the app re-checks `tailscale status` every 10 seconds. The same 10-second check notices when this device's Tailscale IP changes, and rebinds the receiver.

## Wire protocol v1
Plain HTTP/1.1 over TCP to the peer's Tailscale IPv4, **port 45611**. No TLS: tailnet traffic is already encrypted and mutually authenticated by WireGuard, and the source IP of a tailnet connection is bound to the sending node, so `whois` on it is a trustworthy identity.

**Every request**, before anything else: the receiver runs `whois` on the remote IP. If that fails (not a tailnet address, unknown node) it answers `403` and does nothing more. The resulting `StableID` is the sender's identity for the rest of the request.

All JSON is UTF-8, camelCase.

### `GET /v1/hello`
`200 {"app":"tailshare","protocol":1}`. No trust check (it reveals nothing). Discovery: the sender's device list is the online peers (from `status`) that answer `hello` with `protocol: 1` within 2 seconds, probed in parallel; this device is never listed.

### `POST /v1/offer`
Request body:
```json
{ "kind": "text" | "file" | "bundle",
  "name": "photo.jpg",
  "items": 1,
  "isFolder": false,
  "bytes": 123456,
  "totalSize": 123456 }
```
- `kind`: `text` is UTF-8 text; `file` is exactly one file sent as-is; `bundle` is a zip (anything other than one plain file: several files, any folder, or a mix).
- `name`: the file name (`file`), the first top-level item's name (`bundle`), absent for `text`.
- `items`: number of top-level items (1 for `text` and `file`).
- `isFolder`: for a `bundle` of one top-level item, whether that item is a folder; false otherwise.
- `bytes`: exact length of the upload body that will follow.
- `totalSize`: sum of the original file sizes (equals `bytes` for `text` and `file`). Shown to the user, and the cap on what extracting the bundle may write.

The receiver decides, reading the settings file fresh for every offer, so hand edits apply without a restart:
- sender's ID in `blocked` → `403 {"error":"blocked"}` at once, no prompt (blocked wins over trusted);
- in `trusted` → `200 {"token":"…"}` at once;
- otherwise → the first-contact prompt (below); the request stays open until it's answered or **120 seconds** pass. Accept → add the sender to `trusted` and `200 {"token":"…"}`. Decline → `403 {"error":"declined"}`, and the sender is added to neither list. Timeout → `403 {"error":"timeout"}`. If the sender closes the connection while waiting, the prompt is withdrawn.
- `text` with `bytes` over 1 MiB → `413` (text is held in memory and shown in a notification; files have no size limit).
- malformed offer → `400`.

The sender's HTTP timeout for `offer` is 130 seconds.

**Token**: 128 random bits from a cryptographic RNG, base64url. Single use, bound to the sender's `StableID`, expires 60 seconds after issue if no upload starts.

### `PUT /v1/upload/{token}`
Body: exactly `bytes` bytes (`Content-Length` must equal the offer's `bytes`). The token must exist, be unexpired, and belong to the requesting `StableID`; anything else → `403`, nothing written. The token is consumed when the upload starts.
- The receiver streams the body into a hidden temp file `.tailshare-<random>.part` in the receive folder (or memory, for `text`), then finishes it (below) and answers `200 {"ok":true}`.
- A short body, a dropped connection, a receiver-side Cancel, or a failed finish: the temp file and anything partly extracted are deleted, and the sender sees a failure.

### Bundles
A zip whose entries use `/` separators and relative paths, each top-level item at the root (a folder as `Photos/…`). Compression level `Fastest`: bundling exists to cut per-file overhead, and most large media is already compressed. The sender writes the zip to a temp file first (so `bytes` is known), and deletes it afterwards.

## Receiving
**Receive folder**: the settings file's `receiveFolder`, or the user's Downloads folder (the Windows known-folder Downloads, not a guessed path) when it's absent, null, or doesn't exist.

**Names**: every name from a peer is sanitized before use: path components stripped, characters invalid on the platform replaced with `_`, Windows reserved names (`CON`, `NUL`, `COM1`…) prefixed with `_`, length capped at 200, an empty result becomes `file`. A name that's taken gets the first free suffix of `name (1).ext`, `name (2).ext`, … (a folder: `Photos (1)`).

**Finishing**:
- `file`: the temp file is renamed to its final name in the receive folder.
- `bundle`: each top-level item is extracted into the receive folder under its own collision-free name; entries inside a folder keep their relative paths. An entry that is absolute, has a drive or `..` segment, or would resolve outside its item's folder fails the whole transfer, and nothing is left behind. Extraction also fails once it has written more than the offer's `totalSize`.
- `text`: nothing is written to disk.

**Notifications** (Windows App SDK app notifications; titles and bodies built by Core so they're testable):
- First contact: title `<device>`, body `wants to send <what>` where `<what>` is `photo.jpg, 2.4 MB`, `photo.jpg and 2 more, 12.4 MB`, `Photos (folder), 1.2 GB`, or `text`. Buttons **Accept** and **Decline**. Stays on screen until acted on (reminder scenario); removed on timeout or withdrawal.
- Received file or bundle: title `From <device>`, body `<what>` as above. Click opens Explorer at the receive folder with the item selected (one top-level item), or just the receive folder (several).
- Received text: title `From <device>`, body the text, truncated to 200 characters with `…`. Click copies the full text to the clipboard.
- Received link (the trimmed text is a single absolute `http`/`https` URL with no whitespace): body the URL. Click opens it in the default browser; a **Copy** button copies it. Any other scheme is treated as plain text and never opened.
- Big incoming transfers (`bytes` over 20 MB): a progress notification `Receiving <what> from <device>` with a progress bar and a **Cancel** button, replaced by the received notification when done.
- Text content is never logged.

**Sizes** are shown in binary units with one decimal (`2.4 MB`, `1.2 GB`; bytes under 1 KB as `512 B`).

**Port 45611 already in use** at startup: a notification `Can't receive: port 45611 is in use`, and the tray tooltip says the same. Sending still works.

## Settings file
`settings.json` in the packaged app's local folder (`ApplicationData.Current.LocalFolder`). Hand-editable:
```json
{
  "receiveFolder": null,
  "port": null,
  "trusted": [ { "id": "<StableID>", "name": "<machine name when added>" } ],
  "blocked": [ { "id": "<StableID>", "name": "<machine name>" } ]
}
```
- Missing file = all defaults. Written only when Accept adds a trusted device, atomically (temp file, then replace).
- `blocked` is only ever filled by hand. `port` is null or absent for the default 45611.
- **Malformed file**: treated as all defaults (everyone gets prompted, Downloads is used) and **never overwritten**; an Accept while it's malformed lets that transfer through but isn't saved.

## Sending
What gets sent: one plain file → `file`; any other set of files and folders → `bundle`; text → `text`.

**Tray popup** (left-click the tray icon; right-click menu: **Open**, **Quit**). It comes to the front with focus whenever it opens. Standard WinUI controls only, per CLAUDE.md's Frontend rules:
- Device list, refreshed whenever the popup opens, plus a **Refresh** button. States: the list; `No devices`; `Tailscale not connected`.
- A **Files** / **Text** switch (a send is one or the other):
  - Files: **Browse** (multi-select files), **Folder** (pick a folder), and dropping files or folders onto the popup; a list of what's picked, each removable.
  - Text: a multi-line box.
- **Send**, enabled once a device is selected and there's something to send.
- Status line with a progress bar: `Waiting for <device>`, sending progress, `Sent`, `<device> declined`, `<device> didn't respond`, `Failed`. A **Cancel** button while waiting or sending.

**Share sheet**: the MSIX manifest registers a share target for text, web links and files (storage items, which may include folders). Sharing opens a small window, always on top, with the device list and **Send**; it shows the same waiting/progress status and **Cancel**, closes when the send ends, and the result is a notification: `Sent to <device>`, `<device> declined`, `<device> didn't respond`, or `Couldn't send to <device>`. A shared web link is sent as `text` (the receiver's link rule makes it clickable).

**Single instance**: a share activation while the app is running is redirected to the running instance.

## Packaging and startup
- MSIX with a startup task (requested on first run, so the app launches at login; the user can turn it off in Windows' Startup apps settings) and a firewall rule for inbound TCP 45611 declared in the manifest.
- The receiver binds only to this device's Tailscale IPv4, never to all interfaces.
- **Signing**: a self-signed certificate made and trusted on this laptop only. The certificate and its key never enter the repo (`.gitignore` already blocks `*.pfx`/`*.cer`). Creating it writes the user certificate store (HKCU-backed), which the Claude app's shell redirects, so the creation and the admin trust command (into `LocalMachine\TrustedPeople`) go to Finn as commands for a normal terminal. Implement records the build, sign and install commands verbatim in CLAUDE.md's Commands.
- Whether installing the package (`Add-AppxPackage`) works from the Claude app's shell is unknown; try it once, and if it doesn't, hand Finn the command.

## Test peer
`Tailshare.TestPeer`, a console app over Core, for Verify only:
- `devices` — prints the discovered tailshare devices.
- `send <device> <path>…` / `send <device> --text "<text>"`.
- `listen --into <dir> [--accept | --decline | --ask]` — receives on its own Tailscale IP, port 45611. Prompts on stdin with `--ask`, and keeps its own settings file beside it.

It's published self-contained for Linux (`dotnet publish windows/Tailshare.TestPeer -c Release -r linux-x64 --self-contained -o <out>`), copied to Finn's Linux server over its key-based SSH alias (Finn gives the alias in chat; it isn't written in the repo), and run from a temp folder there. Verify stops it and deletes that folder afterwards. As of 2026-11-16 the server's port 45611 was free.

## Security checklist
- Listen only on the Tailscale IPv4.
- `whois` every connection; identity is the `StableID` from `whois`, never anything in the request.
- Tokens: crypto RNG, single use, sender-bound, 60-second expiry.
- Sanitized names, zip-slip and `totalSize` caps on extraction, partial output always removed.
- Links: only `http`/`https` are ever opened.
- No text content or file names in logs.
- No real device names, tailnet names or IPs in code, tests or docs (fixtures use placeholders).

## Tests
xUnit over Core. There are no existing rules for these clauses to replace. The suite should pin:
- **Names**:
  - an existing `a.txt` yields `a (1).txt`, and `a (2).txt` when that is taken too;
  - names without an extension, and folders, follow the same pattern;
  - path components are stripped;
  - invalid characters are replaced, reserved names prefixed, length capped, and an empty name becomes `file`.
- **Bundles**:
  - an entry with `..`, an absolute path or a drive fails the transfer and leaves nothing in the receive folder;
  - extraction past `totalSize` fails and leaves nothing;
  - folder and loose-file bundles land under collision-free top-level names.
- **Sender kind**: one file gives `file`; two files, a folder, or a mix gives `bundle`; text gives `text`.
- **Trust**:
  - an unknown device is prompted, a trusted device isn't, and a blocked device is rejected without a prompt even when it's also trusted;
  - Decline adds the device to neither list;
  - Accept adds it to `trusted` with its name;
  - a hand edit is seen on the next offer without a restart;
  - a malformed file prompts everyone, isn't overwritten, and an Accept while it's malformed doesn't persist.
- **Receive folder**: unset or null, set, and set to a missing folder (falls back to Downloads).
- **Protocol**, against an in-process receiver on loopback with `whois` stubbed:
  - `hello`;
  - `offer`: declined → `declined`, timed out (with a short injected timeout) → `timeout`, blocked → `blocked`;
  - token single use, token presented by another `StableID` → `403`, expired token → `403`;
  - `Content-Length` not matching `bytes` → rejected with no file left;
  - `whois` failure → `403`;
  - text over 1 MiB → `413`;
  - a successful file, bundle and text round trip.
- **Link detection**: a single `https://` URL is a link; text containing a URL is text; `file:`, `javascript:` and other schemes are text.
- **Notification text**: truncation at 200 characters; size formatting at the B/KB/MB/GB boundaries; `and N more`; the folder form.

The tray, popup, share target, toasts, startup task and firewall rule aren't unit-tested. Verify exercises them by hand against the test peer, in both directions, including first contact (Accept and Decline), timeout, Cancel on each side, a big file with the progress notification, a folder, a link and plain text.
