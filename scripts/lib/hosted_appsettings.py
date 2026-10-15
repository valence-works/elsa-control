"""Name-only projection helpers for hosted application-setting listings."""

from __future__ import annotations

import ast
import re
import shlex
from pathlib import Path
from typing import Iterable, Iterator

EXACT_SETTING_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.:-]*$")
UNSAFE_NAME_TOKENS = ("*", "?", "[", "]", ",", " ")
LISTING_WORDS = ("webapp", "config", "appsettings", "list")
LISTING_COMMAND = re.compile(
    r"""(?<![A-Za-z0-9_])["']?webapp["']?\s+["']?config["']?\s+["']?appsettings["']?\s+["']?list["']?(?![A-Za-z0-9_])"""
)
SHELL_VARIABLE = re.compile(r"^\$[A-Za-z_][A-Za-z0-9_]*$|^\$\{[A-Za-z_][A-Za-z0-9_]*\}$")
INDIRECT_COMMAND_WORDS = frozenset({"eval", "alias", "source"})
HELPER_QUERY_VALUES = {"$query", '"$query"', "'$query'"}
HELPER_REPO_PATH = Path("scripts") / "lib" / "hosted-appsettings.sh"
PLACEHOLDER_QUERY_PATHS = frozenset(
    {
        HELPER_REPO_PATH,
        Path("scripts") / "staging_stripe_reconcile.py",
        Path(".github") / "workflows" / "azure-api-deploy.yml",
    }
)
IDENTIFIER = r"[A-Za-z0-9_.:-]+"
PLACEHOLDER = r"(?:\$\{[A-Za-z_][A-Za-z0-9_]*\}|\{[A-Za-z_][A-Za-z0-9_]*\})"
UNICODE_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})")


def _query_regexes(literal: str) -> tuple[re.Pattern[str], re.Pattern[str]]:
    filter_term = rf"(?:name=='{literal}'|starts_with\(name,\s*'{literal}'\))"
    filt = rf"\[\?{filter_term}(?: \|\| {filter_term})*\]"
    names_only = re.compile(
        rf"^(?:\[\]\.name|length\(@\)|{filt}\.name|{filt} \| length\(@\))$"
    )
    exact_value = re.compile(
        rf"^\[\?name=='{literal}'\]"
        rf"(?: \| \[0\]\.(?:value|slotSetting)| \| length\(@\))?$"
    )
    return names_only, exact_value


