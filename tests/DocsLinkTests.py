"""Documentation checks for the tracked markdown files and for leaked internal references.

Stdlib only. Reads the file list from `git ls-files`, so new files must be `git add`ed.
  python3 tests/DocsLinkTests.py [--external]
Checks: links, images, lint (markdownlint MD001, MD025, MD040, MD042, MD059), commands, references,
placeholders, and with --external a GET of every http(s) URL in the markdown.
Prints PASS or FAIL lines and exits non-zero when a check fails.
"""

import posixpath
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SELF = "tests/DocsLinkTests.py"
OWN_REPOSITORY = "https://github.com/TechLakeRS/Packman"
GENERIC_LINK_TEXT = {"here", "click here", "link", "this", "more", "read more"}
# Vendored or generated trees that are not scanned for leaked references.
REFERENCE_SKIP = ("packages/", "third-party/", "Packman/MonacoEditor/vs/")
LEAKS = [
    ("task id", re.compile(r"\bT-\d{4}\b")),
    ("machine-local path", re.compile(r"(?<![\w.])/(?:var/lib|srv|home)/")),
    ("single CI run URL", re.compile(r"/actions/runs/\d+")),
    ("session folder", re.compile(r"\.orch/")),
    ("session link", re.compile(r"claude\.ai/code")),
]
PLACEHOLDER = re.compile(r"\b(?:TODO|TBD)\b|\blorem\b", re.IGNORECASE)

FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")
HEADING = re.compile(r"^ {0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")
CODE_SPAN = re.compile(r"(`+)(.+?)\1")
LINK_END = re.compile(r"\]\(((?:[^()\s]|\([^()\s]*\))*)(?:\s+\"[^\"]*\")?\)")
HTML_IMG = re.compile(r"<img\b[^>]*>", re.IGNORECASE)
HTML_ATTR = r"""\b{}\s*=\s*["']([^"']*)["']"""
HTML_HREF = re.compile(HTML_ATTR.format("href"), re.IGNORECASE)
BARE_URL = re.compile(r"https?://[^\s<>\"'\]]+")
PUBLIC_SCHEME = re.compile(r"^(?:https?|mailto):", re.IGNORECASE)


