#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: scripts/validate-azure-api-candidate.sh <descriptor> <run-id> <expected-digest> <expected-image-repository> <expected-github-repository> <output-file>" >&2
}

fail() {
  echo "Azure API candidate validation failed: $1" >&2
  exit 1
}

if (($# != 6)); then
  usage
  exit 2
fi

descriptor="$1"
candidate_run_id="$2"
expected_digest="$3"
expected_image_repository="$4"
expected_github_repository="$5"
output_file="$6"

[[ -f "$descriptor" && ! -L "$descriptor" ]] || fail "candidate descriptor is unavailable"
descriptor_size="$(wc -c < "$descriptor")"
if (( descriptor_size > 8192 )); then
  fail "candidate descriptor is too large"
fi
[[ "$candidate_run_id" =~ ^[0-9]+$ ]] || fail "candidate run identity is invalid"
[[ "$expected_digest" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "candidate digest is invalid"
[[ -n "$expected_image_repository" && -n "$expected_github_repository" ]] || fail "candidate repository configuration is incomplete"
[[ -n "$output_file" && ! -L "$output_file" ]] || fail "candidate output path is invalid"
command -v jq >/dev/null 2>&1 || fail "JSON validator is unavailable"

if [[ "${TARGET_ENVIRONMENT:-}" == test && "${GITHUB_REF:-}" == refs/heads/candidate/staging ]]; then
  trusted_branch=candidate/staging
elif [[ "${GITHUB_REF:-}" == refs/heads/main ]]; then
  trusted_branch=main
else
  fail "candidate promotion ref is not approved for this environment"
fi

jq -e '
  type == "object" and
  ((keys | sort) == ([
    "artifactSchemaVersion",
    "buildRunId",
    "buildRunNumber",
    "digest",
    "repository",
    "sourceRepository",
    "sourceSha"
  ] | sort)) and
  .artifactSchemaVersion == 1 and
  (.buildRunId | type) == "string" and
  (.buildRunNumber | type) == "string" and
  (.digest | type) == "string" and
  (.repository | type) == "string" and
  (.sourceRepository | type) == "string" and
  (.sourceSha | type) == "string"
' "$descriptor" >/dev/null 2>&1 || fail "candidate descriptor schema is invalid"

source_sha="$(jq -er '.sourceSha' "$descriptor")" || fail "candidate source identity is missing"
descriptor_run_id="$(jq -er '.buildRunId' "$descriptor")" || fail "candidate run identity is missing"
descriptor_run_number="$(jq -er '.buildRunNumber' "$descriptor")" || fail "candidate run number is missing"
descriptor_digest="$(jq -er '.digest' "$descriptor")" || fail "candidate digest is missing"
descriptor_repository="$(jq -er '.repository' "$descriptor")" || fail "candidate image repository is missing"
descriptor_source_repository="$(jq -er '.sourceRepository' "$descriptor")" || fail "candidate source repository is missing"

[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "candidate source identity is invalid"
[[ "$descriptor_run_id" =~ ^[0-9]+$ && "$descriptor_run_id" == "$candidate_run_id" ]] || fail "candidate run identity does not match"
[[ "$descriptor_run_number" =~ ^[0-9]+$ ]] || fail "candidate run number is invalid"
[[ "$descriptor_digest" == "$expected_digest" ]] || fail "candidate digest does not match the expected digest"
[[ "$descriptor_repository" == "$expected_image_repository" ]] || fail "candidate image repository does not match the configured repository"
[[ "$descriptor_source_repository" == "$expected_github_repository" ]] || fail "candidate source repository does not match this repository"

run_file="$(mktemp)"
trap 'rm -f "$run_file"' EXIT
command -v gh >/dev/null 2>&1 || fail "GitHub CLI is unavailable"
if ! gh api "repos/$expected_github_repository/actions/runs/$candidate_run_id" >"$run_file" 2>/dev/null; then
  fail "candidate workflow run could not be verified"
fi

jq -e \
  --arg run_id "$candidate_run_id" \
  --arg run_number "$descriptor_run_number" \
  --arg source_sha "$source_sha" \
  --arg repository "$expected_github_repository" \
  --arg trusted_branch "$trusted_branch" \
  '(.id | tostring) == $run_id and
   .name == "Azure Control API Deploy" and
   .path == ".github/workflows/azure-api-deploy.yml" and
   .repository.full_name == $repository and
   .head_repository.full_name == $repository and
   .status == "completed" and
   .conclusion == "success" and
   .event == "workflow_dispatch" and
   .head_branch == $trusted_branch and
   .head_sha == $source_sha and
   (.run_number | tostring) == $run_number' \
  "$run_file" >/dev/null 2>&1 || fail "candidate workflow run is not a trusted successful build"

command -v git >/dev/null 2>&1 || fail "Git is unavailable"
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "source repository is unavailable"
if [[ "$(git rev-parse --is-shallow-repository 2>/dev/null || true)" == true ]]; then
  git fetch --no-tags --quiet --unshallow origin "$trusted_branch" >/dev/null 2>&1 || fail "candidate ancestry could not be verified"
else
  git fetch --no-tags --quiet origin "$trusted_branch" >/dev/null 2>&1 || fail "candidate ancestry could not be verified"
fi
git cat-file -e "$source_sha^{commit}" >/dev/null 2>&1 || fail "candidate source commit could not be verified"
git merge-base --is-ancestor "$source_sha" "origin/$trusted_branch" >/dev/null 2>&1 || fail "candidate source is not an ancestor of the approved branch"

printf '%s\n' \
  "candidate_repository=$descriptor_repository" \
  "candidate_digest=$descriptor_digest" \
  "candidate_image=$descriptor_repository@$descriptor_digest" \
  "candidate_source_sha=$source_sha" \
  "candidate_build_run_id=$descriptor_run_id" \
  "candidate_build_number=$descriptor_run_number" >>"$output_file"
