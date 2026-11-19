#!/usr/bin/env python3
"""Writes the two unsigned Shortcut files (XML property lists) next to this script.

    python3 shortcuts/generate.py

The Shortcut file format is undocumented, so every action, parameter name and value below was checked against
the real thing on a Mac: the identifiers and parameter keys against WorkflowKit's own WFActions.plist, the
rest by importing a built file and running it with `shortcuts run` against a local echo server (README.md).
Nothing here names a device: the hub's machine name is an import question, asked once when the Shortcut is added.

`--port` and `--hub` exist only for those local test runs (a build that talks to 127.0.0.1 on another port).
"""
import argparse
import plistlib
import uuid
from pathlib import Path


def U():
    return str(uuid.uuid4()).upper()


# --- Tokens: how a parameter refers to a variable or to an earlier action's output ---------------------------

INPUT = {"Type": "ExtensionInput"}


def var(name):
    return {"Type": "Variable", "VariableName": name}


def out(uid, name):
    return {"Type": "ActionOutput", "OutputName": name, "OutputUUID": uid}


def prop(token, name):
    t = dict(token)
    t["Aggrandizements"] = [{"Type": "WFPropertyVariableAggrandizement", "PropertyName": name}]
    return t


def attach(token):
    """A parameter that is exactly one variable."""
    return {"Value": token, "WFSerializationType": "WFTextTokenAttachment"}


def text(*parts):
    """A text parameter made of literal strings and tokens; each token is one U+FFFC placeholder."""
    s, att = "", {}
    for p in parts:
        if isinstance(p, str):
            s += p
        else:
            att["{%d, 1}" % len(s)] = p
            s += "￼"
    return {"Value": {"string": s, "attachmentsByRange": att}, "WFSerializationType": "WFTextTokenString"}


def cond_input(token):
    return {"Type": "Variable", "Variable": attach(token)}


def json_body(*items):
    """(key, 'text'|'number'|'bool', value); a value is a literal or an already-built text(...)."""
    built = []
    for key, kind, value in items:
        if kind == "bool":
            wf, item_type = {"Value": bool(value), "WFSerializationType": "WFNumberSubstitutableState"}, 4
        else:
            wf = value if isinstance(value, dict) else text(str(value))
            item_type = 3 if kind == "number" else 0
        built.append({"WFItemType": item_type, "WFKey": text(key), "WFValue": wf})
    return {"Value": {"WFDictionaryFieldValueItems": built}, "WFSerializationType": "WFDictionaryFieldValue"}


# --- Actions -------------------------------------------------------------------------------------------------

# WFCondition numbers, found by running them: strings as words are silently "otherwise".
EQUALS, HAS_VALUE, NO_VALUE = 4, 100, 101