def tracked_files():
    out = subprocess.run(["git", "-C", str(ROOT), "ls-files", "-z"], capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


def read_lines(path):
    return (ROOT / path).read_text(encoding="utf-8", errors="replace").splitlines()


class Markdown:
    """One markdown file split into headings, links, images, fences and the text of its fenced code."""

    def __init__(self, path):
        self.path = path
        self.headings = []  # (line, level, text)
        self.links = []  # (line, text, target)
        self.images = []  # (line, alt, target)
        self.fences = []  # (line, language)
        self.code_lines = []  # (line, text)
        self.urls = []  # (line, url)
        self._parse(read_lines(path))

    def _parse(self, lines):
        fence = None
        for number, line in enumerate(lines, 1):
            match = FENCE.match(line)
            if fence:
                if match and match.group(1)[0] == fence[0] and len(match.group(1)) >= fence[1] and not match.group(2).strip():
                    fence = None
                else:
                    self.code_lines.append((number, line))
                continue
            if match:
                fence = (match.group(1)[0], len(match.group(1)))
                self.fences.append((number, match.group(2).strip()))
                continue
            text = CODE_SPAN.sub(lambda m: " " * len(m.group(0)), line)
            heading = HEADING.match(line)
            if heading:
                self.headings.append((number, len(heading.group(1)), heading.group(2)))
            self._parse_links(number, text)

    def _parse_links(self, number, text):
        for end in LINK_END.finditer(text):
            start = self._opening_bracket(text, end.start())
            if start is None:
                continue
            target = end.group(1)
            label = text[start + 1:end.start()]
            if start > 0 and text[start - 1] == "!":
                self.images.append((number, label, target))
            else:
                self.links.append((number, label, target))
        for tag in HTML_IMG.findall(text):
            src = re.search(HTML_ATTR.format("src"), tag, re.IGNORECASE)
            alt = re.search(HTML_ATTR.format("alt"), tag, re.IGNORECASE)
            self.images.append((number, alt.group(1) if alt else "", src.group(1) if src else ""))
        for href in HTML_HREF.findall(text):
            self.links.append((number, "html", href))
        for url in BARE_URL.findall(text):
            self.urls.append((number, url.rstrip(".,;:)")))

    @staticmethod
    def _opening_bracket(text, close):
        depth = 0
        for i in range(close, -1, -1):
            if text[i] == "]":
                depth += 1
            elif text[i] == "[":
                depth -= 1
                if depth == 0:
                    return i
        return None

    def slugs(self):
        seen = {}
        result = set()
        for _, _, text in self.headings:
            text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", text)
            slug = "".join(c for c in text.lower() if c.isalnum() or c in "_ -").replace(" ", "-")
            count = seen.get(slug, 0)
            seen[slug] = count + 1
            result.add(slug if count == 0 else f"{slug}-{count}")
        return result


class Repository:
    def __init__(self):
        self.files = tracked_files()
        self.file_set = set(self.files)
        self.directories = {posixpath.dirname(f) for f in self.files}
        for d in list(self.directories):
            while d:
                d = posixpath.dirname(d)
                self.directories.add(d)
        self.markdown = {f: Markdown(f) for f in self.files if f.endswith(".md") and not f.startswith("third-party/")}
        self.top_level = {f.split("/")[0] for f in self.files}

    def resolve(self, source, target):
        """The repo path a relative link points at, or None when it leaves the repository or does not exist."""
        path = urllib.parse.unquote(target.split("#")[0].split("?")[0])
        if not path:
            return source
        joined = path[1:] if path.startswith("/") else posixpath.join(posixpath.dirname(source), path)
        normal = posixpath.normpath(joined)
        if normal == ".":
            normal = ""
        if normal.startswith("..") or not (normal in self.file_set or normal in self.directories):
            return None
        return normal

    def exists(self, path):
        return path in self.file_set or path in self.directories


def is_remote(target):
    return bool(PUBLIC_SCHEME.match(target))


def check_schemes(repo):
    """Only http, https and mailto are exempt from file resolution; file:, ftp: and drive paths must not pass."""
    public = ["http://example.com", "HTTPS://example.com/a#b", "mailto:a@example.com"]
    private = ["file:///C:/Internal/report.md", "ftp://host/x", "javascript:alert(1)", "C:/Internal/report.md"]
    return [f"{target} is wrongly treated as {'private' if want else 'public'}"
            for want, targets in ((True, public), (False, private))
            for target in targets if is_remote(target) != want]


def check_links(repo):
    fails = []
    for path, md in repo.markdown.items():
        for line, _, target in md.links + md.images:
            if is_remote(target):
                continue
            resolved = repo.resolve(path, target)
            if resolved is None:
                fails.append(f"{path}:{line}: {target}")
                continue
            fragment = urllib.parse.unquote(target.partition("#")[2])
            if fragment and resolved.endswith(".md") and resolved in repo.markdown:
                if fragment not in repo.markdown[resolved].slugs():
                    fails.append(f"{path}:{line}: {target} (no such heading in {resolved})")
    return fails


def check_images(repo):
    fails = []
    for path, md in repo.markdown.items():
        for line, alt, target in md.images:
            resolved = None if is_remote(target) else repo.resolve(path, target)
            if resolved is None or not resolved.startswith("docs/images/"):
                fails.append(f"{path}:{line}: {target} is not a file under docs/images/")
            if not alt.strip():
                fails.append(f"{path}:{line}: {target} has no alt text (MD045)")
    return fails


def check_lint(repo):
    fails = []
    for path, md in repo.markdown.items():
        previous = 0
        h1_lines = []
        for line, level, _ in md.headings:
            if previous and level > previous + 1:
                fails.append(f"{path}:{line}: MD001 heading level jumps from {previous} to {level}")
            previous = level
            if level == 1:
                h1_lines.append(line)
        for line in h1_lines[1:]:
            fails.append(f"{path}:{line}: MD025 more than one H1")
        for line, language in md.fences:
            if not language:
                fails.append(f"{path}:{line}: MD040 code fence without a language")
        for line, text, target in md.links:
            if not target.strip() or target.strip() == "#" or not text.strip():
                fails.append(f"{path}:{line}: MD042 empty link text or target: [{text}]({target})")
            elif text.strip().lower() in GENERIC_LINK_TEXT:
                fails.append(f"{path}:{line}: MD059 link text '{text}' does not name its target")
    return fails


def check_commands(repo):
    fails = []
    for path, md in repo.markdown.items():
        for line, text in md.code_lines:
            for raw in text.split():
                token = raw.strip("\"'`(),;:").replace("\\", "/")
                token = token[2:] if token.startswith("./") else token
                if not token or any(c in token for c in "*?<>$%{}[]|=") or "..." in token or token.startswith(("-", "/", "~")):
                    continue
                first = token.split("/")[0]
                if first in repo.top_level and "/" in token and not repo.exists(token.rstrip("/")):
                    fails.append(f"{path}:{line}: {token}")
    return fails


def check_references(repo):
    fails = []
    for path in repo.files:
        if path == SELF or path.startswith(REFERENCE_SKIP):
            continue
        data = (ROOT / path).read_bytes()
        if b"\0" in data[:8192]:
            continue
        for number, text in enumerate(data.decode("utf-8", errors="replace").splitlines(), 1):
            for name, pattern in LEAKS:
                found = pattern.search(text)
                if found:
                    fails.append(f"{path}:{number}: {name} '{found.group(0)}'")
    return fails


def check_placeholders(repo):
    fails = []
    for path in repo.markdown:
        for number, text in enumerate(read_lines(path), 1):
            found = PLACEHOLDER.search(text)
            if found:
                fails.append(f"{path}:{number}: {found.group(0)}")
    return fails


def check_external(repo, skipped):
    first_seen = {}
    for path, md in repo.markdown.items():
        for line, _, target in md.links + md.images:
            if target.startswith(("http://", "https://")):
                first_seen.setdefault(target.split("#")[0], (path, line))
        for line, url in md.urls:
            first_seen.setdefault(url.split("#")[0], (path, line))
    fails = []
    for url, (path, line) in sorted(first_seen.items()):
        if url.startswith(OWN_REPOSITORY):
            skipped.append(f"SKIP  external: {url} (this repository)")
            continue
        request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Packman docs check)"})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                response.read(1)
        except urllib.error.HTTPError as error:
            fails.append(f"{path}:{line}: {url} returned {error.code}")
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            fails.append(f"{path}:{line}: {url} failed: {error}")
    return fails


def main():
    repo = Repository()
    checks = [
        ("schemes", check_schemes),
        ("links", check_links),
        ("images", check_images),
        ("lint", check_lint),
        ("commands", check_commands),
        ("references", check_references),
        ("placeholders", check_placeholders),
    ]
    skipped = []
    if "--external" in sys.argv[1:]:
        checks.append(("external", lambda r: check_external(r, skipped)))
    failures = 0
    for name, check in checks:
        problems = check(repo)
        failures += len(problems)
        if not problems:
            print(f"PASS  {name}")
        for problem in problems:
            print(f"FAIL  {name}: {problem}")
    for line in skipped:
        print(line)
    print(f"{len(checks)} checks, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
