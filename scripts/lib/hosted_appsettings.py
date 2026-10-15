"""Name-only projection helpers for hosted application-setting listings."""

from __future__ import annotations

import re
from pathlib import Path
from typing import Iterable, Iterator

EXACT_SETTING_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.:-]*$")
UNSAFE_NAME_TOKENS = ("*", "?", "[", "]", ",", " ")
LISTING_COMMAND = re.compile(r"webapp\s+config\s+appsettings\s+list\b")
EXACT_VALUE_QUERY = re.compile(
    r"^\[\?name==(?:'[^']+'|\"[^\"]+\")\]"
    r"(?: \| \[0\]\.(?:value|slotSetting)| \| length\(@\))?$"
)
NAMES_ONLY_FILTERED = re.compile(r"^\[\?.+\](?:\.name| \| length\(@\))$")
QUERY_ARGUMENT = re.compile(
    r"""--query\s+('(?:\\'|[^'])*'|"(?:\\"|[^"])*"|\S+)""",
)
PYTHON_LIST_BLOCK = re.compile(
    r'"appsettings"\s*,\s*\n\s*"list"\s*,(?P<body>.*?)(?=\n\s*\])',
    re.DOTALL,
)
PYTHON_QUERY_LITERAL = re.compile(
    r"""--query["']\s*,\s*(?P<query>f?(?:\"[^\"]*\"|'[^']*'))""",
)


class HostedAppSettingsError(ValueError):
    """Operator-facing rejection for an unsafe settings name or listing."""


def require_exact_setting_name(name: str) -> str:
    if not isinstance(name, str) or not name or not EXACT_SETTING_NAME.fullmatch(name):
        raise HostedAppSettingsError("Reading a value requires one exact setting name")
    if any(token in name for token in UNSAFE_NAME_TOKENS):
        raise HostedAppSettingsError("Wildcard, prefix, or bulk value reads are rejected")
    return name


def resolve_setting_name(requested: str, names: Iterable[str]) -> str | None:
    """Return the hosted name that matches ``requested`` case-insensitively."""

    requested = require_exact_setting_name(requested)
    needle = requested.casefold()
    matches = {name for name in names if isinstance(name, str) and name.casefold() == needle}
    if not matches:
        return None
    if len(matches) != 1:
        raise HostedAppSettingsError("Setting name matches more than one hosted name")
    return next(iter(matches))


def classify_appsettings_list_query(query: str | None) -> str:
    """Return ``names``, ``exact-value``, ``helper``, or ``unsafe``."""

    if query is None:
        return "unsafe"
    raw = query.strip()
    if raw in {"$query", '"$query"', "'$query'"}:
        return "helper"
    if raw.startswith("f'") or raw.startswith('f"'):
        raw = raw[1:]
    text = raw.strip("'\"")
    text = re.sub(r"\s+", " ", text)
    if not text:
        return "unsafe"
    if text in {"[].name", "length(@)"}:
        return "names"
    if EXACT_VALUE_QUERY.fullmatch(text):
        return "exact-value"
    if ".value" in text:
        return "unsafe"
    if NAMES_ONLY_FILTERED.fullmatch(text):
        return "names"
    return "unsafe"


def _collapse_continuations(text: str) -> str:
    return re.sub(r"\\\n", " ", text)


def iter_shell_listing_queries(text: str) -> Iterator[str | None]:
    collapsed = _collapse_continuations(text)
    for match in LISTING_COMMAND.finditer(collapsed):
        window = collapsed[match.end() : match.end() + 800]
        found = QUERY_ARGUMENT.search(window)
        if found is None:
            yield None
            continue
        yield found.group(1)


def iter_python_listing_queries(text: str) -> Iterator[str | None]:
    for match in PYTHON_LIST_BLOCK.finditer(text):
        body = match.group("body")
        found = PYTHON_QUERY_LITERAL.search(body)
        if found is None:
            if "--query" in body:
                yield "$query"
            else:
                yield None
            continue
        yield found.group("query")


def iter_listing_queries(path: Path, text: str | None = None) -> Iterator[tuple[Path, str | None]]:
    source = path.read_text() if text is None else text
    if path.suffix == ".py":
        queries: Iterable[str | None] = iter_python_listing_queries(source)
    else:
        queries = iter_shell_listing_queries(source)
    for query in queries:
        yield path, query


def scan_operator_listings(root: Path) -> list[tuple[Path, str | None, str]]:
    """Classify every hosted app-settings listing under scripts and workflows."""

    findings: list[tuple[Path, str | None, str]] = []
    roots = [root / "scripts", root / ".github" / "workflows"]
    for base in roots:
        if not base.exists():
            continue
        for path in sorted(base.rglob("*")):
            if not path.is_file() or path.suffix not in {".sh", ".py", ".yml", ".yaml"}:
                continue
            if path.name.endswith(".py") and path.parent.name == "tests" and path.name.startswith("test_"):
                continue
            for listing_path, query in iter_listing_queries(path):
                findings.append((listing_path, query, classify_appsettings_list_query(query)))
    return findings
