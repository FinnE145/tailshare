# Shortcuts for iPhone and iPad

Two Shortcuts talk to the hub (`windows/Tailshare.Hub/`) and to the tailshare computers over Tailscale. Spec: `docs/specs/ios-shortcuts-E.md`, *The Shortcuts*. **Status (2026-11-19): the unsigned files are written by `generate.py` and have run on the Mac.** Not on a device yet; Verify does that and records what it finds here.

What ran, on the Mac, from a build pointed at a local echo server (`generate.py --port ... --hub ... --headless`, run with `shortcuts run`):
- **Send:** a text file went as a `text` offer then a `text/plain` PUT; a PNG as a `file` offer (name, `totalSize` 70 as a JSON number, `sizeIsEstimate: true`, no `bytes`) then an `image/png` PUT with `Content-Length: 70`; two files as a `bundle` offer (first item's name, `items: 2`, `totalSize` the sum) then an `application/zip` PUT. Shortcuts sends `Content-Length` on uploads here.
- **Get:** register, the pickup list, the item fetch, text copied to the clipboard. A file first shows a popup (`Download from <computer>?`, then its name and size, OK or Cancel), then opens in Quick Look when it is an image, a PDF or text, and goes to the Save picker otherwise. A bundle gets the same popup (`Download files from <computer>?`) and then the picker. Cancel stops the Shortcut, and what wasn't downloaded stays held until its time runs out. Quick Look showed a real image; the type names `PDF` and `Text` are untested.
- **Import:** the hub-name question shows at import with its default.

Not run: choosing a device from the list, the link menu, the Save picker for a file or an unzipped bundle, Quick Look on a PDF or text, the result notifications, a refused or declined offer, and everything on an iPhone or iPad.

Things learned the hard way, so the generator stays as it is:
- Loops: a loop over a variable that holds a one-item list runs zero times. Loop over the action's output. A loop inside a loop names its item `Repeat Item 2`.
- Conditions are numbers in the file (`4` equals, `100` has a value, `101` has no value); the words are silently "otherwise". Compare dictionary values only after turning them into text.
- Match Text is an Apple intent action that can't be given its text from a file (it asks when it runs). A link is detected with Replace Text and a regular expression instead: if removing the pattern leaves nothing, the whole text was one link.
- macOS asks "Allow to connect" and "Allow to send" once per Shortcut and host, and again to write the clipboard. Expect the same on the iPhone, once per computer; Always Allow stops them.

- **Send with tailshare**: in the share sheet. Sends text, links, images and files to a computer.
- **Get from tailshare**: run by hand (home screen, widget, Back Tap or the Action button). Fetches what a computer is holding for this device.

Both start with `POST /v1/register`, so the first run of either one makes the device show up in every computer's device list. Both ask for the hub's Tailscale machine name when they are imported, and keep it. Requests go to `http://<name>:45611`. No device names are written in these files.

## Putting them on a device
- The unsigned files are XML property lists. They are written by `python3 shortcuts/generate.py` (no dependencies); change the script and regenerate, never edit the files by hand. Signed files are **never committed** (`signed/` is ignored): the signature can carry the signer's Apple ID.
- Sign on the Mac, from this folder:

  ```bash
  shortcuts sign --mode anyone --input "Send with tailshare.shortcut" --output "signed/Send with tailshare.shortcut"
  ```

- Then AirDrop the signed file to the iPhone, or share it from the Shortcuts app on the Mac as an iCloud link for someone else's iPad. Opening it asks for the hub's name.
- A shortcut that won't sign or import is built by hand from the recipe below. The recipe is also the readable description of what each one does.

## Recipe

Names in `{braces}` are Shortcuts variables (Set Variable). Every request is **Get Contents of URL**. A failed request stops the Shortcut with Shortcuts' own message; there is no error handling to do better.

### Register (first step of both)
1. **Text** `HUB` (the hub's machine name; this is the import question, "Hub name"). **Set Variable** `{hub}`.
2. **Get Contents of URL**: `http://{hub}:45611/v1/register`, method **POST**, no body.
3. **Get Dictionary Value** `computers` from the result. **Set Variable** `{computers}`.
4. If `{computers}` has no value: **Show Alert** `Can't reach the hub`, **Stop This Shortcut**.

### Send with tailshare
Share sheet: accepts text, URLs, images and files. Start with Register.
1. **Repeat with Each** item of `{computers}`: **Get Dictionary Value** `name` from the item, **Add to Variable** `{names}`. **Choose from List** `{names}`; if there are none, **Show Alert** `No devices` and stop. **Set Variable** `{device}`.
2. Find the address: **Repeat with Each** item of `{computers}`; **If** its `name` is `{device}`, **Get Dictionary Value** `address`, **Set Variable** `{address}`.
3. **Text or link** (the input is text or a URL): offer body, **JSON**: `kind` = `text`, `items` = `1`, `isFolder` = `false`. No `bytes`, no `totalSize`. The text itself is the upload body.
4. **One file or image**: `kind` = `file`, `name` = the file name, `items` = `1`, `isFolder` = `false`, `totalSize` = the file size in bytes (Get Details of Files, File Size, as a number), `sizeIsEstimate` = `true`. No `bytes`.
5. **Several items**: **Make Archive** (zip) of them. `kind` = `bundle`, `name` = the first item's name, `items` = the count, `isFolder` = `false`, `totalSize` = the sum of the file sizes, `sizeIsEstimate` = `true`. No `bytes`. The archive is the upload body.
6. **Get Contents of URL**: `http://{address}:45611/v1/offer`, method **POST**, body **JSON** as above. If the reply has no `token`: read `error` from it; `declined` shows `{device} declined`, `timeout` shows `{device} didn't respond`, anything else shows `Couldn't send to {device}`. Stop.
7. **Get Contents of URL**: `http://{address}:45611/v1/upload/{token}`, method **PUT**, body **File** (the text, file or archive). Show `Sent to {device}`.

An `offer` to a computer that hasn't trusted this device stays open until the person there answers, up to 120 seconds. How long Shortcuts waits is unknown (see below).

### Get from tailshare
Run by hand. Start with Register. Then **Repeat with Each** computer in `{computers}`:
1. **Get Dictionary Value** `address`. **Get Contents of URL** `http://{address}:45611/v1/pickup` (GET). The result is a list of `{token, kind, name, bytes}`; **Repeat with Each** item:
2. **Get Contents of URL** `http://{address}:45611/v1/pickup/{token}` (GET). Add 1 to `{count}`.
3. `kind` = `text`: if the trimmed text matches `^https?://\S+$` (one link), **Choose from Menu** `Open` / `Copy`: Open runs **Open URLs**, Copy runs **Copy to Clipboard**. Any other text: **Copy to Clipboard**, then **Show Result**.
4. `kind` = `file`: **Save File** (ask where to save).
5. `kind` = `bundle`: **Extract Archive**, then **Save File** (ask where to save). If extracting fails, save the zip as it is.
6. After the loops, if `{count}` is 0: **Show Alert** `Nothing to pick up`.

## Not known yet (Verify measures these on a real iPhone)
- Whether Shortcuts sends `Content-Length` on an upload, and how exact its file size is. The protocol covers both: `bytes` is optional and `sizeIsEstimate` loosens the unzip cap.
- How long Shortcuts waits on a request. An offer to a computer that hasn't trusted the phone stays open for up to 120 seconds.
- Whether Shortcuts allows plain `http://` to a tailnet name.
