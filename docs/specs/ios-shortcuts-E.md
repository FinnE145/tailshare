# iPhone and iPad through Shortcuts, with a hub (E)

Step E of `docs/Planning/roadmap.md` (added by this plan session; it draws on the *More platforms* feature idea).

**Estimate** (from recorded durations: A's Implement took about 50 minutes against a 5-hour estimate and its Verify about 1 hour; B recorded no durations. Planning ran 2026-11-18 and reached this spec at about 21:55 EST, with long waits for answers in between):
- Implement, laptop (Core, the hub, the Windows app, tests and mutants): about 1.5 hours wall time; about 10 minutes of Finn's hands-on time (install and a look at the popup).
- Deploy, the server (its own session): about 30 minutes wall time; about 10 minutes hands-on (one `run-reviewed` script, the Tailscale SSH check).
- Implement, Mac (TailshareCore, the Mac app, the two Shortcuts): about 2 hours wall time, because the Shortcut file format is undocumented; about 20 minutes hands-on (signing in to Shortcuts if asked, importing the Shortcuts on the iPhone).
- Verify (also B's Verify, folded in by Finn's call): about 2.5 hours wall time; about 1.5 hours hands-on (iPhone, iPad, both computers).

## What this step delivers
1. **Wire protocol v1 additions** (below), in both Cores: `bytes` becomes optional, an offer can say its size is an estimate, `hello` can mark a hub, and a computer can hold an item for a phone or iPad to pick up. Nothing in v1 is removed; `hello` still answers `protocol: 1`. **This section is authoritative for the additions; `docs/specs/windows-app-A.md` stays authoritative for everything else in v1.**
2. **The hub** (`windows/Tailshare.Hub/`): a headless service over Core, deployed on Finn's always-on Linux home server ("the server" below; its name is not written in the repo). It keeps a list of registered phones and iPads and answers who is online. It never receives or sends files.
3. **The Windows app and the Mac app**: phones and iPads that the hub lists appear in the device list as pickup targets; sending to one holds the item for 120 seconds; the `hub` setting.
4. **Two Shortcuts** (`shortcuts/`), for iPhone and iPad: **Send with tailshare** (share sheet) and **Get from tailshare** (run by hand).

Works across Tailscale accounts on one tailnet: nothing uses Taildrop.

Not in this step: a tailshare CLI for headless machines (a feature idea, by Finn's call); notifications on the phone (a manual pull is enough, Finn); native iOS or Android apps.

**B is folded in.** B's Mac app was merged into `main` without its Verify (Finn, 2026-11-18). E's Verify also checks everything `docs/specs/mac-app-B.md` promises, and records its findings in B's spec as well as this one.

## Where the implementation departed
Dated 2026-11-18, during the laptop's Implement (Core, the hub, the Windows app). Each is also fixed in line below. The server and Mac sessions read this list before the rest.
- **Pickup listing `name` is the item's own name; a bundle's download file name adds `.zip`.** The spec's `<name>.zip` described the download only. The Shortcut can show the listing's name as is.
- **`Content-Disposition` carries an ASCII fallback in `filename` and the real name in `filename*=UTF-8''…`** when the name isn't ASCII. Names go through the same sanitizing as received names first (a quote or control character becomes `_`). Kestrel refuses a non-ASCII header value, so a plain `filename="é.txt"` would have failed the download.
- **A held file that is gone when it is picked up answers `404`** (the spec said only that the download fails), and the sender's hold ends `Failed`.
- **A hold that ends `Failed` also raises a notification, `Couldn't send to <device>`.** The spec listed only picked up and not picked up; a failure with no notification would leave a closed share window with no word at all. A cancel raises none, as specced.
- **The bundle cap is `totalSize + totalSize / 4 + 1 MiB` in integer arithmetic**, which is `totalSize × 1.25 + 1 MiB` rounded down.
- **Device names in the hub's `computers` and `mobiles` are Tailscale's current names from `status`**, not the name stored at registration (which is kept in the file for reading). A renamed phone needs no new registration. A device that is registered but not online isn't listed, as specced.
- **`whois` runs before routing in the hub**, so a caller Tailscale doesn't know gets `403` even for a path the hub doesn't serve; a tailnet caller gets `404` there.
- **The hub assumes every computer listens on the default port 45611.** It has no settings file; an app whose `port` setting is changed is invisible to it.
- **`PickupStore` (Core) is owned by the host and handed to the receiver** (`ReceiverOptions.Pickups`), so the tray app's 10-second rebind doesn't drop a hold. `HoldAsync` returns `PickedUp`, `NotPickedUp`, `Failed` or `Cancelled`; the app's controller turns it into the status line and the notification.
- **The Windows share window sends once.** Its Send button stays off after the click, also while a hold only waits, because the window closes when the hold ends.
- **The compose file takes its data directory from `HUB_DATA` (default `./data`)**, so the server session can point it at the stack's own `data/`. The Docker build context is `windows/` (`windows/.dockerignore` keeps `bin`/`obj` out of it). Neither file was built here: Docker isn't installed on the laptop, so the server session's build is the first.

## Decisions (Finn, 2026-11-18)
- Shortcuts in both directions; no phone notification; a manual pull is fine.
- A hub on the server, which is always on. A phone or iPad **registers itself the first time either Shortcut runs**, so every computer can offer it as a target without a send in the other direction first (people used to AirDrop won't put up with a two-way setup).
- The hub is a device list and registry only; no receiving.
- Computers find the hub themselves from `hello` on every refresh, never cache it, and a `hub` setting overrides that. The app never writes `hub`.
- Computers keep finding other computers themselves and add the hub's phones and iPads, so a hub that's down costs only the mobile targets.
- Anyone on the tailnet (`whois`-identified) may register and ask the hub for its list: the hub has no screen to prompt on, and the list holds only names Tailscale already shows them.
- A registered device is listed only while Tailscale shows it online, so expired, offline and removed devices drop out by themselves. Unregistering is a hand edit of the hub's file.
- `bytes` optional. Several files from a phone go as one bundle with a best-guess `totalSize` and a flag saying it's a guess; the unzip cap is loosened for flagged offers.
- Held items wait **120 seconds** (the same as the first-contact timeout).
- What can be held: text, one file, or a bundle (several files or a folder). The Shortcut unzips a bundle; if unzipping fails on the first try, it saves the zip to Files instead.
- On the phone: text goes to the clipboard and is shown; a link asks **Open** or **Copy**; a file goes to Save to Files.
- On the computer: the status line shows `Waiting for <device> to pick up` with **Cancel**; a notification when it's picked up and when it isn't (and `Couldn't send to <device>` when it fails).
- The Shortcuts are authored as files in the repo and signed on the Mac; if that fails on the first try, they become a written recipe Finn builds by hand.
- The server deploys the hub as a Docker stack under `svc`, on the host network, listening only on its tailnet IPv4, with the Tailscale socket and CLI mounted. The hub replaces the test peer on the server; Verify uses the laptop and the Mac as the two computers.
- One branch, `feat/ios-shortcuts-E`. Order: **laptop → server → Mac**. The laptop writes Core, the hub (code, tests, Dockerfile and compose) and the Windows app; a session on the server only deploys; the Mac writes TailshareCore, the Mac app and the Shortcuts, which need the live hub to test against.

## Wire protocol additions
Everything in A's protocol still holds unless changed here: `whois` on every request and `403` when it fails, identity is the `StableID` from `whois`, listen only on the Tailscale IPv4, port 45611 (or `port` in the settings file).

### `hello`
A hub answers `200 {"app":"tailshare","protocol":1,"hub":true}`. Apps answer as before (no `hub` field, which means false). A device list never shows a device whose `hello` says `hub: true` as a send target.

### `offer`: optional `bytes`, estimated size
```json
{ "kind": "text" | "file" | "bundle",
  "name": "photo.jpg",
  "items": 1,
  "isFolder": false,
  "bytes": 123456,
  "totalSize": 123456,
  "sizeIsEstimate": false }
```
- `bytes` may be omitted (or null). Then the upload has no declared length: the receiver takes the body as it arrives, with or without `Content-Length` (chunked included), until it ends. When `bytes` is present, A's rule stands: `Content-Length` must equal it, or `400`.
- For `text`, `totalSize` may be omitted too (treated as 0, and the first-contact prompt says `text` as before). For `file` and `bundle` it stays required.
- `sizeIsEstimate` (optional, default false): `totalSize` is the sender's best guess. Shown the same way. For a `bundle`, the extraction cap becomes `totalSize × 1.25 + 1 MiB` (rounded down: `totalSize + totalSize / 4 + 1 MiB` in integers) instead of `totalSize`.
- The text limit (1 MiB) still applies. With `bytes` present, it's checked at offer time as before (`413`). Without it, the upload stops and fails with `413` once the body passes 1 MiB, and nothing is kept.
- The big-transfer progress notification (over 20 MB) uses `bytes`, or `totalSize` when `bytes` is absent, as its total.

The apps' own senders keep sending `bytes` and never set `sizeIsEstimate`.

### Pickup (served by the Windows and Mac apps)
A computer holds an item for one device, identified by its `StableID` (from the hub's list), for **120 seconds** from when the send starts. Holding happens in the app's receiver process.
- `GET /v1/pickup` → `200 [{"token":"…","kind":"text"|"file"|"bundle","name":"…","bytes":123}]`: every item held for the caller that is still waiting (not one being downloaded), oldest first; `[]` when there are none. `name` is absent for `text`; for a bundle it is the bundle's own name, without `.zip`. Never lists items held for anyone else.
- `GET /v1/pickup/{token}` → the item's body: UTF-8 text (`text/plain; charset=utf-8`), the file, or the bundle's zip (`application/octet-stream` with `Content-Disposition: attachment; filename="<name>"`, `<name>.zip` for a bundle; a name that isn't ASCII is also given as `filename*=UTF-8''<percent-encoded>`, with `_` for each such character in `filename`). The token must exist, be unexpired, and belong to the caller; anything else → `403`. The token is consumed when the download starts; a download that started before the 120 seconds ran out runs to the end.
- **Token**: 128 random bits from a cryptographic RNG, base64url, bound to the target's `StableID`, single use, dies with the hold.
- **What's held**: text in memory; a single file by its path, read when it's picked up (if it's gone by then, the download answers `404` and the sender sees `Failed`); a bundle is zipped to a temp file when the send starts (`Preparing`), and the temp file is deleted when the hold ends, however it ends.
- A hold ends when it's picked up, cancelled, after 120 seconds, or when the app quits. Nothing is held across a restart.
- There is no trust check on pickup: the computer's user chose the target, and only that device can fetch.

### Hub endpoints
- `GET /v1/devices` → `200 {"computers":[{"name":"…","address":"100.x.y.z"}],"mobiles":[{"name":"…","id":"<StableID>"}]}`:
  - `computers`: online peers (from the hub's `tailscale status`) that answer `hello` with `protocol: 1` within 2 seconds and are not hubs, probed in parallel like an app's device list. `address` is the peer's Tailscale IPv4.
  - `mobiles`: registered devices that `tailscale status` shows online right now.
  - The caller itself is never in either list.
  - Names are Tailscale machine names, as in A.
- `POST /v1/register` (no body) → registers the caller (its `StableID` and `ComputedName` from `whois`), updating the stored name if it changed, then answers exactly as `GET /v1/devices`. Calling it again is harmless.
- Anything else, including `offer` and `upload` → `404`.

## The hub (`windows/Tailshare.Hub/`)
A `net10.0` console app over `Tailshare.Core`, added to `windows/Tailshare.sln`. Like Core, it uses no Windows-only API.
- Listens on the server's Tailscale IPv4, port 45611 (the address found the way Core's receiver finds it, with the same 10-second re-check and rebind).
- Registry file: `registry.json` in its data directory (`--data <dir>`, `/data` in the container), shape `{"devices":[{"id":"<StableID>","name":"<machine name>"}]}`, written atomically (temp file, then replace), only by the hub's registry store. A missing file is an empty list. A malformed file is treated as empty, never overwritten, and registering while it's malformed answers the list but saves nothing (A's settings rule).
- Logs requests by device name and endpoint only.
- **Deploy files in the repo** (`windows/Tailshare.Hub/deploy/`), with no real names or addresses in them:
  - `Dockerfile`: builds from `windows/` with the .NET 10 SDK image, runs on the ASP.NET 10 runtime image, `USER 2003:2003`.
  - `compose.yaml`: `name: tailshare-hub`, `network_mode: host`, `user: "2003:2003"`, `restart: unless-stopped`, the stack's `data/` mounted at `/data`, the host's `/var/run/tailscale/tailscaled.sock` and `/usr/bin/tailscale` mounted read-only.
  - The hub finds its own address from Tailscale at runtime, so the compose file carries none.

### Deploying on the server (its own session)
Finn opens a session on the server, which clones the repo and reads this section and the server's own `SERVER.md` (which takes precedence there). That session:
- Follows the server's built-from-source stack shape (`/srv/stacks/<name>/` with the clone in `repo/` and the compose inside the repo), under the `svc` account, with `data/` made by a `run-reviewed` script Finn runs.
- Checks that `tailscale whois` and `tailscale status` work as uid 2003 through the mounted socket (they work as the server's agent user, 2026-11-18). If they don't, it stops and asks.
- Checks port 45611 is free (it was, 2026-11-18) and that nothing new binds `0.0.0.0`. No firewall change is expected: the hub listens on the tailnet address only.
- Writes the server's `services/<name>.md` and its row in `SERVER.md`'s index, as `SERVER.md` requires, and tells Finn.
- Edits nothing in the repo; a bug in the hub goes back to Finn.

## Apps: the `hub` setting and the device list
- **Settings file** (both apps) gains `hub`: null, absent or `""` = find the hub automatically; otherwise a Tailscale machine name or a Tailscale IPv4. Read fresh on every refresh. **Never written by the app**, and kept as it is whenever the settings store writes the file (on Accept).
- **Refresh** (popup and share window, both apps): discover computers as before. The `hello` probe already reaches every online peer, so a hub is found in the same pass, at no extra cost. With `hub` set, that device is the hub instead. With several hubs and no setting, the first by name. If there's a hub, ask it `GET /v1/devices` and add its `mobiles`, excluding any device already listed. If there's no hub or it doesn't answer within 2 seconds, the list simply has no phones or iPads, with no error shown.
- **Mobile rows** carry a phone icon (SF Symbol `iphone` on the Mac, Segoe Fluent Icons `CellPhone` on Windows) beside the name. The button still reads `Send to <device>`.

## Apps: sending to a phone or iPad
Anything that can be sent can be held: text, one file, or a bundle.
- Status line: `Preparing` (a bundle), `Waiting for <device> to pick up` with **Cancel**, a progress bar while it's being downloaded, then `Picked up by <device>`, `Not picked up`, `Failed` or `Cancelled`.
- A hold doesn't block another send. The status line shows the most recent send; every hold ends in a notification: `Picked up by <device>` or `Not picked up by <device>`. Cancelling raises none.
- The share window (both apps) behaves as it does for a computer: it shows the waiting status and **Cancel**, closes when the hold ends, and the result is the notification above.
- The Mac's Share Extension hands off to the main app as before; the hold lives in the main app.

## The Shortcuts (`shortcuts/`)
Authored on the Mac. The repo holds the **unsigned** Shortcut files (XML property lists, readable in a diff), and a `README.md` there with the signing command (`shortcuts sign --mode anyone --input <file> --output <signed file>`), how to put them on a device (AirDrop, or an iCloud link for someone else's iPad), and the recipe if signing fails. **Signed files are never committed** (`.gitignore`): the signature may carry the signer's Apple ID. No device names in the files: both Shortcuts ask for the hub's name when they're imported (Shortcuts' import questions) and keep it.

Both start with `POST /v1/register` to the hub, so either Shortcut's first run registers the device. Requests go to `http://<name>:45611`. If the hub can't be reached, the Shortcut stops with `Can't reach the hub`. A Shortcut that fails a request stops there with what went wrong; Shortcuts has no error handling to do better.

**Send with tailshare** (in the share sheet; accepts text, links, images and files):
1. Register; pick one of `computers` from a list (`No devices` if empty).
2. Text or a link → `text` offer without `bytes` or `totalSize`. One file or image → `file` offer: `name`, `items: 1`, `totalSize` from Shortcuts' file size, `sizeIsEstimate: true`, no `bytes`. Several → zipped with Make Archive into a `bundle` offer: `name` of the first item, `items` = the count, `isFolder: false`, `totalSize` = the sum of the file sizes, `sizeIsEstimate: true`, no `bytes`.
3. `POST /v1/offer`, then `PUT /v1/upload/{token}`. Shows `Sent to <device>`, `<device> declined`, `<device> didn't respond`, or `Couldn't send to <device>`.

**Get from tailshare** (run by hand: home screen, widget, Back Tap or the Action button):
1. Register; for each of `computers`, `GET /v1/pickup`, then fetch each item.
2. Text: copied to the clipboard and shown. A link (the same rule as A: the trimmed text is one `http`/`https` URL with no whitespace) asks **Open** or **Copy**.
3. A file: Save to Files. A bundle: unzipped, then Save to Files.
4. Nothing anywhere: `Nothing to pick up`.

**Known unknowns, for the Mac's Implement and for Verify, on a real iPhone**:
- Whether Shortcuts sends `Content-Length` for an upload, and how exact its file size is. The protocol covers both cases: `bytes` is optional and `sizeIsEstimate` loosens the cap.
- **How long Shortcuts waits on a request.** An `offer` to a computer that hasn't trusted the phone stays open for up to 120 seconds. If Shortcuts gives up sooner, first contact fails until the computer has accepted. Verify measures it and records the number; it doesn't work around it without asking.
- Whether Shortcuts allows plain `http://` to a tailnet name (the Mac app needed `NSAllowsArbitraryLoads` for the same thing, 2026-11-17).

## Codebase map additions (root `CLAUDE.md`)
- `windows/Tailshare.Hub/`: the hub, over Core, and its deploy files. Invariants: **only the hub's registry store writes `registry.json`; the hub never writes anything else, and never receives files.** Owned by the laptop's checkout.
- `shortcuts/`: the two Shortcuts (unsigned) and their README. Owned by the Mac's checkout.

## Security checklist
A's checklist holds for everything here. Also:
- The hub and the pickup endpoints run `whois` on every request; non-tailnet callers get `403`.
- The hub listens on the tailnet IPv4 only, runs as `svc` (not root, not Finn), and its only writable path is `/data`. Its code and image files stay root-owned.
- The Tailscale socket gives the hub read-only LocalAPI access: `svc` is not Tailscale's operator, so it can't change anything.
- Pickup tokens: crypto RNG, single use, bound to the target's `StableID`, gone after 120 seconds. `GET /v1/pickup` shows a caller only its own items.
- The pickup endpoints serve only what the user chose to send: a held file is read by the path picked at send time, never a path from the request.
- `register` takes no body: the name stored is the one from `whois`, never one the device sends.
- The loosened cap applies only when the offer says `sizeIsEstimate`, and only to bundles; a missing `bytes` never lifts the text limit.
- No text content or file names in logs (hub included).
- No real device names, tailnet names or addresses in code, tests, Shortcuts, the deploy files or docs. Signed Shortcuts never enter the repo.

## Tests
xUnit on Windows (Core and the hub), XCTest on the Mac (TailshareCore mirrors Core's protocol tests, as B's did). The Shortcuts have no automated tests; Verify runs them on the iPhone and an iPad.

- **Optional `bytes`** (replaces A's "`Content-Length` must equal `bytes`" for offers without it; the old rule stays for offers with it):
  - an offer without `bytes` is accepted; its upload is taken at whatever length arrives, with `Content-Length` and chunked;
  - an offer **with** `bytes` and a different `Content-Length` is still `400`, nothing written;
  - `text` without `bytes`: a body over 1 MiB fails with `413` and nothing is kept; at exactly 1 MiB it's accepted;
  - `text` without `totalSize` is accepted; `file` and `bundle` without `totalSize` are `400`.
- **Estimated size** (replaces A's "extraction fails once it has written more than `totalSize`" for flagged bundles only):
  - a flagged bundle that writes more than `totalSize` but no more than `totalSize × 1.25 + 1 MiB` is received;
  - a flagged bundle that writes more than that fails, and nothing is left behind;
  - an **unflagged** bundle that writes one byte over `totalSize` still fails.
- **`hello` and the hub mark**: the hub's `hello` carries `hub: true`; an app's doesn't; a device answering `hub: true` is never in the send list; with `hub` set, that device is used even when another hub answers.
- **Settings**: `hub` null, absent and `""` all mean automatic; an Accept rewrites the file with `hub` exactly as it was.
- **Hub**:
  - `register` stores the caller's `StableID` and `whois` name, and a second call with a new name updates it without adding a row;
  - `devices` lists a registered device only while `status` shows it online, and never the caller;
  - `computers` holds only peers that answered `hello` with `protocol: 1` and aren't hubs;
  - `whois` failure → `403` on every endpoint; `offer` and `upload` → `404`;
  - a malformed `registry.json` is answered as empty and left byte-for-byte unchanged after a `register`.
- **Pickup**:
  - `GET /v1/pickup` lists only the caller's items, oldest first; another device gets `[]`;
  - a token presented by another `StableID` → `403`; a second use → `403`; after 120 seconds (an injected short timeout) → `403` and the item is gone;
  - cancel removes the item and its token; a bundle's temp zip is deleted after pickup, cancel and expiry;
  - the body served is exactly the text, the file's bytes, or the zip, with the right name;
  - a held file deleted before pickup → the download fails and the sender sees `Failed`.
- **Notices**: `Picked up by <device>`, `Not picked up by <device>`, and the waiting status text, built by Core.