class Flow:
    def __init__(self, port, hub, headless=False):
        self.actions = []
        self.port = port
        self.hub = hub
        # Test builds only: no step waits for a person, so `shortcuts run` can finish on its own.
        self.headless = headless
        self.computers = None

    def add(self, ident, uid=None, **params):
        if uid:
            params["UUID"] = uid
        self.actions.append({"WFWorkflowActionIdentifier": "is.workflow.actions." + ident, "WFWorkflowActionParameters": params})
        return uid

    def step(self, ident, **params):
        """An action whose output is used later: returns its UUID."""
        return self.add(ident, U(), **params)

    def set(self, name, source_uid, source_name):
        self.add("setvariable", WFVariableName=name, WFInput=attach(out(source_uid, source_name)))

    def append(self, name, source_uid, source_name):
        self.add("appendvariable", WFVariableName=name, WFInput=attach(out(source_uid, source_name)))

    def url(self, path, *tail):
        return text("http://", var("hub"), ":" + self.port + path, *tail)

    def request(self, method, path, *tail, body=None):
        extra = {}
        if body is not None:
            extra.update(body)
        return self.step("downloadurl", WFURL=self.url(path, *tail), WFHTTPMethod=method, **extra)

    def value(self, key, source_token):
        return self.step("getvalueforkey", WFDictionaryKey=key, WFInput=attach(source_token))

    def as_text(self, token):
        """A value read from a dictionary, as plain text: Equals on the raw value does not match a string."""
        return out(self.step("gettext", WFTextActionText=text(token)), "Text")

    def if_(self, subject, condition, string=None):
        group = U()
        params = {"GroupingIdentifier": group, "WFControlFlowMode": 0, "WFCondition": condition, "WFInput": cond_input(subject)}
        if string is not None:
            params["WFConditionalActionString"] = string if isinstance(string, dict) else text(string)
        self.add("conditional", **params)
        return group

    def else_(self, group):
        self.add("conditional", GroupingIdentifier=group, WFControlFlowMode=1)

    def end_if(self, group):
        self.add("conditional", GroupingIdentifier=group, WFControlFlowMode=2)

    def each(self, items_token):
        group = U()
        self.add("repeat.each", GroupingIdentifier=group, WFControlFlowMode=0, WFInput=attach(items_token))
        return group

    def end_each(self, group):
        self.add("repeat.each", GroupingIdentifier=group, WFControlFlowMode=2, UUID=U())

    def tell(self, title, *body):
        self.add("notification", WFNotificationActionTitle=text(*title) if isinstance(title, tuple) else title,
                 WFNotificationActionBody=text(*body) if body else "", WFNotificationActionSound=False)

    def stop(self, title):
        self.add("alert", WFAlertActionTitle=title, WFAlertActionMessage="", WFAlertActionCancelButtonShown=False)
        self.add("exit")

    # The start of both Shortcuts: who the hub is, then register this device and read the computers.
    def register(self):
        hub_text = self.step("gettext", WFTextActionText=self.hub)
        self.set("hub", hub_text, "Text")
        registered = self.request("POST", "/v1/register")
        computers = self.value("computers", out(registered, "Contents of URL"))
        # Looped over as this output, never through a variable: Shortcuts runs a loop over a variable holding the
        # list zero times when the list has one item.
        self.computers = out(computers, "Dictionary Value")


def finish(flow, name, types, input_classes, directory):
    workflow = {
        "WFWorkflowActions": flow.actions,
        "WFWorkflowClientVersion": "2302.0.4",
        "WFWorkflowMinimumClientVersion": 900,
        "WFWorkflowMinimumClientVersionString": "900",
        "WFWorkflowIcon": {"WFWorkflowIconStartColor": 4282601983, "WFWorkflowIconGlyphNumber": 59511},
        # The first action is the hub's name; the question fills it in when the Shortcut is added.
        "WFWorkflowImportQuestions": [{"ParameterKey": "WFTextActionText", "Category": "Parameter", "ActionIndex": 0,
                                       "Text": "Hub name", "DefaultValue": flow.hub}],
        "WFWorkflowInputContentItemClasses": input_classes,
        "WFWorkflowTypes": types,
        "WFWorkflowHasShortcutInputVariables": bool(input_classes),
    }
    path = directory / (name + ".shortcut")
    with open(path, "wb") as f:
        plistlib.dump(workflow, f, fmt=plistlib.FMT_XML)
    return path


# --- Send with tailshare -------------------------------------------------------------------------------------

