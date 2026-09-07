#!/usr/bin/env python3
"""Bounded, value-free managed lifecycle SLO sampler (#266).

Samples a fresh managed runtime health endpoint at a fixed interval for a bounded
window, optionally records Control's operational health evaluation for the same
instance, and optionally counts managed lifecycle signals that reached the Azure
Monitor sink during the window. It prints exactly one JSON line containing safe
UTC window timestamps and healthy/unhealthy/unknown/total counts. It never prints URLs,
hosts, tokens, response bodies or error messages. Missing or capped samples are
never counted as healthy.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

SCHEMA = "elsa-control.slo-sample/v1"
MINIMUM_WINDOW_SECONDS = 300.0
TOKEN_ENVIRONMENT_VARIABLE = "ELSA_CONTROL_SAMPLER_TOKEN"
RUNTIME_CATEGORIES = ("healthy", "unhealthy", "unknown")
CONTROL_STATUSES = ("healthy", "degraded", "failed", "unknown", "stale", "recovery_required")
CONTROL_CATEGORIES = (*CONTROL_STATUSES, "unhealthy_response", "other")
SIGNAL_NAME = re.compile(r"^managed_lifecycle\.[a-z_.]{1,80}$")
WORKSPACE_ID = re.compile(r"^[0-9a-fA-F-]{36}$")


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class SilentArgumentParser(argparse.ArgumentParser):
    """Rejects bad input with exit code 2 and no usage text, so operator values are never echoed."""

    def error(self, message: str) -> None:
        raise SystemExit(2)


def parse_arguments(argv: list[str]) -> argparse.Namespace:
    parser = SilentArgumentParser(add_help=False)
    parser.add_argument("--runtime-health-url", required=True)
    parser.add_argument("--control-health-url")
    parser.add_argument("--sink-workspace-id")
    parser.add_argument("--sink-signal", action="append", default=[])
    parser.add_argument("--duration-seconds", type=float, default=300.0)
    parser.add_argument("--interval-seconds", type=float, default=15.0)
    parser.add_argument("--minimum-window-seconds", type=float, default=300.0)
    parser.add_argument("--timeout-seconds", type=float, default=10.0)
    parser.add_argument("--az-bin", default="az")
    # Test seams only: the production contract is HTTPS targets and a five-minute floor.
    parser.add_argument("--allow-short-window", action="store_true")
    parser.add_argument("--allow-insecure-http", action="store_true")
    arguments = parser.parse_args(argv)
    urls = [url for url in (arguments.runtime_health_url, arguments.control_health_url) if url is not None]
    scheme = r"https?" if arguments.allow_insecure_http else r"https"
    invalid = (
        any(not re.fullmatch(scheme + r"://[^\s/]+(/[^\s]*)?", url) for url in urls)
        or (arguments.minimum_window_seconds < MINIMUM_WINDOW_SECONDS and not arguments.allow_short_window)
        or arguments.minimum_window_seconds <= 0
        or (arguments.control_health_url and not os.environ.get(TOKEN_ENVIRONMENT_VARIABLE))
        or (arguments.sink_signal and not arguments.sink_workspace_id)
        or (arguments.sink_workspace_id and not WORKSPACE_ID.fullmatch(arguments.sink_workspace_id))
        or any(not SIGNAL_NAME.fullmatch(signal) for signal in arguments.sink_signal)
        or not (0 < arguments.interval_seconds <= arguments.duration_seconds <= 7200)
        or arguments.timeout_seconds <= 0
    )
    if invalid:
        raise SystemExit(2)
    return arguments


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect is never a healthy sample and must never carry the bearer token elsewhere."""

    def redirect_request(self, *_: object, **__: object) -> None:
        return None


OPENER = urllib.request.build_opener(NoRedirect)


def fetch(url: str, timeout: float, token: str | None = None) -> tuple[int | None, object]:
    """Return (status, parsed JSON or None). Any transport failure or timeout returns (None, None)."""
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with OPENER.open(request, timeout=timeout) as response:
            body = response.read(65536)
            status = response.status
    except urllib.error.HTTPError as error:
        return error.code, None
    except Exception:
        return None, None
    try:
        return status, json.loads(body.decode("utf-8"))
    except Exception:
        return status, None


