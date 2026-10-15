"""Name-only projection helpers for hosted application-setting listings."""

from __future__ import annotations

import re
import shlex
from pathlib import Path
from typing import Iterable, Iterator

EXACT_SETTING_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.:-]*$")
UNSAFE_NAME_TOKENS = ("*", "?", "[", "]", ",", " ")
LISTING_COMMAND = re.compile(r"webapp\s+config\s+appsettings\s+list\b")
HELPER_QUERY_VALUES = {"$query", '"$query"', "'$query'"}
HELPER_REPO_PATH = Path("scripts") / "lib" / "hosted-appsettings.sh"
LITERAL = r"(?:[A-Za-z0-9_.:-]+|\$\{[A-Za-z_][A-Za-z0-9_]*\}|\{[A-Za-z_][A-Za-z0-9_]*\})"
FILTER_TERM = rf"(?:name=='{LITERAL}'|starts_with\(name,\s*'{LITERAL}'\))"
FILTER = rf"\[\?{FILTER_TERM}(?: \|\| {FILTER_TERM})*\]"
NAMES_ONLY_QUERY = re.compile(
    rf"^(?:\[\]\.name|length\(@\)|{FILTER}\.name|{FILTER} \| length\(@\))$"
)
EXACT_VALUE_QUERY = re.compile(
    rf"^\[\?name=='{LITERAL}'\]"
    rf"(?: \| \[0\]\.(?:value|slotSetting)| \| length\(@\))?$"
)
PYTHON_LIST_BLOCK = re.compile(
    r'"appsettings"\s*,\s*\n\s*"list"\s*,(?P<body>.*?)(?=\n\s*\])',
    re.DOTALL,
)
PYTHON_QUERY_LITERAL = re.compile(
    r"""--query["']\s*,\s*(?P<query>f?(?:\"[^\"]*\"|'[^']*'))""",
)
UNICODE_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})")


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


def _helper_query_allowed(path: Path | None, root: Path | None) -> bool:
    if path is None or root is None:
        return False
    base = root.resolve()
    candidate = path if path.is_absolute() else base / path
    try:
        relative = candidate.resolve().relative_to(base)
    except ValueError:
        return False
    return relative == HELPER_REPO_PATH


def _decode_query_escapes(text: str) -> str:
    def replace(match: re.Match[str]) -> str:
        hex_digits = match.group(1) or match.group(2)
        return chr(int(hex_digits, 16))

    return UNICODE_ESCAPE.sub(replace, text)


def classify_appsettings_list_query(
    query: str | None,
    *,
    path: Path | None = None,
    root: Path | None = None,
) -> str:
    """Return ``names``, ``exact-value``, ``helper``, or ``unsafe``."""

    if query is None:
        return "unsafe"
    raw = query.strip()
    if raw in HELPER_QUERY_VALUES:
        return "helper" if _helper_query_allowed(path, root) else "unsafe"
    if raw.startswith("f'") or raw.startswith('f"'):
        raw = raw[1:]
    text = _decode_query_escapes(raw.strip("'\""))
    text = re.sub(r"\s+", " ", text)
    if not text:
        return "unsafe"
    if EXACT_VALUE_QUERY.fullmatch(text):
        return "exact-value"
    if NAMES_ONLY_QUERY.fullmatch(text):
        return "names"
    return "unsafe"


