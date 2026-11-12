#!/usr/bin/env bash
set -euo pipefail

pin="${ELSA_CORE_PIN:-59b14969027b5cff1273f54970d90da189f4ff0f}"
root="$(cd "$(dirname "$0")" && pwd)"
dest="${ElsaCoreRoot:-/tmp/spike/elsa-core}"

mkdir -p "$(dirname "$dest")"

if [[ ! -d "$dest/.git" ]]; then
  git clone --filter=blob:none https://github.com/elsa-workflows/elsa-core.git "$dest"
fi

git -C "$dest" fetch --depth=1 origin "$pin"
git -C "$dest" checkout --detach "$pin"

head="$(git -C "$dest" rev-parse HEAD)"
if [[ "$head" != "$pin" ]]; then
  echo "Pinned elsa-core HEAD $head is not $pin" >&2
  exit 1
fi

echo "elsa-core pinned at $head"
