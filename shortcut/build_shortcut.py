#!/usr/bin/env python3
"""Builds the "Play on Baird" iPhone Shortcut (unsigned) from source.

    python3 shortcut/build_shortcut.py out/unsigned.shortcut
    shortcuts sign --mode anyone --input out/unsigned.shortcut --output "out/Play on Baird.shortcut"   # macOS only

iOS only imports signed Shortcuts, and only macOS can sign them, so the .github/workflows/shortcut.yml job builds and
signs it on a Mac runner; the release ships it in Baird's zip and Baird serves it to phones (Settings > Connect a phone).

What it does, on the share sheet (or with a copied link when run on its own):
  1. Reads baird-pairing.txt from the Shortcuts folder in iCloud Drive. The first time there is none: it asks the viewer
     to scan code 2 on the TV ({"baird": "http://<ip>:8099", "token": "..."}) and saves it.
  2. POSTs {"url": <what was shared>} to <baird>/play with "Authorization: Bearer <token>".
  3. Shows Baird's answer as a notification. If Baird says "unpaired" (forgotten in Settings), it deletes the pairing so
     the next share scans again.
Baird always answers 200 with {"status", "message"}, so nothing here depends on how Shortcuts treats HTTP errors.
"""
import plistlib
import sys
import uuid

PAIRING_FILE = "baird-pairing.txt"
OBJ = "￼"  # where a variable sits in a text field
_ns = uuid.UUID("6f0b1c8e-4a55-4bd1-9a51-6b2b6f1c0a11")


def uid(name):
    """Stable ids, so the same source always builds the same Shortcut."""
    return str(uuid.uuid5(_ns, name)).upper()


def output(action_uuid, name):
    return {"Type": "ActionOutput", "OutputUUID": action_uuid, "OutputName": name}


def var(action_uuid, name):
    """A whole-parameter reference to an earlier action's output."""
    return {"Value": output(action_uuid, name), "WFSerializationType": "WFTextTokenAttachment"}


def text(s, **attachments):
    """A text field; attachments map the index of each OBJ in s to what goes there."""
    value = {"string": s}
    if attachments:
        value["attachmentsByRange"] = {f"{{{int(k[1:])}, 1}}": v for k, v in attachments.items()}
    return {"Value": value, "WFSerializationType": "WFTextTokenString"}


def dictionary(items):
    return {
        "Value": {"WFDictionaryFieldValueItems": [{"WFItemType": 0, "WFKey": text(k), "WFValue": v} for k, v in items]},
        "WFSerializationType": "WFDictionaryFieldValue",
    }


def action(identifier, **params):
    return {"WFWorkflowActionIdentifier": identifier, "WFWorkflowActionParameters": params}


def condition_input(action_uuid, name):
    return {"Type": "Variable", "Variable": var(action_uuid, name)}


IS, HAS_NO_VALUE = 4, 101  # Shortcuts' If conditions


