#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${PATH}"
export ElsaCoreRoot="${ElsaCoreRoot:-/tmp/spike/elsa-core}"

"$root/restore-elsa-core.sh"

dotnet restore "$root/Spike.sln" --configfile "$root/NuGet.Config" /p:ElsaCoreRoot="$ElsaCoreRoot"
dotnet test "$root/Spike.sln" --configuration Release --no-restore --logger "console;verbosity=normal" /p:ElsaCoreRoot="$ElsaCoreRoot"
