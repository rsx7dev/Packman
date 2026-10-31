"""PSADT IntelliSense in the real Packman/MonacoEditor/index.html, driven by Playwright.

Serves Packman/MonacoEditor on localhost, shims window.chrome.webview, and checks completion,
hover, signature help, the version filter, the Functions picker and its theme colours.
  python3 tests/MonacoEditorTests.py
Chromium comes from PACKMAN_CHROMIUM, else the Orchestra VM build if it exists, else Playwright's own.
Prints one line per check and exits non-zero when a check fails.
"""

import contextlib
import functools
import http.server
import json
import os
import re
import statistics
import sys
import threading
from pathlib import Path

from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parent.parent
EDITOR_DIR = ROOT / "Packman" / "MonacoEditor"
CATALOG = {"functions": []}
FUNCTIONS = {}
VM_CHROMIUM = Path("/var/lib/orchestra/qa-browsers/chromium-1248/chrome-linux64/chrome")
SCRIPT_PATH = "/packman-tests/Invoke-AppDeployToolkit.ps1"
DARK = "#0C1322"
LIGHT = "#FFFFFF"

# Runs before the page scripts so index.html's window.chrome.webview calls land here.
SHIM = """
window.__posted = [];
window.__listeners = [];
window.chrome = { webview: {
  postMessage: m => window.__posted.push(m),
  addEventListener: (type, fn) => { if (type === 'message') window.__listeners.push(fn); }
} };
window.__send = msg => window.__listeners.forEach(fn => fn({ data: msg }));
"""

# Helpers evaluated in the page once Monaco is ready.
HELPERS = """
window.__ed = () => monaco.editor.getEditors()[0];
window.__setText = text => { const model = __ed().getModel(); model.setValue(text); return model; };
window.__endPos = model => ({ lineNumber: model.getLineCount(), column: model.getLineMaxColumn(model.getLineCount()) });
"""


def chromium_path():
    configured = os.environ.get("PACKMAN_CHROMIUM")
    if configured:
        return configured
    if VM_CHROMIUM.exists():
        return str(VM_CHROMIUM)
    return None


def expand_snippet(snippet):
    """The text Monaco leaves after a snippet is inserted: placeholders become their defaults, $0 disappears."""
    return re.sub(r"\$\{\d+:([^}]*)\}", r"\1", snippet).replace("$0", "")


def short_type(type_name):
    if type_name.endswith("SwitchParameter"):
        return "switch"
    return type_name.rsplit(".", 1)[-1]


def expected_search(query):
    query = query.lower()
    return {f["name"] for f in CATALOG["functions"] if query in f["name"].lower() or query in f["synopsis"].lower()}


