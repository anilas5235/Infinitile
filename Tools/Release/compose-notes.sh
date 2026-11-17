#!/usr/bin/env bash
# Writes the release notes to stdout: the Highlights (Markdown file, may be empty or missing) and, when <pr-list> is
# true, GitHub's generated list of the pull requests merged since <previous tag>.
#   Tools/Release/compose-notes.sh <tag> <target commit> <previous tag or -> <highlights file> <pr-list true|false>
# Needs gh with a token that can read the repository (GH_TOKEN in the workflow, gh auth login locally).
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <tag> <target commit> <previous tag or -> <highlights file> <pr-list true|false>" >&2
  exit 2
fi
tag=$1 target=$2 previous=$3 highlights=$4 pr_list=$5
repo=${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}

if [ -f "$highlights" ] && grep -q '[^[:space:]]' "$highlights"; then
  # Strip the CR of Windows line ends; add the heading unless the text starts with its own.
  text=$(tr -d '\r' < "$highlights")
  case "$text" in
    '#'*) ;;
    *) printf '## Highlights\n\n' ;;
  esac
  printf '%s\n\n' "$text"
fi

if [ "$pr_list" = true ]; then
  args=(-f "tag_name=$tag" -f "target_commitish=$target")
  if [ "$previous" != - ]; then args+=(-f "previous_tag_name=$previous"); fi
  gh api --method POST "repos/$repo/releases/generate-notes" "${args[@]}" --jq .body
  echo
fi
