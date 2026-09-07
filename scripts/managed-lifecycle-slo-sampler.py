#!/usr/bin/env python3
"""Bounded, value-free managed lifecycle SLO sampler (#266).

Samples a fresh managed runtime health endpoint at a fixed interval for a bounded
window, optionally records Control's operational health evaluation for the same
instance, and optionally counts managed lifecycle signals that reached the Azure
Monitor sink during the window. It prints exactly one JSON line containing safe
UTC window timestamps and healthy/total/unknown counts. It never prints URLs,
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
TOKEN_ENVIRONMENT_VARIABLE = "ELSA_CONTROL_SAMPLER_TOKEN"
CONTROL_STATUSES = ("healthy", "degraded", "failed", "unknown", "stale", "recovery_required")
SIGNAL_NAME = re.compile(r"^managed_lifecycle\.[a-z_.]{1,80}$")
WORKSPACE_ID = re.compile(r"^[0-9a-fA-F-]{36}$")


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--runtime-health-url", required=True)
    parser.add_argument("--control-health-url")
    parser.add_argument("--sink-workspace-id")
    parser.add_argument("--sink-signal", action="append", default=[])
    parser.add_argument("--duration-seconds", type=float, default=300.0)
    parser.add_argument("--interval-seconds", type=float, default=15.0)
    parser.add_argument("--minimum-window-seconds", type=float, default=300.0)
    parser.add_argument("--timeout-seconds", type=float, default=10.0)
    parser.add_argument("--az-bin", default="az")
    arguments = parser.parse_args(argv)
    for url in (arguments.runtime_health_url, arguments.control_health_url):
        if url is not None and not re.fullmatch(r"https?://[^\s/]+(/[^\s]*)?", url):
            raise SystemExit(2)
    if arguments.control_health_url and not os.environ.get(TOKEN_ENVIRONMENT_VARIABLE):
        raise SystemExit(2)
    if arguments.sink_signal and not arguments.sink_workspace_id:
        raise SystemExit(2)
    if arguments.sink_workspace_id and not WORKSPACE_ID.fullmatch(arguments.sink_workspace_id):
        raise SystemExit(2)
    if any(not SIGNAL_NAME.fullmatch(signal) for signal in arguments.sink_signal):
        raise SystemExit(2)
    if not (0 < arguments.interval_seconds <= arguments.duration_seconds <= 7200) or arguments.timeout_seconds <= 0:
        raise SystemExit(2)
    return arguments


def fetch(url: str, timeout: float, token: str | None = None) -> tuple[int | None, object]:
    """Return (status, parsed JSON or None). Any transport failure returns (None, None)."""
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - operator-supplied HTTPS target
            body = response.read(65536)
            status = response.status
    except urllib.error.HTTPError as error:
        return error.code, None
    except Exception:  # noqa: BLE001 - value-free by design
        return None, None
    try:
        return status, json.loads(body.decode("utf-8"))
    except Exception:  # noqa: BLE001
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
    except Exception:  # noqa: BLE001
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
    planned = int(arguments.duration_seconds // arguments.interval_seconds)
    window_start = utc_now()
    started = time.monotonic()
    runtime = {"total": 0, "healthy": 0, "unhealthy": 0, "unknown": 0}
    control = None
    if arguments.control_health_url:
        control = {"total": 0, "unknown": 0, "unhealthy_response": 0, "other": 0, **{status: 0 for status in CONTROL_STATUSES}}
    for sample in range(planned):
        runtime_status, _ = fetch(arguments.runtime_health_url, arguments.timeout_seconds)
        runtime["total"] += 1
        runtime[classify_runtime(runtime_status)] += 1
        if control is not None:
            status, payload = fetch(arguments.control_health_url, arguments.timeout_seconds, token)
            control["total"] += 1
            control[classify_control(status, payload)] += 1
        next_sample = started + (sample + 1) * arguments.interval_seconds
        remaining = next_sample - time.monotonic()
        if remaining > 0 and sample + 1 < planned:
            time.sleep(remaining)
    window_end = utc_now()
    elapsed = time.monotonic() - started
    sink = None
    if arguments.sink_signal:
        sink = query_sink(arguments.az_bin, arguments.sink_workspace_id, arguments.sink_signal, window_start, window_end, arguments.timeout_seconds)
    complete = (
        runtime["total"] == planned
        and planned > 0
        and elapsed + arguments.interval_seconds >= arguments.minimum_window_seconds
        and (sink is None or sink["queried"])
    )
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
    except Exception:  # noqa: BLE001 - never leak exception text
        raise SystemExit(3)