class Editor:
    """One page with the editor loaded, initialised, opened on an empty PowerShell script, and given a version."""

    def __init__(self, browser, base, version="4.1.7", dark=True, content="", catalog_status=None):
        self.context = browser.new_context(viewport={"width": 1200, "height": 800})
        self.page = self.context.new_page()
        self.errors = []
        self.page.on("pageerror", lambda error: self.errors.append(str(error)))
        self.page.add_init_script(SHIM)
        if catalog_status:
            self.page.route("**/psadt-functions.json",
                            lambda route: route.fulfill(status=catalog_status, body="Not found"))
        self.page.goto(f"{base}/index.html")
        self.page.wait_for_function("window.__posted.some(m => m.type === 'ready')")
        self.send({"type": "init", "background": DARK if dark else LIGHT, "dark": dark})
        self.send({"type": "open", "path": SCRIPT_PATH, "content": content, "language": "powershell",
                   "readOnly": False, "eol": "crlf", "activate": True})
        # Sent before the catalog is awaited below, so the version may arrive while the fetch is still running.
        self.send({"type": "psadtVersion", "version": version})
        self.page.wait_for_function("window.monaco && monaco.editor.getEditors().length > 0 && window.PsadtIntellisense")
        self.page.evaluate(HELPERS)
        self.page.evaluate("PsadtIntellisense.load('psadt-functions.json')")

    def send(self, message):
        self.page.evaluate("message => window.__send(message)", message)

    def check_no_page_errors(self):
        assert not self.errors, f"page errors: {self.errors}"

    def complete(self, text, position=None):
        return self.page.evaluate("""([text, position]) => {
            const model = __setText(text);
            return PsadtIntellisense.completionsAt(model, position || __endPos(model));
        }""", [text, position])

    def labels(self, text, position=None):
        return [item["label"] for item in self.complete(text, position)]

    def hover(self, text, line, column):
        return self.page.evaluate("""([text, line, column]) => {
            const model = __setText(text);
            return PsadtIntellisense.hoverAt(model, { lineNumber: line, column });
        }""", [text, line, column])

    def signature(self, text):
        return self.page.evaluate("""(text) => {
            const model = __setText(text);
            return PsadtIntellisense.signatureAt(model, __endPos(model));
        }""", text)

    def snippet_for(self, name):
        return self.page.evaluate("fn => PsadtIntellisense.snippetFor(fn)", FUNCTIONS[name])

    def set_text(self, text, line=1, column=1):
        self.page.evaluate("""([text, line, column]) => {
            __setText(text);
            __ed().setPosition({ lineNumber: line, column });
        }""", [text, line, column])

    def text(self):
        return self.page.evaluate("__ed().getModel().getValue()")

    def selected_text(self):
        return self.page.evaluate("() => { const ed = __ed(); return ed.getModel().getValueInRange(ed.getSelection()); }")

    def open_picker(self):
        self.page.evaluate("() => __ed().getAction('packman.pickFunction').run()")
        self.page.wait_for_selector("#psadt-picker", state="visible")

    def picker_rows(self):
        return self.page.eval_on_selector_all("#psadt-list li[role=option]", "els => els.map(e => e.dataset.name)")

    def picker_selected(self):
        return self.page.get_attribute("#psadt-list li[aria-selected=true]", "data-name")

    def picker_row(self, name):
        return self.page.locator(f'#psadt-list li[role=option][data-name="{name}"]')

    def probe_colour(self, variable):
        return self.page.evaluate("""(variable) => {
            const probe = document.createElement('div');
            probe.style.backgroundColor = `var(${variable})`;
            __ed().getDomNode().appendChild(probe);
            const colour = getComputedStyle(probe).backgroundColor;
            probe.remove();
            return colour;
        }""", variable)

    def picker_background(self):
        return self.page.evaluate("getComputedStyle(document.getElementById('psadt-picker')).backgroundColor")


@contextlib.contextmanager
def editor(browser, base, **options):
    ed = Editor(browser, base, **options)
    try:
        yield ed
        ed.check_no_page_errors()
    finally:
        ed.context.close()


# ── Completion ──

def check_function_completion(browser, base):
    with editor(browser, base) as ed:
        items = ed.complete("Show-ADT")
        by_label = {item["label"]: item for item in items}
        assert "Show-ADTInstallationWelcome" in by_label, "Show-ADTInstallationWelcome not suggested for Show-ADT"
        assert "Show-ADTNotifyIcon" not in by_label, "4.2-only Show-ADTNotifyIcon offered on a 4.1.7 package"
        welcome = by_label["Show-ADTInstallationWelcome"]
        assert welcome["detail"] == FUNCTIONS["Show-ADTInstallationWelcome"]["synopsis"], "detail is not the synopsis"
        assert welcome["insertText"] == ed.snippet_for("Show-ADTInstallationWelcome"), "insertText is not the snippet"
        assert welcome["range"]["startColumn"] == 1 and welcome["range"]["endColumn"] == len("Show-ADT") + 1, "range is not the whole word"
        ed.check_no_page_errors()


def check_dash_adt_offers_functions(browser, base):
    with editor(browser, base) as ed:
        items = {item["label"]: item for item in ed.complete("-ADT")}
        assert "Show-ADTInstallationWelcome" in items, "-ADT did not offer the functions"
        assert items["Show-ADTInstallationWelcome"]["filterText"] == "-Show-ADTInstallationWelcome", "filterText does not start with the dash"
        assert "Show-ADTInstallationWelcome" in ed.labels("Show-ADTIn"), "a plain prefix did not offer the function"
        ed.check_no_page_errors()


