#!/usr/bin/env bash
# Prints "<previous tag> <next version>" for a release: the previous tag is the highest vX.Y.Z tag in the
# repository ("-" when there is none), the next version is that one with <bump> raised.
#   Tools/Release/next-version.sh patch|minor|major
# The tags must be fetched (actions/checkout with fetch-depth 0, or git fetch --tags).
set -euo pipefail

bump=${1:-}
case "$bump" in
  patch | minor | major) ;;
  *)
    echo "usage: $0 patch|minor|major" >&2
    exit 2
    ;;
esac

previous=$(git tag --list 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -n 1 || true)
if [ -n "$previous" ]; then
  IFS=. read -r major minor patch <<< "${previous#v}"
else
  major=0 minor=0 patch=0
fi

case "$bump" in
  major) major=$((major + 1)) minor=0 patch=0 ;;
  minor) minor=$((minor + 1)) patch=0 ;;
  patch) patch=$((patch + 1)) ;;
esac
next="$major.$minor.$patch"

if git rev-parse -q --verify "refs/tags/v$next" > /dev/null; then
  echo "tag v$next exists already" >&2
  exit 1
fi
echo "${previous:--} $next"
