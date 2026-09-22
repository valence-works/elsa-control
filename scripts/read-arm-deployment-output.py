#!/usr/bin/env python3
"""Read one ARM deployment output without depending on Azure's key casing."""

from __future__ import annotations

import json
import sys


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: read-arm-deployment-output.py <name>")

    requested_name = sys.argv[1]
    outputs = json.load(sys.stdin)
    matching_names = [
        name for name in outputs if name.casefold() == requested_name.casefold()
    ]
    if len(matching_names) != 1:
        raise SystemExit(f"Missing deployment output: {requested_name}")

    value = outputs[matching_names[0]].get("value")
    if value is None or value == "":
        raise SystemExit(f"Missing deployment output: {requested_name}")

    print(value)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