def check_snippet_pins(browser, base):
    with editor(browser, base) as ed:
        assert ed.snippet_for("Add-ADTEdgeExtension") == \
            "Add-ADTEdgeExtension -ExtensionID '${1:ExtensionID}' -UpdateUrl '${2:UpdateUrl}' -InstallationMode '${3:InstallationMode}'$0"
        assert ed.snippet_for("ConvertTo-ADTNTAccountOrSID") == "ConvertTo-ADTNTAccountOrSID -AccountName ${1:AccountName}$0"
        assert ed.snippet_for("Show-ADTNotifyIcon") == "Show-ADTNotifyIcon$0"
        ed.check_no_page_errors()


def check_no_completion_contexts(browser, base):
    cases = {
        "$adt": "a variable",
        "Write-Host": "a non-PSADT command",
        "$x -eq": "a comparison operator",
        "<#\n.SYNOPSIS\n    Show-ADT": "inside a block comment",
        "# Show-ADT": "after a comment marker",
        "Write-Log 'Show-ADT": "inside a single-quoted string",
        'Write-Log "Show-ADT': "inside a double-quoted string",
    }
    with editor(browser, base) as ed:
        for text, reason in cases.items():
            assert ed.complete(text) == [], f"offered items {reason}: {text!r}"
        ed.check_no_page_errors()


def check_parameter_completion_mandatory_first(browser, base):
    welcome = FUNCTIONS["Show-ADTInstallationWelcome"]
    mandatory = [p["name"] for p in welcome["parameters"] if p["mandatory"]]
    with editor(browser, base) as ed:
        items = ed.complete("Show-ADTInstallationWelcome -")
        labels = [item["label"] for item in items]
        assert sorted(labels) == sorted("-" + p["name"] for p in welcome["parameters"]), "parameter labels differ from the catalog"
        assert {item["label"] for item in items[:len(mandatory)]} == {"-" + n for n in mandatory}, "mandatory parameters are not first"
        assert all(item["sortText"].startswith("0") for item in items[:len(mandatory)])
        assert all(item["sortText"].startswith("1") for item in items[len(mandatory):])

        by_name = {p["name"]: p for p in welcome["parameters"]}
        for item in items:
            param = by_name[item["label"][1:]]
            if "[" in param["type"] or not param["type"]:
                continue
            expected = ("required " if param["mandatory"] else "") + short_type(param["type"])
            assert item["detail"] == expected, f"{item['label']} detail {item['detail']!r}, expected {expected!r}"
            assert item["documentation"] == param["description"], f"{item['label']} documentation is not its description"
        ed.check_no_page_errors()


def check_used_parameters_left_out(browser, base):
    text = "Add-ADTEdgeExtension -ExtensionID 'x' -"
    with editor(browser, base) as ed:
        items = ed.complete(text)
        labels = [item["label"] for item in items]
        assert "-ExtensionID" not in labels, "an already-used parameter is offered again"
        assert set(labels) == {"-UpdateUrl", "-InstallationMode", "-MinimumVersionRequired"}, f"labels {labels}"
        assert labels.index("-UpdateUrl") < labels.index("-MinimumVersionRequired")
        assert labels.index("-InstallationMode") < labels.index("-MinimumVersionRequired")
        assert all(item["range"]["startColumn"] == text.rindex("-") + 1 for item in items), "range does not cover the dash"

        typed = "Show-ADTInstallationWelcome -Clo"
        typed_items = ed.complete(typed)
        assert {"-CloseProcesses", "-CloseProcessesCountdown"} <= {item["label"] for item in typed_items}, "typed prefix does not keep matching parameters"
        assert all(item["range"]["startColumn"] == typed.index("-") + 1 for item in typed_items), "range does not start at the dash"
        ed.check_no_page_errors()


def check_typed_completion_inserts_snippet(browser, base):
    with editor(browser, base) as ed:
        ed.page.evaluate("() => __ed().focus()")
        ed.page.keyboard.type("Add-ADTEdge")
        ed.page.wait_for_selector(".suggest-widget .monaco-list-row.focused", state="visible", timeout=5000)
        focused = ed.page.inner_text(".suggest-widget .monaco-list-row.focused")
        assert "Add-ADTEdgeExtension" in focused, f"focused suggestion is {focused!r}"
        ed.page.keyboard.press("Enter")
        assert ed.text() == expand_snippet(ed.snippet_for("Add-ADTEdgeExtension")), f"inserted text {ed.text()!r}"
        assert ed.selected_text() == "ExtensionID", "the first tab stop is not selected"
        ed.check_no_page_errors()