def _normalize_source(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    return re.sub(r"\\\n", " ", text)


def _read_dollar_paren(text: str, start: int) -> tuple[str, str, int]:
    """Return ``($(…)``, inner script, index after the closing paren)."""

    index = start + 2
    depth = 1
    quote = None
    length = len(text)
    while index < length and depth:
        char = text[index]
        if quote == "'":
            if char == "'":
                quote = None
            index += 1
            continue
        if quote == '"':
            if char == "\\" and index + 1 < length:
                index += 2
                continue
            if char == '"':
                quote = None
                index += 1
                continue
            if text.startswith("$(", index):
                _, _, index = _read_dollar_paren(text, index)
                continue
            if char == "`":
                _, _, index = _read_backtick(text, index)
                continue
            index += 1
            continue
        if char in {"'", '"'}:
            quote = char
            index += 1
            continue
        if text.startswith("$(", index):
            _, _, index = _read_dollar_paren(text, index)
            continue
        if char == "`":
            _, _, index = _read_backtick(text, index)
            continue
        if char == "(":
            depth += 1
            index += 1
            continue
        if char == ")":
            depth -= 1
            index += 1
            continue
        index += 1
    inner = text[start + 2 : index - 1 if depth == 0 else length]
    return text[start:index], inner, index


def _read_backtick(text: str, start: int) -> tuple[str, str, int]:
    index = start + 1
    length = len(text)
    while index < length:
        char = text[index]
        if char == "\\" and index + 1 < length:
            index += 2
            continue
        if char == "`":
            inner = text[start + 1 : index]
            return text[start : index + 1], inner, index + 1
        index += 1
    return text[start:], text[start + 1 :], length


def _collect_substitutions(text: str) -> list[str]:
    inners: list[str] = []
    index = 0
    length = len(text)
    quote = None
    while index < length:
        char = text[index]
        if quote == "'":
            if char == "'":
                quote = None
            index += 1
            continue
        if quote == '"':
            if char == "\\" and index + 1 < length:
                index += 2
                continue
            if char == '"':
                quote = None
                index += 1
                continue
            if text.startswith("$(", index):
                _, inner, index = _read_dollar_paren(text, index)
                inners.append(inner)
                continue
            if char == "`":
                _, inner, index = _read_backtick(text, index)
                inners.append(inner)
                continue
            index += 1
            continue
        if char in {"'", '"'}:
            quote = char
            index += 1
            continue
        if text.startswith("$(", index):
            _, inner, index = _read_dollar_paren(text, index)
            inners.append(inner)
            continue
        if char == "`":
            _, inner, index = _read_backtick(text, index)
            inners.append(inner)
            continue
        index += 1
    return inners


def _is_unquoted_comment(text: str, index: int, buffer: list[str]) -> bool:
    if index > 0 and not text[index - 1].isspace():
        return False
    if buffer and not buffer[-1][-1:].isspace():
        return False
    return True


def _split_top_level_commands(text: str) -> list[str]:
    commands: list[str] = []
    buffer: list[str] = []
    index = 0
    length = len(text)

    def flush() -> None:
        command = "".join(buffer).strip()
        if command:
            commands.append(command)
        buffer.clear()

    while index < length:
        char = text[index]
        if char == "#" and _is_unquoted_comment(text, index, buffer):
            while index < length and text[index] != "\n":
                index += 1
            continue
        if char in {";", "\n"}:
            flush()
            index += 1
            continue
        if text.startswith("&&", index):
            flush()
            index += 2
            continue
        if text.startswith("||", index):
            flush()
            index += 2
            continue
        if char == "|":
            flush()
            index += 1
            continue
        if char == "'":
            end = text.find("'", index + 1)
            if end < 0:
                buffer.append(text[index:])
                break
            buffer.append(text[index : end + 1])
            index = end + 1
            continue
        if char == '"':
            quote = '"'
            index += 1
            buffer.append('"')
            while index < length:
                current = text[index]
                if current == "\\" and index + 1 < length:
                    buffer.append(text[index : index + 2])
                    index += 2
                    continue
                if text.startswith("$(", index):
                    chunk, _, index = _read_dollar_paren(text, index)
                    buffer.append(chunk)
                    continue
                if current == "`":
                    chunk, _, index = _read_backtick(text, index)
                    buffer.append(chunk)
                    continue
                buffer.append(current)
                index += 1
                if current == quote:
                    break
            continue
        if text.startswith("$(", index):
            chunk, _, index = _read_dollar_paren(text, index)
            buffer.append(chunk)
            continue
        if char == "`":
            chunk, _, index = _read_backtick(text, index)
            buffer.append(chunk)
            continue
        buffer.append(char)
        index += 1
    flush()
    return commands


def _iter_shell_commands(text: str) -> Iterator[str]:
    for segment in _split_top_level_commands(_normalize_source(text)):
        for inner in _collect_substitutions(segment):
            yield from _iter_shell_commands(inner)
        residual = segment
        for inner in _collect_substitutions(segment):
            residual = residual.replace(f"$({inner})", " ", 1)
            residual = residual.replace(f"`{inner}`", " ", 1)
        residual = residual.strip()
        if residual:
            yield residual


def _query_argument(command: str) -> str | None:
    lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
    lexer.whitespace_split = False
    try:
        tokens = list(lexer)
    except ValueError:
        return None
    for index, token in enumerate(tokens):
        if token == "--query" and index + 1 < len(tokens):
            return tokens[index + 1]
        if token.startswith("--query="):
            return token[len("--query=") :]
    return None


def iter_shell_listing_queries(text: str) -> Iterator[str | None]:
    for command in _iter_shell_commands(text):
        if not LISTING_COMMAND.search(command):
            continue
        yield _query_argument(command)


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
                findings.append(
                    (
                        listing_path,
                        query,
                        classify_appsettings_list_query(query, path=listing_path, root=root),
                    )
                )
    return findings
