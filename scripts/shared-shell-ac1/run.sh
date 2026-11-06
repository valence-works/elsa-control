#!/usr/bin/env bash
# AC1 Shared-shell density harness for valence-works/elsa-control#798.
# Each (mode, group count) runs in a fresh process so density numbers are not
# contaminated by leftover shells, JIT, or ALC state.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
CONFIG="${CONFIG:-Release}"
ITERATIONS="${ITERATIONS:-10}"
WARMUP="${WARMUP:-1}"
# Do not read GROUPS from the environment; cloud/CI images sometimes set it.
GROUP_COUNTS_CSV="${AC1_GROUPS:-1,5,20}"
IFS=',' read -r -a GROUP_COUNTS <<< "$GROUP_COUNTS_CSV" || true
OUT_DIR="${OUT_DIR:-$ROOT/results}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

mkdir -p "$OUT_DIR"

echo "==> Building verified package"
dotnet build "$ROOT/verified-package/ElsaControl.SharedShellAc1.VerifiedPackage.csproj" -c "$CONFIG" -v q

PACKAGE="$(find "$ROOT/verified-package/bin/$CONFIG" -name 'ElsaControl.SharedShellAc1.VerifiedPackage.dll' | head -n 1)"
if [[ -z "$PACKAGE" || ! -f "$PACKAGE" ]]; then
  echo "Verified package DLL was not produced." >&2
  exit 1
fi

echo "==> Building harness"
dotnet build "$ROOT/ElsaControl.SharedShellAc1.csproj" -c "$CONFIG" -v q

echo "==> Statistics tests"
dotnet test "$ROOT/tests/ElsaControl.SharedShellAc1.Tests.csproj" -c "$CONFIG" --filter 'FullyQualifiedName~StatisticsTests' -v q

echo "==> Collectible ALC unload test"
dotnet test "$ROOT/tests/ElsaControl.SharedShellAc1.Tests.csproj" -c "$CONFIG" --filter 'FullyQualifiedName~CollectibleUnloadTests' -v q

run_one() {
  local mode="$1"
  local groups="$2"
  local dest="$3"
  echo "==> $mode groups=$groups iterations=$ITERATIONS warmup=$WARMUP"
  dotnet run --project "$ROOT/ElsaControl.SharedShellAc1.csproj" -c "$CONFIG" --no-build -- \
    --package "$PACKAGE" \
    --mode "$mode" \
    --groups "$groups" \
    --iterations "$ITERATIONS" \
    --warmup "$WARMUP" \
    --output "$dest"
}

PARTS=()
for g in "${GROUP_COUNTS[@]}"; do
  dest="$OUT_DIR/ac1-$STAMP-density-$g.json"
  run_one density "$g" "$dest"
  PARTS+=("$dest")
  dest="$OUT_DIR/ac1-$STAMP-cold-start-$g.json"
  run_one cold-start "$g" "$dest"
  PARTS+=("$dest")
done

UNLOAD="$OUT_DIR/ac1-$STAMP-unload.json"
run_one unload 1 "$UNLOAD"
PARTS+=("$UNLOAD")

OUTPUT="$OUT_DIR/ac1-$STAMP.json"
python3 - "$OUTPUT" "${PARTS[@]}" <<'PY'
import json, sys
from pathlib import Path
out = Path(sys.argv[1])
parts = [json.loads(Path(p).read_text()) for p in sys.argv[2:]]
merged = {
    "Issue": parts[0]["Issue"],
    "CapturedAtUtc": parts[0]["CapturedAtUtc"],
    "Machine": parts[0]["Machine"],
    "PackagePath": parts[0]["PackagePath"],
    "Iterations": parts[0]["Iterations"],
    "Warmup": parts[0]["Warmup"],
    "Caveats": parts[0]["Caveats"],
    "Density": [],
    "ColdStart": [],
    "Unload": None,
}
for part in parts:
    merged["Density"].extend(part.get("Density") or [])
    merged["ColdStart"].extend(part.get("ColdStart") or [])
    if part.get("Unload"):
        merged["Unload"] = part["Unload"]
out.write_text(json.dumps(merged, indent=2) + "\n")
print(out.read_text())
PY

ln -sfn "$(basename "$OUTPUT")" "$OUT_DIR/latest.json"
echo "==> Wrote $OUTPUT"
echo "==> Also linked $OUT_DIR/latest.json"