def classify_runtime(status: int | None) -> str:
    if status is None:
        return "unknown"
    return "healthy" if status == 200 else "unhealthy"


def classify_control(status: int | None, payload: object) -> str:
    if status is None:
        return "unknown"
    if status != 200 or not isinstance(payload, dict):
        return "unhealthy_response"
    value = payload.get("status")
    if not isinstance(value, str):
        return "other"
    normalized = re.sub(r"(?<!^)(?=[A-Z])", "_", value).lower()
    return normalized if normalized in CONTROL_STATUSES else "other"


def query_sink(az_bin: str, workspace_id: str, signals: list[str], start: str, end: str, timeout: float) -> dict[str, object]:
    names = ", ".join(f'"{signal}"' for signal in signals)
    query = (
        "AppMetrics | where TimeGenerated between (datetime({start}) .. datetime({end})) "
        "| where Name in ({names}) | summarize rows = count() by Name"
    ).format(start=start, end=end, names=names)
    try:
        completed = subprocess.run(
            [az_bin, "monitor", "log-analytics", "query", "-w", workspace_id, "--analytics-query", query, "-o", "json", "--only-show-errors"],
            capture_output=True,
            text=True,
            timeout=max(30.0, timeout * 6),
            check=False,
        )
        rows = json.loads(completed.stdout) if completed.returncode == 0 else None
    except Exception:
        rows = None
    if not isinstance(rows, list):
        return {"queried": False, "signals": None}
    counts = {signal: 0 for signal in signals}
    for row in rows:
        if isinstance(row, dict) and row.get("Name") in counts:
            try:
                counts[row["Name"]] = int(row.get("rows", 0))
            except (TypeError, ValueError):
                counts[row["Name"]] = 0
    return {"queried": True, "signals": counts}


def main(argv: list[str]) -> int:
    arguments = parse_arguments(argv)
    token = os.environ.get(TOKEN_ENVIRONMENT_VARIABLE)
    planned = max(1, round(arguments.duration_seconds / arguments.interval_seconds))
    window_start = utc_now()
    started = time.monotonic()
    runtime = {"total": 0, **dict.fromkeys(RUNTIME_CATEGORIES, 0)}
    control = {"total": 0, **dict.fromkeys(CONTROL_CATEGORIES, 0)} if arguments.control_health_url else None
    for sample in range(planned):
        runtime_status, _ = fetch(arguments.runtime_health_url, arguments.timeout_seconds)
        runtime["total"] += 1
        runtime[classify_runtime(runtime_status)] += 1
        if control is not None:
            status, payload = fetch(arguments.control_health_url, arguments.timeout_seconds, token)
            control["total"] += 1
            control[classify_control(status, payload)] += 1
        # Hold the full interval after every sample, so the window (and the sink query range) spans
        # the whole planned duration rather than ending at the last probe.
        remaining = started + (sample + 1) * arguments.interval_seconds - time.monotonic()
        if remaining > 0:
            time.sleep(remaining)
    window_end = utc_now()
    elapsed = time.monotonic() - started
    sink = None
    if arguments.sink_signal:
        sink = query_sink(arguments.az_bin, arguments.sink_workspace_id, arguments.sink_signal, window_start, window_end, arguments.timeout_seconds)
    complete = elapsed >= arguments.minimum_window_seconds and (sink is None or sink["queried"])
    healthy_window = complete and runtime["healthy"] == runtime["total"] and (
        control is None or control["total"] == control["healthy"]
    )
    result = {
        "schema": SCHEMA,
        "windowStartUtc": window_start,
        "windowEndUtc": window_end,
        "intervalSeconds": arguments.interval_seconds,
        "plannedSamples": planned,
        "runtime": runtime,
        "control": control,
        "sink": sink,
        "complete": complete,
        "healthyWindow": healthy_window,
    }
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0 if healthy_window else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except SystemExit:
        raise
    except Exception:
        raise SystemExit(3)