def check_parameter_hints_while_typing(browser, base):
    with editor(browser, base) as ed:
        ed.page.evaluate("() => __ed().focus()")
        ed.page.keyboard.type("Add-ADTEdgeExtension -")
        ed.page.wait_for_selector(".parameter-hints-widget", state="visible", timeout=5000)
        hints = ed.page.inner_text(".parameter-hints-widget")
        assert "ExtensionID" in hints, f"signature help is {hints!r}"
        ed.check_no_page_errors()


# ── Hover and signature help ──

def check_hover_docs_link(browser, base):
    name = "Add-ADTEdgeExtension"
    docs = FUNCTIONS[name]["docs_url"]
    with editor(browser, base) as ed:
        ed.set_text(f"{name} -ExtensionID 'x'")
        point = ed.page.evaluate("""() => {
            const ed = __ed();
            const box = ed.getDomNode().getBoundingClientRect();
            const pos = ed.getScrolledVisiblePosition({ lineNumber: 1, column: 5 });
            return { x: box.left + pos.left + 2, y: box.top + pos.top + pos.height / 2 };
        }""")
        ed.page.mouse.move(point["x"], point["y"], steps=4)
        link = ed.page.locator(".monaco-hover a").filter(has_text="Docs")
        link.first.wait_for(state="visible", timeout=5000)
        href = link.first.get_attribute("href") or link.first.get_attribute("data-href")
        assert href == docs, f"docs link is {href!r}"
        ed.check_no_page_errors()


def check_hover_contents(browser, base):
    with editor(browser, base, version="4.1.7") as ed:
        text = "Add-ADTEdgeExtension -ExtensionID 'x'"
        hover = ed.hover(text, 1, 5)
        assert hover and hover["range"]["startColumn"] == 1, f"no hover range over the function: {hover}"
        value = "\n".join(part["value"] for part in hover["contents"])
        assert FUNCTIONS["Add-ADTEdgeExtension"]["synopsis"] in value, "hover lacks the synopsis"
        assert f"[Docs]({FUNCTIONS['Add-ADTEdgeExtension']['docs_url']})" in value, "hover lacks the docs link"

        param = ed.hover(text, 1, text.index("-ExtensionID") + 2)
        assert param, "no hover over the parameter"
        param_value = "\n".join(part["value"] for part in param["contents"])
        assert "System.String" in param_value, "parameter hover lacks the full type"
        assert re.search("mandatory|required", param_value, re.I), "parameter hover lacks the mandatory flag"
        assert FUNCTIONS["Add-ADTEdgeExtension"]["parameters"][0]["description"] in param_value, "parameter hover lacks its description"

        hidden_value = "\n".join(part["value"] for part in ed.hover("Show-ADTNotifyIcon", 1, 5)["contents"])
        assert "Not in this package's PSADT 4.1.7" in hidden_value, "hover lacks the availability sentence"
        assert ed.hover("Write-Host", 1, 3) is None, "hover on a non-PSADT word is not null"
        ed.check_no_page_errors()


def check_signature_help_values(browser, base):
    with editor(browser, base) as ed:
        signature = ed.signature("Add-ADTEdgeExtension -ExtensionID 'abc' -UpdateUrl 'u' ")
        assert signature, "no signature help after a call"
        assert signature["activeSignature"] == 0
        labels = [p["label"] for p in signature["signatures"][0]["parameters"]]
        assert labels == ["-ExtensionID", "-UpdateUrl", "-InstallationMode", "-MinimumVersionRequired"], f"parameters {labels}"
        assert signature["activeParameter"] == 1, f"activeParameter {signature['activeParameter']}, expected 1 after -UpdateUrl"
        assert ed.signature("Add-ADTEdgeExtension -")["activeParameter"] == 0
        assert ed.signature("Write-Host -Object x") is None, "signature help for a non-PSADT call"
        ed.check_no_page_errors()


# ── Version filter ──