def build_send(port, hub, headless=False):
    f = Flow(port, hub, headless)
    f.register()

    # Names of the computers, to choose from.
    each = f.each(f.computers)
    name = f.value("name", var("Repeat Item"))
    f.append("names", name, "Dictionary Value")
    f.end_each(each)

    none = f.if_(var("names"), NO_VALUE)
    f.stop("No devices")
    f.end_if(none)

    if f.headless:
        chosen = f.step("getitemfromlist", WFItemSpecifier="First Item", WFInput=attach(var("names")))
        f.set("device", chosen, "Item from List")
    else:
        chosen = f.step("choosefromlist", WFInput=attach(var("names")), WFChooseFromListActionPrompt="Send to")
        f.set("device", chosen, "Chosen Item")

    # Its address.
    each = f.each(f.computers)
    name = f.value("name", var("Repeat Item"))
    match = f.if_(f.as_text(out(name, "Dictionary Value")), EQUALS, text(var("device")))
    address = f.value("address", var("Repeat Item"))
    f.set("address", address, "Dictionary Value")
    f.end_if(match)
    f.end_each(each)

    def to_computer(path, *tail, **kw):
        return text("http://", var("address"), ":" + port + path, *tail)

    # What is being sent: text or a link goes as text; anything else as a file, or a zip when there are several.
    count = f.step("count", WFCountType="Items", Input=attach(INPUT))
    count_text = f.step("gettext", WFTextActionText=text(out(count, "Count")))
    f.set("cnt", count_text, "Text")

    kind = f.step("gettext", WFTextActionText="file")
    f.set("kind", kind, "Text")
    single = f.if_(var("cnt"), EQUALS, "1")
    item_type = f.step("getitemtype", WFInput=attach(INPUT))
    for wanted in ("Text", "URL", "Rich Text"):
        is_text = f.if_(out(item_type, "Type"), EQUALS, wanted)
        as_text = f.step("gettext", WFTextActionText="text")
        f.set("kind", as_text, "Text")
        f.end_if(is_text)
    f.end_if(single)

    is_text = f.if_(var("kind"), EQUALS, "text")
    # --- text
    body = f.step("gettext", WFTextActionText=text(INPUT))
    f.set("payload", body, "Text")
    offer = f.step("downloadurl", WFURL=to_computer("/v1/offer"), WFHTTPMethod="POST", WFHTTPBodyType="JSON",
                   WFJSONValues=json_body(("kind", "text", "text"), ("items", "number", 1), ("isFolder", "bool", False)))
    f.set("response", offer, "Contents of URL")

    f.else_(is_text)
    # --- file or bundle: the size is the sum of the files' sizes, the name is the first item's
    each = f.each(INPUT)
    size = f.step("properties.files", WFInput=attach(var("Repeat Item")), WFContentItemPropertyName="File Size")
    bytes_ = f.step("format.filesize", WFFileSize=attach(out(size, "Details of Files")), WFFileSizeFormat="Bytes", WFFileSizeIncludeUnits=False)
    f.append("sizes", bytes_, "Formatted File Size")
    f.end_each(each)
    total = f.step("statistics", WFStatisticsOperation="Sum", Input=attach(var("sizes")))
    f.set("total", total, "Statistics")

    first = f.step("getitemfromlist", WFItemSpecifier="First Item", WFInput=attach(INPUT))
    base = f.step("gettext", WFTextActionText=text(prop(out(first, "Item from List"), "Name")))
    f.set("fname", base, "Text")
    extension = f.step("gettext", WFTextActionText=text(prop(out(first, "Item from List"), "File Extension")))
    has_extension = f.if_(out(extension, "Text"), HAS_VALUE)
    named = f.step("gettext", WFTextActionText=text(var("fname"), ".", out(extension, "Text")))
    f.set("fname", named, "Text")
    f.end_if(has_extension)

    one_file = f.if_(var("cnt"), EQUALS, "1")
    f.add("setvariable", WFVariableName="payload", WFInput=attach(INPUT))
    offer = f.step("downloadurl", WFURL=to_computer("/v1/offer"), WFHTTPMethod="POST", WFHTTPBodyType="JSON",
                   WFJSONValues=json_body(("kind", "text", "file"), ("name", "text", text(var("fname"))), ("items", "number", 1),
                                          ("isFolder", "bool", False), ("totalSize", "number", text(var("total"))),
                                          ("sizeIsEstimate", "bool", True)))
    f.set("response", offer, "Contents of URL")
    f.else_(one_file)
    archive = f.step("makezip", WFZIPName="bundle", WFInput=attach(INPUT))
    f.set("payload", archive, "Archive")
    offer = f.step("downloadurl", WFURL=to_computer("/v1/offer"), WFHTTPMethod="POST", WFHTTPBodyType="JSON",
                   WFJSONValues=json_body(("kind", "text", "bundle"), ("name", "text", text(var("fname"))), ("items", "number", text(var("cnt"))),
                                          ("isFolder", "bool", False), ("totalSize", "number", text(var("total"))),
                                          ("sizeIsEstimate", "bool", True)))
    f.set("response", offer, "Contents of URL")
    f.end_if(one_file)
    f.end_if(is_text)

    # --- the answer to the offer: a token, or why not
    token = f.value("token", var("response"))
    accepted = f.if_(out(token, "Dictionary Value"), HAS_VALUE)
    upload = f.step("downloadurl", WFURL=to_computer("/v1/upload/", out(token, "Dictionary Value")), WFHTTPMethod="PUT",
                    WFHTTPBodyType="File", WFRequestVariable=attach(var("payload")))
    ok = f.value("ok", out(upload, "Contents of URL"))
    sent = f.if_(out(ok, "Dictionary Value"), HAS_VALUE)
    f.tell(("Sent to ", var("device")))
    f.else_(sent)
    f.tell(("Couldn't send to ", var("device")))
    f.end_if(sent)

    f.else_(accepted)
    why = f.value("error", var("response"))
    reason = f.as_text(out(why, "Dictionary Value"))
    declined = f.if_(reason, EQUALS, "declined")
    f.tell((var("device"), " declined"))
    f.else_(declined)
    blocked = f.if_(reason, EQUALS, "blocked")
    f.tell((var("device"), " declined"))
    f.else_(blocked)
    late = f.if_(reason, EQUALS, "timeout")
    f.tell((var("device"), " didn't respond"))
    f.else_(late)
    f.tell(("Couldn't send to ", var("device")))
    f.end_if(late)
    f.end_if(blocked)
    f.end_if(declined)
    f.end_if(accepted)
    return f