def actions():
    first_file, scan, file, pairing, base, token = (uid(n) for n in ("file1", "scan", "file2", "pairing", "base", "token"))
    response, reply, status, message = (uid(n) for n in ("response", "reply", "status", "message"))
    not_paired, unpaired = uid("if-not-paired"), uid("if-unpaired")
    shortcut_input = {"Type": "ExtensionInput"}
    return [
        action("is.workflow.actions.comment", WFCommentActionText=
               "Play on Baird: sends what you share to Baird, the TV. Built from shortcut/build_shortcut.py in the Baird repo."),
        # 1. The pairing (Baird's address and this phone's token), or scan it from the TV.
        action("is.workflow.actions.documentpicker.open", UUID=first_file, WFGetFilePath=text(PAIRING_FILE),
               WFFileErrorIfNotFound=False, WFShowFilePicker=False),
        action("is.workflow.actions.conditional", GroupingIdentifier=not_paired, WFControlFlowMode=0,
               WFCondition=HAS_NO_VALUE, WFInput=condition_input(first_file, "File")),
        action("is.workflow.actions.alert", WFAlertActionTitle="Connect to Baird",
               WFAlertActionMessage="On the TV, open Settings › Connect a phone, then point the camera at code 2.",
               WFAlertActionCancelButtonShown=False),
        action("is.workflow.actions.scanbarcode", UUID=scan),
        action("is.workflow.actions.documentpicker.save", WFInput=var(scan, "QR/Barcode"), WFAskWhereToSave=False,
               WFFileDestinationPath=text(PAIRING_FILE), WFSaveFileOverwrite=True),
        action("is.workflow.actions.conditional", GroupingIdentifier=not_paired, WFControlFlowMode=2),
        action("is.workflow.actions.documentpicker.open", UUID=file, WFGetFilePath=text(PAIRING_FILE),
               WFFileErrorIfNotFound=True, WFShowFilePicker=False),
        action("is.workflow.actions.detect.dictionary", UUID=pairing, WFInput=var(file, "File")),
        action("is.workflow.actions.getvalueforkey", UUID=base, WFInput=var(pairing, "Dictionary"),
               WFGetDictionaryValueType="Value", WFDictionaryKey="baird"),
        action("is.workflow.actions.getvalueforkey", UUID=token, WFInput=var(pairing, "Dictionary"),
               WFGetDictionaryValueType="Value", WFDictionaryKey="token"),
        # 2. Send it.
        action("is.workflow.actions.downloadurl", UUID=response,
               WFURL=text(OBJ + "/play", i0=output(base, "Dictionary Value")),
               WFHTTPMethod="POST", ShowHeaders=True,
               WFHTTPHeaders=dictionary([("Authorization", text("Bearer " + OBJ, i7=output(token, "Dictionary Value")))]),
               WFHTTPBodyType="JSON",
               WFJSONValues=dictionary([("url", text(OBJ, i0=shortcut_input))])),
        # 3. Baird's answer.
        action("is.workflow.actions.detect.dictionary", UUID=reply, WFInput=var(response, "Contents of URL")),
        action("is.workflow.actions.getvalueforkey", UUID=status, WFInput=var(reply, "Dictionary"),
               WFGetDictionaryValueType="Value", WFDictionaryKey="status"),
        action("is.workflow.actions.getvalueforkey", UUID=message, WFInput=var(reply, "Dictionary"),
               WFGetDictionaryValueType="Value", WFDictionaryKey="message"),
        action("is.workflow.actions.conditional", GroupingIdentifier=unpaired, WFControlFlowMode=0,
               WFCondition=IS, WFConditionalActionString="unpaired", WFInput=condition_input(status, "Dictionary Value")),
        action("is.workflow.actions.file.delete", WFInput=var(file, "File"), WFDeleteFileConfirmDeletion=False),
        action("is.workflow.actions.conditional", GroupingIdentifier=unpaired, WFControlFlowMode=2),
        action("is.workflow.actions.notification", WFNotificationActionTitle="Baird",
               WFNotificationActionBody=text(OBJ, i0=output(message, "Dictionary Value")), WFNotificationActionSound=False),
    ]


def shortcut():
    return {
        "WFWorkflowClientVersion": "2302.0.4",
        "WFWorkflowMinimumClientVersion": 900,
        "WFWorkflowMinimumClientVersionString": "900",
        "WFWorkflowIcon": {"WFWorkflowIconStartColor": 463140863, "WFWorkflowIconGlyphNumber": 59446},
        "WFWorkflowTypes": ["ActionExtension"],
        "WFWorkflowInputContentItemClasses": ["WFURLContentItem", "WFSafariWebPageContentItem", "WFStringContentItem", "WFArticleContentItem"],
        "WFWorkflowHasShortcutInputVariables": True,
        # Run on its own (not from the share sheet): play the copied link.
        "WFWorkflowNoInputBehavior": {"Name": "WFWorkflowNoInputBehaviorGetClipboard", "Parameters": {}},
        "WFWorkflowOutputContentItemClasses": [],
        "WFWorkflowHasOutputFallback": False,
        "WFWorkflowImportQuestions": [],
        "WFQuickActionSurfaces": [],
        "WFWorkflowActions": actions(),
    }


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    with open(sys.argv[1], "wb") as f:
        plistlib.dump(shortcut(), f, fmt=plistlib.FMT_BINARY)


if __name__ == "__main__":
    main()