def check_version_filter(browser, base):
    cases = [
        ("4.1.7", {"Show-ADTNotifyIcon", "Add-ADTFont"}),
        ("4.2.0", {"Get-ADTEnvironment", "Get-ADTUniversalDate"}),
        (None, set()),
    ]
    for version, hidden in cases:
        with editor(browser, base, version=version) as ed:
            shown = set(ed.labels("Show-ADT")) | set(ed.labels("Add-ADT")) | set(ed.labels("Get-ADT"))
            for name in hidden:
                assert name not in shown, f"version {version}: {name} offered"
            for name in ["Show-ADTInstallationWelcome", "Add-ADTEdgeExtension"]:
                assert name in shown, f"version {version}: {name} hidden"
            if version is None:
                assert "Show-ADTNotifyIcon" in shown and "Get-ADTEnvironment" in shown, "no version hid nothing"
            if version == "4.2.0":
                assert "Show-ADTNotifyIcon" in shown and "Add-ADTFont" in shown, "4.2 functions hidden on 4.2.0"
                assert "Get-ADTEnvironment" not in shown, "removed-in-4.2 function offered on 4.2.0"
            if version == "4.1.7":
                assert "Get-ADTEnvironment" in shown, "release function hidden on 4.1.7"
                assert ed.labels("Show-ADTNotifyIcon -"), "hidden function lost its parameter help"
                assert "-Force" in ed.labels("Show-ADTNotifyIcon -"), "hidden function lost -Force"
                assert ed.page.evaluate("PsadtIntellisense.availability(FN)", FUNCTIONS["Show-ADTNotifyIcon"]) == "devOnly"
                assert ed.page.evaluate("PsadtIntellisense.availability(FN)", FUNCTIONS["Get-ADTEnvironment"]) == "available"
            if version == "4.2.0":
                assert ed.page.evaluate("PsadtIntellisense.availability(FN)", FUNCTIONS["Get-ADTEnvironment"]) == "removed"

            ed.open_picker()
            unavailable = ed.page.eval_on_selector_all("#psadt-list li[role=option].psadt-unavailable", "els => els.map(e => e.dataset.name)")
            if version == "4.1.7":
                assert "Show-ADTNotifyIcon" in unavailable, f"picker unavailable rows {unavailable}"
                assert "Add-ADTFont" in unavailable
                assert "Get-ADTEnvironment" not in unavailable
            elif version == "4.2.0":
                assert "Get-ADTEnvironment" in unavailable
                assert "Show-ADTNotifyIcon" not in unavailable
            else:
                assert unavailable == [], f"picker marks rows with no version: {unavailable}"
            ed.check_no_page_errors()


def check_version_badges(browser, base):
    with editor(browser, base, version="4.1.7") as ed:
        assert ed.page.evaluate("PsadtIntellisense.badgeText(FN)", FUNCTIONS["Show-ADTNotifyIcon"]) == "4.2 only"
        assert ed.page.evaluate("PsadtIntellisense.badgeText(FN)", FUNCTIONS["Get-ADTEnvironment"]) == "removed in 4.2"
        assert ed.page.evaluate("PsadtIntellisense.badgeText(FN)", FUNCTIONS["Add-ADTEdgeExtension"]) == ""
        ed.open_picker()
        assert ed.picker_row("Show-ADTNotifyIcon").locator(".psadt-badge").inner_text() == "4.2 only"
        assert ed.picker_row("Get-ADTEnvironment").locator(".psadt-badge").inner_text() == "removed in 4.2"
        assert ed.picker_row("Add-ADTEdgeExtension").locator(".psadt-badge").inner_text() == "", "available function has a badge"
        ed.check_no_page_errors()


# ── Picker ──

def check_picker_groups_and_rows(browser, base):
    with editor(browser, base) as ed:
        ed.open_picker()
        verbs = sorted({f["verb"] for f in CATALOG["functions"]})
        headers = ed.page.eval_on_selector_all("#psadt-list li.psadt-group", "els => els.map(e => e.textContent.trim())")
        assert len(headers) == len(verbs), f"{len(headers)} groups, expected {len(verbs)}"
        assert all(any(h.startswith(v) for h in headers) for v in verbs), "a verb has no group"
        assert len(ed.picker_rows()) == len(CATALOG["functions"]), "picker does not list every function"
        assert ed.page.get_attribute("#psadt-list", "role") == "listbox"
        assert ed.page.evaluate("document.activeElement.id") == "psadt-search", "search box is not focused on open"
        ed.check_no_page_errors()


