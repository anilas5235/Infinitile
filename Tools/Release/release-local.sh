#!/usr/bin/env bash
# Local fallback for the Release workflow (.github/workflows/release.yml): builds the Windows and Linux players on
# this PC with the project's Unity version, packs them and publishes the GitHub release. See Docs/Releases.md.
#   Tools/Release/release-local.sh patch|minor|major [--highlights FILE] [--no-pr-list] [--dry-run] [--yes]
#     --highlights FILE  Markdown put above the PR list (heading "## Highlights" added unless the file has one)
#     --no-pr-list       leave out GitHub's list of the PRs merged since the last release
#     --dry-run          build and pack only: no tag, no release (the checkout may be on any commit)
#     --yes              publish without asking once more
# Run it in Git Bash at the root of a checkout of master that matches origin/master, with the Unity Editor closed
# on that checkout. UNITY_EXE overrides the Editor path (default: the Hub's folder of ProjectVersion.txt's version).
# The archives and the Unity logs go to Builds/Release/<version>/ (gitignored).
set -euo pipefail

usage() {
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

bump="" highlights="" pr_list=true dry_run=false yes=false
while [ $# -gt 0 ]; do
  case "$1" in
    patch | minor | major) bump=$1 ;;
    --highlights)
      [ $# -ge 2 ] || usage
      highlights=$2
      shift
      ;;
    --no-pr-list) pr_list=false ;;
    --dry-run) dry_run=true ;;
    --yes) yes=true ;;
    *) usage ;;
  esac
  shift
done
[ -n "$bump" ] || usage
if [ -n "$highlights" ] && [ ! -f "$highlights" ]; then
  echo "no highlights file '$highlights'" >&2
  exit 1
fi
[ -z "$highlights" ] || highlights=$(cd "$(dirname "$highlights")" && pwd)/$(basename "$highlights")
if [ "$dry_run" = false ] && [ "$yes" = false ] && [ ! -t 0 ]; then
  # Found out now, not after the builds: without a terminal the publish question cannot be answered.
  echo "no terminal to ask before publishing: pass --yes (or --dry-run)" >&2
  exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)
cd "$(git -C "$here" rev-parse --show-toplevel)"
root=$(pwd)

# --- checks -------------------------------------------------------------------------------------------------------
git fetch --quiet --tags origin
dirty_before=$(git status --porcelain --untracked-files=no)
dirty_names=$(git diff --name-only HEAD)
if [ "$dry_run" = false ]; then
  if [ -n "$dirty_before" ]; then
    echo "the checkout has uncommitted changes; a release is built from a clean master" >&2
    exit 1
  fi
  git fetch --quiet origin master
  if [ "$(git rev-parse HEAD)" != "$(git rev-parse origin/master)" ]; then
    echo "HEAD is not origin/master; check out master and pull first (or use --dry-run)" >&2
    exit 1
  fi
fi

lockfile="$root/Temp/UnityLockfile"
if [ -f "$lockfile" ] && ! powershell.exe -NoProfile -Command \
  "try { [IO.File]::Open('$(cygpath -w "$lockfile")','Open','ReadWrite','None').Close() } catch { exit 1 }" > /dev/null 2>&1; then
  echo "a Unity Editor has this project open; close it first" >&2
  exit 1
fi

unity_version=$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt | tr -d '\r')
unity=${UNITY_EXE:-"/c/Program Files/Unity/Hub/Editor/$unity_version/Editor/Unity.exe"}
if [ ! -x "$unity" ]; then
  echo "no Unity $unity_version at '$unity' (set UNITY_EXE)" >&2
  exit 1
fi

next=$(bash "$here/next-version.sh" "$bump")
read -r previous version <<< "$next"
tag="v$version"
out="$root/Builds/Release/$version"
echo "release $tag (previous ${previous}), Unity $unity_version, output $out"
rm -rf "$out"
mkdir -p "$out"

# --- build --------------------------------------------------------------------------------------------------------
build() { # <BuildTarget> <-buildTarget name> <platform> <executable>
  local target=$1 switch=$2 platform=$3 executable=$4 start
  start=$(date +%s)
  echo "building $platform ..."
  if ! "$unity" -batchmode -quit -projectPath "$(cygpath -w "$root")" -buildTarget "$switch" \
    -executeMethod Engine.Editor.Build.ReleaseBuild.BuildFromCommandLine \
    -customBuildTarget "$target" -customBuildPath "$(cygpath -w "$out/$platform/$executable")" \
    -buildVersion "$version" -logFile "$(cygpath -w "$out/unity-$platform.log")"; then
    echo "the $platform build failed; see $out/unity-$platform.log" >&2
    return 1
  fi
  echo "$platform built in $(($(date +%s) - start)) s"
}

restore_build_changes() {
  # A batch build re-saves project settings and render pipeline assets; put back the ones that were clean before.
  local file changed=()
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    if ! grep -qxF "$file" <<< "$dirty_names"; then changed+=("$file"); fi
  done < <(git diff --name-only -- ProjectSettings Assets/Settings)
  if [ ${#changed[@]} -gt 0 ]; then
    echo "putting back what the build re-saved: ${changed[*]}"
    git checkout -- "${changed[@]}"
  fi
  if [ "$(git status --porcelain --untracked-files=no)" != "$dirty_before" ]; then
    echo "note: the build changed other tracked files too (see git status)" >&2
  fi
}
trap restore_build_changes EXIT

# Windows last, so the project's active build target stays Win64 for the Editor.
build StandaloneLinux64 Linux64 linux-x64 Infinitile.x86_64
build StandaloneWindows64 Win64 windows-x64 Infinitile.exe

# --- pack and publish ---------------------------------------------------------------------------------------------
archives=()
for platform in windows-x64 linux-x64; do
  archives+=("$(bash "$here/package.sh" "$out/$platform" "$platform" "$version" "$out")")
done
ls -l "${archives[@]}"

bash "$here/compose-notes.sh" "$tag" "$(git rev-parse HEAD)" "$previous" "${highlights:-/dev/null}" "$pr_list" \
  > "$out/notes.md"
echo "--- notes ($out/notes.md) ---"
cat "$out/notes.md"
echo "---"

if [ "$dry_run" = true ]; then
  echo "dry run of $tag ($bump after $previous): nothing published"
  exit 0
fi
if [ "$yes" = false ]; then
  answer=""
  if ! read -r -p "Publish $tag as the latest release of $(gh repo view --json nameWithOwner --jq .nameWithOwner)? [y/N] " answer; then
    echo >&2
    echo "no answer (no terminal?): not published; run again with --yes to publish without the question" >&2
    exit 1
  fi
  [ "$answer" = y ] || [ "$answer" = Y ] || {
    echo "not published"
    exit 1
  }
fi
gh release create "$tag" "${archives[@]}" --target "$(git rev-parse HEAD)" --title "Infinitile $version" \
  --notes-file "$out/notes.md" --latest