NAMES_ONLY_IDENT, EXACT_VALUE_IDENT = _query_regexes(IDENTIFIER)
NAMES_ONLY_PLACEHOLDER, EXACT_VALUE_PLACEHOLDER = _query_regexes(
    rf"(?:{IDENTIFIER}|{PLACEHOLDER})"
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


def _repo_relative(path: Path | None, root: Path | None) -> Path | None:
    if path is None or root is None:
        return None
    base = root.resolve()
    candidate = path if path.is_absolute() else base / path
    try:
        return candidate.resolve().relative_to(base)
    except ValueError:
        return None


def _helper_query_allowed(path: Path | None, root: Path | None) -> bool:
    return _repo_relative(path, root) == HELPER_REPO_PATH


def _placeholder_query_allowed(path: Path | None, root: Path | None) -> bool:
    relative = _repo_relative(path, root)
    return relative in PLACEHOLDER_QUERY_PATHS


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
    if EXACT_VALUE_IDENT.fullmatch(text):
        return "exact-value"
    if NAMES_ONLY_IDENT.fullmatch(text):
        return "names"
    if _placeholder_query_allowed(path, root):
        if EXACT_VALUE_PLACEHOLDER.fullmatch(text):
            return "exact-value"
        if NAMES_ONLY_PLACEHOLDER.fullmatch(text):
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


def _fold_shell_variables(tokens: list[str]) -> list[str]:
    folded: list[str] = []
    index = 0
    while index < len(tokens):
        token = tokens[index]
        if token == "$" and index + 1 < len(tokens):
            nxt = tokens[index + 1]
            if (
                nxt == "{"
                and index + 3 < len(tokens)
                and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", tokens[index + 2] or "")
                and tokens[index + 3] == "}"
            ):
                folded.append("${" + tokens[index + 2] + "}")
                index += 4
                continue
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", nxt):
                folded.append("$" + nxt)
                index += 2
                continue
        folded.append(token)
        index += 1
    return folded


def _tokenize_shell(command: str) -> list[str] | None:
    lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
    lexer.whitespace_split = False
    try:
        return _fold_shell_variables(list(lexer))
    except ValueError:
        return None


def _source_mentions_listing(text: str) -> bool:
    if LISTING_COMMAND.search(text):
        return True
    return bool(re.search(r"""['"]appsettings['"]""", text) and re.search(r"""['"]list['"]""", text))


def _window_status(window: list[str | None], *, variables: bool) -> str:
    """Return ``listing``, ``variable``, or ``no`` for one four-word window."""

    exact = 0
    unknown = 0
    for expected, actual in zip(LISTING_WORDS, window):
        if actual == expected:
            exact += 1
            continue
        if actual is None or (variables and isinstance(actual, str) and SHELL_VARIABLE.fullmatch(actual)):
            unknown += 1
            continue
        return "no"
    if exact == 4:
        return "listing"
    if exact == 3 and unknown == 1:
        return "variable"
    return "no"


def _listing_status(tokens: list[str]) -> str:
    """Return ``listing``, ``variable``, or ``no`` for a tokenized command."""

    for index in range(len(tokens) - 3):
        status = _window_status(list(tokens[index : index + 4]), variables=True)
        if status != "no":
            return status
    return "no"


def _is_indirect_command(tokens: list[str]) -> bool:
    return any(token in INDIRECT_COMMAND_WORDS for token in tokens) or (bool(tokens) and tokens[0] == ".")


def _has_query_flag(tokens: list[str]) -> bool:
    return any(token == "--query" or token.startswith("--query=") for token in tokens)


def _query_arguments(tokens: list[str]) -> str | None:
    queries: list[str] = []
    for index, token in enumerate(tokens):
        if token == "--query":
            if index + 1 >= len(tokens):
                return None
            queries.append(tokens[index + 1])
        elif token.startswith("--query="):
            queries.append(token[len("--query=") :])
    if len(queries) != 1:
        return None
    return queries[0]


def iter_shell_listing_queries(text: str) -> Iterator[str | None]:
    for command in _iter_shell_commands(text):
        tokens = _tokenize_shell(command)
        if tokens is None:
            if _source_mentions_listing(command):
                yield None
            continue
        if _is_indirect_command(tokens) and _source_mentions_listing(command):
            yield None
            continue
        if tokens and SHELL_VARIABLE.fullmatch(tokens[0]) and _has_query_flag(tokens):
            yield None
            continue
        status = _listing_status(tokens)
        if status == "variable":
            yield None
            continue
        if status == "listing":
            if "`" in command:
                yield None
                continue
            yield _query_arguments(tokens)
            continue
        if _source_mentions_listing(command):
            yield None


def _constant_str(node: ast.AST) -> str | None:
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return node.value
    return None


def _joined_str(node: ast.JoinedStr) -> str | None:
    parts: list[str] = []
    for value in node.values:
        if isinstance(value, ast.Constant) and isinstance(value.value, str):
            parts.append(value.value)
            continue
        if isinstance(value, ast.FormattedValue) and isinstance(value.value, ast.Name):
            parts.append("{" + value.value.id + "}")
            continue
        return None
    return "".join(parts)


def _argv_string(node: ast.AST) -> str | None:
    constant = _constant_str(node)
    if constant is not None:
        return constant
    if isinstance(node, ast.JoinedStr):
        return _joined_str(node)
    return None


def _sequence_elements(node: ast.List | ast.Tuple) -> list[ast.AST | None]:
    elements: list[ast.AST | None] = []
    for element in node.elts:
        if isinstance(element, ast.Starred):
            elements.append(None)
            continue
        elements.append(element)
    return elements


def _python_argv_is_az(words: list[str | None]) -> bool:
    return any(word == "az" or (isinstance(word, str) and word.startswith("-")) for word in words)


def _sequence_listing(node: ast.List | ast.Tuple) -> tuple[bool, str | None]:
    elements = _sequence_elements(node)
    words = [None if element is None else _argv_string(element) for element in elements]
    if not _python_argv_is_az(words):
        return False, None
    found = False
    unparseable = False
    for index in range(len(words) - 3):
        status = _window_status(words[index : index + 4], variables=True)
        if status == "listing":
            found = True
        elif status == "variable":
            found = True
            unparseable = True
    if not found:
        return False, None
    if unparseable:
        return True, None
    return True, _query_arguments_from_words(words)


def _query_arguments_from_words(words: list[str | None]) -> str | None:
    queries: list[str] = []
    index = 0
    while index < len(words):
        word = words[index]
        if word == "--query":
            if index + 1 >= len(words) or words[index + 1] is None:
                return None
            queries.append(words[index + 1])
            index += 2
            continue
        if word is not None and word.startswith("--query="):
            queries.append(word[len("--query=") :])
        index += 1
    if len(queries) != 1:
        return None
    return queries[0]


def _call_keyword(node: ast.Call, name: str) -> ast.AST | None:
    for keyword in node.keywords:
        if keyword.arg == name:
            return keyword.value
    return None


def _call_uses_shell(node: ast.Call) -> bool:
    value = _call_keyword(node, "shell")
    return isinstance(value, ast.Constant) and value.value is True


def _call_command_string(node: ast.Call) -> str | None:
    if not node.args:
        return None
    return _argv_string(node.args[0])


def _iter_call_strings(node: ast.Call) -> Iterator[str]:
    for child in ast.walk(node):
        constant = _constant_str(child)
        if constant is not None:
            yield constant
            continue
        if isinstance(child, ast.JoinedStr):
            joined = _joined_str(child)
            if joined is not None:
                yield joined


SUBPROCESS_FUNCS = frozenset({"run", "check_output", "Popen", "call", "check_call", "system"})


def _call_name(node: ast.Call) -> str | None:
    func = node.func
    if isinstance(func, ast.Name):
        return func.id
    if isinstance(func, ast.Attribute):
        return func.attr
    return None


def _call_mentions_listing(node: ast.Call) -> bool:
    strings = list(_iter_call_strings(node))
    if any(LISTING_COMMAND.search(value) for value in strings):
        return True
    name = _call_name(node)
    return name in SUBPROCESS_FUNCS and "appsettings" in strings and "list" in strings


def iter_python_listing_queries(text: str) -> Iterator[str | None]:
    try:
        tree = ast.parse(text)
    except SyntaxError:
        if _source_mentions_listing(text):
            yield None
        return

    covered_lists: set[int] = set()
    covered_calls: set[int] = set()

    for node in ast.walk(tree):
        if isinstance(node, (ast.List, ast.Tuple)):
            found, query = _sequence_listing(node)
            if found:
                covered_lists.add(id(node))
                yield query

    for node in ast.walk(tree):
        if not isinstance(node, ast.Call) or not _call_uses_shell(node):
            continue
        if node.args and id(node.args[0]) in covered_lists:
            covered_calls.add(id(node))
            continue
        command = _call_command_string(node)
        if command is None:
            if _call_mentions_listing(node):
                covered_calls.add(id(node))
                yield None
            continue
        yielded = False
        for query in iter_shell_listing_queries(command):
            yielded = True
            yield query
        if yielded or _source_mentions_listing(command):
            covered_calls.add(id(node))

    mentioning = [
        node
        for node in ast.walk(tree)
        if isinstance(node, ast.Call)
        and id(node) not in covered_calls
        and not (node.args and id(node.args[0]) in covered_lists)
        and _call_mentions_listing(node)
    ]
    for node in mentioning:
        if any(node is not other and node in ast.walk(other) for other in mentioning):
            continue
        yield None


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