def check_picker_search_keyboard_and_insert(browser, base):
    with editor(browser, base) as ed:
        ed.set_text("first\nsecond", line=2, column=1)
        ed.page.evaluate("() => __ed().focus()")
        ed.open_picker()
        ed.page.focus("#psadt-search")
        ed.page.keyboard.type("Get-ADT")
        assert ed.page.input_value("#psadt-search") == "Get-ADT"
        assert ed.text() == "first\nsecond", "keys typed in the picker reached the editor"
        rows = ed.picker_rows()
        assert set(rows) == expected_search("Get-ADT"), "search results differ from the catalog"

        start = rows.index(ed.picker_selected())
        ed.page.keyboard.press("ArrowDown")
        assert ed.picker_selected() == rows[start + 1], "ArrowDown did not move the selection"
        ed.page.keyboard.press("ArrowUp")
        assert ed.picker_selected() == rows[start], "ArrowUp did not move the selection back"
        ed.page.keyboard.press("ArrowDown")
        chosen = ed.picker_selected()

        ed.page.keyboard.press("Enter")
        ed.page.wait_for_selector("#psadt-picker", state="hidden")
        assert ed.text() == "first\n" + expand_snippet(ed.snippet_for(chosen)) + "second", f"inserted {ed.text()!r}"
        assert ed.page.evaluate("__ed().hasTextFocus()"), "editor did not regain focus after insert"
        ed.check_no_page_errors()


def check_picker_escape_returns_focus(browser, base):
    with editor(browser, base) as ed:
        ed.page.evaluate("() => __ed().focus()")
        ed.open_picker()
        ed.page.keyboard.press("Escape")
        ed.page.wait_for_selector("#psadt-picker", state="hidden")
        assert ed.page.evaluate("__ed().hasTextFocus()"), "Esc did not return focus to the editor"
        assert ed.text() == "", "Esc inserted something"
        ed.check_no_page_errors()


def check_picker_empty_state(browser, base):
    with editor(browser, base) as ed:
        ed.open_picker()
        ed.page.fill("#psadt-search", "zzqx-no-such-function")
        ed.page.wait_for_selector("#psadt-empty", state="visible")
        assert ed.page.inner_text("#psadt-empty").strip() == "No functions match"
        assert ed.picker_rows() == [], "rows shown for an empty search"
        ed.page.fill("#psadt-search", "Add-ADTEdge")
        ed.page.wait_for_selector("#psadt-empty", state="hidden")
        assert set(ed.picker_rows()) == expected_search("Add-ADTEdge"), "picker is not usable after an empty search"
        ed.check_no_page_errors()


def check_picker_opens_from_message(browser, base):
    with editor(browser, base) as ed:
        ed.send({"type": "pickFunction"})
        ed.page.wait_for_selector("#psadt-picker", state="visible")
        ed.check_no_page_errors()


def check_picker_theme_colours(browser, base):
    with editor(browser, base, dark=True) as ed:
        ed.open_picker()
        dark_probe = ed.probe_colour("--vscode-editorWidget-background")
        assert dark_probe not in ("", "rgba(0, 0, 0, 0)", "transparent"), "--vscode-editorWidget-background did not resolve in the editor"
        dark_picker = ed.picker_background()
        assert dark_picker == dark_probe, f"dark picker {dark_picker} is not the editor widget colour {dark_probe}"

        ed.send({"type": "theme", "background": LIGHT, "dark": False})
        light_probe = ed.probe_colour("--vscode-editorWidget-background")
        light_picker = ed.picker_background()
        assert light_picker == light_probe, f"light picker {light_picker} is not the editor widget colour {light_probe}"
        assert light_picker != dark_picker, "the picker background does not change with the theme"
        ed.check_no_page_errors()


