#!/usr/bin/env python3
"""Write the sorted SQL Server migration ids of a source checkout to a file (the harness's target identity list).

Usage: list-migration-ids.py <repository-root> <output-file>
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

MIGRATIONS = Path("src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.SqlServerMigrations/Migrations")
MIGRATION_FILE = re.compile(r"^([0-9]{14}_[A-Za-z0-9_]+)\.cs$")


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        return 2
    root, output = Path(argv[0]), Path(argv[1])
    ids = sorted(
        match.group(1)
        for path in (root / MIGRATIONS).glob("*.cs")
        if (match := MIGRATION_FILE.match(path.name)) and not path.name.endswith(".Designer.cs")
    )
    if not ids:
        return 2
    output.write_text("\n".join(ids) + "\n", encoding="utf-8")
    print(len(ids))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