# --- Get from tailshare --------------------------------------------------------------------------------------

LINK = r"^\s*https?://\S+\s*$"


def build_get(port, hub, headless=False):
    f = Flow(port, hub, headless)
    f.register()

    computers = f.each(f.computers)
    address = f.value("address", var("Repeat Item"))
    f.set("address", address, "Dictionary Value")
    computer = f.value("name", var("Repeat Item"))
    f.set("from", computer, "Dictionary Value")
    # From here on the requests go to the computer, not the hub.
    items = f.step("downloadurl", WFURL=text("http://", var("address"), ":" + port + "/v1/pickup"), WFHTTPMethod="GET")

    each = f.each(out(items, "Contents of URL"))
    # A loop inside a loop names its item "Repeat Item 2"; "Repeat Item" is still the computer.
    token = f.value("token", var("Repeat Item 2"))
    f.set("token", token, "Dictionary Value")
    kind = f.value("kind", var("Repeat Item 2"))
    kind_text = f.step("gettext", WFTextActionText=text(out(kind, "Dictionary Value")))
    f.set("kind", kind_text, "Text")

    def fetch():
        got = f.step("downloadurl", WFURL=text("http://", var("address"), ":" + port + "/v1/pickup/", var("token")), WFHTTPMethod="GET")
        f.append("got", got, "Contents of URL")
        return out(got, "Contents of URL")

    is_text = f.if_(var("kind"), EQUALS, "text")
    body_in = fetch()
    body = f.step("gettext", WFTextActionText=text(body_in))
    # A link is text that is nothing but one http(s) URL: removing that pattern leaves nothing. (Match Text, the obvious
    # action, is an Apple intent whose input can't be filled from a file; it asks for its text when it runs.)
    rest = f.step("text.replace", WFReplaceTextFind=LINK, WFReplaceTextReplace="", WFReplaceTextRegularExpression=True,
                  WFReplaceTextCaseSensitive=False, WFInput=text(out(body, "Text")))
    is_link = f.if_(out(rest, "Updated Text"), NO_VALUE)
    f.add("choosefrommenu", GroupingIdentifier=(menu := U()), WFControlFlowMode=0, WFMenuPrompt="Link", WFMenuItems=["Open", "Copy"])
    f.add("choosefrommenu", GroupingIdentifier=menu, WFControlFlowMode=1, WFMenuItemTitle="Open")
    f.add("openurl", WFInput=text(out(body, "Text")))
    f.add("choosefrommenu", GroupingIdentifier=menu, WFControlFlowMode=1, WFMenuItemTitle="Copy")
    f.add("setclipboard", WFInput=attach(out(body, "Text")))
    f.add("choosefrommenu", GroupingIdentifier=menu, WFControlFlowMode=2, UUID=U())
    f.else_(is_link)
    f.add("setclipboard", WFInput=attach(out(body, "Text")))
    f.add("showresult", Text=text(out(body, "Text")))
    f.end_if(is_link)

    f.else_(is_text)
    # A file or bundle: say what it is before anything is downloaded, so the Save screen is never a surprise. Cancel
    # stops the Shortcut, and what was not downloaded stays held on the computer until its time runs out.
    file_name = f.value("name", var("Repeat Item 2"))
    file_size = f.value("bytes", var("Repeat Item 2"))
    size = f.step("format.filesize", WFFileSize=attach(out(file_size, "Dictionary Value")))
    is_file = f.if_(var("kind"), EQUALS, "file")
    f.add("alert", WFAlertActionTitle=text("Download from ", var("from"), "?"), WFAlertActionMessage=text(out(file_name, "Dictionary Value"), ", ", out(size, "Formatted File Size")),
          WFAlertActionCancelButtonShown=True)
    got = fetch()

    # Shown as it arrives when it is a kind Quick Look is sure to open; anything else goes straight to Save.
    kind_of = f.step("getitemtype", WFInput=attach(got))
    shown = f.step("gettext", WFTextActionText="no")
    f.set("preview", shown, "Text")
    for previewable in ("Image", "PDF", "Text"):
        match = f.if_(out(kind_of, "Type"), EQUALS, previewable)
        yes = f.step("gettext", WFTextActionText="yes")
        f.set("preview", yes, "Text")
        f.end_if(match)
    preview = f.if_(var("preview"), EQUALS, "yes")
    f.add("previewdocument", WFInput=attach(got))
    f.else_(preview)
    f.add("documentpicker.save", WFInput=attach(got), WFAskWhereToSave=True)
    f.end_if(preview)

    f.else_(is_file)
    f.add("alert", WFAlertActionTitle=text("Download files from ", var("from"), "?"), WFAlertActionMessage=text(out(file_name, "Dictionary Value"), ", ", out(size, "Formatted File Size")),
          WFAlertActionCancelButtonShown=True)
    archive = fetch()
    files = f.step("unzip", WFArchive=attach(archive))
    f.add("documentpicker.save", WFInput=attach(out(files, "Extracted Files")), WFAskWhereToSave=True)
    f.end_if(is_file)
    f.end_if(is_text)

    f.end_each(each)
    f.end_each(computers)

    nothing = f.if_(var("got"), NO_VALUE)
    f.stop("Nothing to pick up")
    f.end_if(nothing)
    return f


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", default="45611")
    ap.add_argument("--hub", default="", help="default for the import question; only for local test builds")
    ap.add_argument("--headless", action="store_true", help="no dialogs to answer; only for local test builds")
    ap.add_argument("--suffix", default="", help="added to the file names; only for local test builds")
    ap.add_argument("--out", default=str(Path(__file__).parent))
    args = ap.parse_args()
    directory = Path(args.out)
    directory.mkdir(parents=True, exist_ok=True)

    classes = ["WFStringContentItem", "WFURLContentItem", "WFImageContentItem", "WFGenericFileContentItem"]
    print(finish(build_send(args.port, args.hub, args.headless), "Send with tailshare" + args.suffix, ["ActionExtension"], classes, directory))
    print(finish(build_get(args.port, args.hub, args.headless), "Get from tailshare" + args.suffix, [], [], directory))


if __name__ == "__main__":
    main()