def check_large_script_completion(browser, base):
    with editor(browser, base) as ed:
        timings = ed.page.evaluate("""() => {
            const lines = [];
            for (let i = 1; i < 20000; i++) lines.push(`$value${i} = 'line ${i}'`);
            lines.push('Show-ADT');
            const model = __setText(lines.join('\\n'));
            const position = { lineNumber: 20000, column: model.getLineMaxColumn(20000) };
            const times = [];
            let count = 0;
            for (let i = 0; i < 20; i++) {
                const started = performance.now();
                count = PsadtIntellisense.completionsAt(model, position).length;
                times.push(performance.now() - started);
            }
            return { times, count };
        }""")
        assert timings["count"] > 0, "no completions on the large script"
        median = statistics.median(timings["times"])
        assert median < 50, f"median completionsAt on a 20,000-line script is {median:.1f} ms"
        ed.check_no_page_errors()


def check_missing_catalog_degrades(browser, base):
    with editor(browser, base, catalog_status=404) as ed:
        assert ed.labels("Show-ADT") == [], "completion offered items without a catalog"
        ed.open_picker()
        ed.page.wait_for_selector("#psadt-empty", state="visible")
        assert ed.page.inner_text("#psadt-empty").strip() == "Function catalog not available"
        ed.check_no_page_errors()


CHECKS = [
    ("Typing Show-ADT suggests functions with synopsis and snippet", check_function_completion),
    ("Typing -ADT and a plain prefix suggests functions with a dash filter", check_dash_adt_offers_functions),
    ("Snippet pins: tab stops for mandatory parameters only", check_snippet_pins),
    ("Variables, other commands, operators, comments and strings get no items", check_no_completion_contexts),
    ("After a function name, - lists parameters with mandatory first and types", check_parameter_completion_mandatory_first),
    ("Already-used parameters are left out and the range covers the dash", check_used_parameters_left_out),
    ("Real suggest widget: typing Add-ADTEdge then Enter inserts the snippet", check_typed_completion_inserts_snippet),
    ("Real parameter hints appear while typing a call", check_parameter_hints_while_typing),
    ("Hover over a function shows the Docs link as href or data-href", check_hover_docs_link),
    ("Hover contents: synopsis, docs link, parameter type and availability", check_hover_contents),
    ("Signature help lists parameters in catalog order with the active one", check_signature_help_values),
    ("Version filter: 4.1.7, 4.2.0 and no version hide the right functions", check_version_filter),
    ("Picker and hover badges: 4.2 only and removed in 4.2", check_version_badges),
    ("Picker groups by verb and lists every function", check_picker_groups_and_rows),
    ("Picker search, arrow keys, Enter inserts at the cursor", check_picker_search_keyboard_and_insert),
    ("Picker Escape returns focus to the editor without inserting", check_picker_escape_returns_focus),
    ("Picker empty search shows a message and stays usable", check_picker_empty_state),
    ("pickFunction message opens the picker", check_picker_opens_from_message),
    ("Picker background matches the editor widget colour in dark and light", check_picker_theme_colours),
    ("Median completion on line 20,000 of a 20,000-line script is under 50 ms", check_large_script_completion),
    ("A catalog that returns 404 degrades without page errors", check_missing_catalog_degrades),
]


def serve():
    handler = functools.partial(QuietHandler, directory=str(EDITOR_DIR))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def load_catalog():
    global CATALOG, FUNCTIONS
    catalog_file = EDITOR_DIR / "psadt-functions.json"
    if not catalog_file.exists():
        raise SystemExit(f"FAIL  shipped catalog is missing: {catalog_file}")
    CATALOG = json.loads(catalog_file.read_text(encoding="utf-8"))
    FUNCTIONS = {f["name"]: f for f in CATALOG["functions"]}


def main():
    load_catalog()
    server = serve()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    failures = []
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(executable_path=chromium_path(), headless=True)
            try:
                for name, check in CHECKS:
                    try:
                        check(browser, base)
                        print(f"PASS  {name}")
                    except Exception as ex:  # Playwright timeouts and assertion failures both count as a failed check
                        message = str(ex).strip().splitlines()[0] if str(ex).strip() else type(ex).__name__
                        failures.append(f"{name}: {message}")
                        print(f"FAIL  {name}: {message}")
            finally:
                browser.close()
    finally:
        server.shutdown()
    if failures:
        print(f"{len(failures)} of {len(CHECKS)} checks failed")
        return 1
    print(f"All {len(CHECKS)} checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
