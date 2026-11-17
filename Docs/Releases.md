# Releases

Infinitile is published as GitHub releases on
[anilas5235/Infinitile](https://github.com/anilas5235/Infinitile/releases). Each release has a Windows x64 and a Linux
x64 player. Both are release builds (not development builds) using the Mono scripting backend. Versions follow
`major.minor.patch`, tags are `vX.Y.Z`, and the first release under this scheme is `v1.0.0`. The earlier tags `v0.2`
and `v0.3.0` were made by hand.

| Piece | Where |
|---|---|
| Workflow (GitHub Actions, GameCI) | `.github/workflows/release.yml` |
| Build method (Unity, batch mode) | `Assets/Engine/Editor/Build/ReleaseBuild.cs` |
| Next version, release notes, packing | `Tools/Release/next-version.sh`, `compose-notes.sh`, `package.sh` |
| Local fallback (this PC) | `Tools/Release/release-local.sh` |

## Which number goes up

The owner picks the bump each time a release is started:

- **major**: a change that breaks what players already have (saved worlds or settings that no longer load, controls
  that work differently), or a milestone the owner names as one (2.0.0).
- **minor**: new things to play or see, such as new biomes, blocks, menus or gameplay features.
- **patch**: fixes, tuning and performance work, with nothing new to play.

When a release has both, the bigger one wins. The workflow takes the highest `vX.Y.Z` tag and raises the chosen
number. A lower number resets to 0, so `1.4.2` + minor gives `1.5.0`.

## One-time setup: Unity secrets (owner)

The build on GitHub uses the owner's Unity Personal licence. Only the owner adds these three secrets, and they never
go into a file, a commit or a chat.

1. Open the repository on GitHub: **Settings → Secrets and variables → Actions → New repository secret**.
2. Name `UNITY_LICENSE`. Open `C:\ProgramData\Unity\Unity_lic.ulf` in Notepad, select all, copy, paste it as the
   value and save. This file exists once Unity Hub has activated the Personal licence on this PC.
3. Name `UNITY_EMAIL`: the e-mail address of the Unity account.
4. Name `UNITY_PASSWORD`: the password of the Unity account.

When the three are there, a pull request that changes the release pipeline builds both players as a check. Without
them, that check is skipped with a warning, and a release run stops with an error. If Unity renews or replaces the
licence (for example after you sign in again in the Hub, or when activation fails in the build log), copy the new
`Unity_lic.ulf` into `UNITY_LICENSE` again. If the account has two-factor sign-in and activation fails, the
[GameCI activation page](https://game-ci.com/docs/github/activation) explains what to do.

## Making a release

Releases are made from `master` and published straight away as **Latest**.

### On GitHub (the normal way)

1. **Actions → Release → Run workflow** on branch `master`.
2. Pick **bump** (patch / minor / major).
3. **highlights** (optional): a short Markdown text shown above the PR list. The web form takes one line; several
   lines work through `gh` (see below).
4. **pr_list**: on by default. It appends GitHub's list of the pull requests merged since the last release.
5. **dry_run**: on means build and pack only, with no tag and no release. The archives stay downloadable from the run
   for 7 days.

From a terminal, with highlights drafted in a file:

```bash
gh workflow run release.yml --ref master -f bump=minor -f highlights="$(cat highlights.md)"
```

The run takes about 15–40 minutes: two build jobs run in parallel, and the first run has no Library cache yet. Then
the release `vX.Y.Z` "Infinitile X.Y.Z" appears with:

- `Infinitile-X.Y.Z-windows-x64.zip`: unpack it and start `Infinitile.exe`.
- `Infinitile-X.Y.Z-linux-x64.tar.gz`: `tar -xzf …`, then start `./Infinitile.x86_64`.

Each archive holds one folder `Infinitile-X.Y.Z-<platform>/`. The `*_DoNotShip` and `*_ButDontShipItWithYourGame`
folders are left out.

### Highlights (how an agent drafts them)

1. The agent lists what was merged since the last release:
   `gh pr list --state merged --base master --search "merged:>$(gh release view --json publishedAt --jq .publishedAt)"`.
2. It writes three to six player-facing bullets into a Markdown file in the session notes folder, not in the repo.
3. The owner reads them and gives the bump and the go for that exact version.
4. The agent runs the `gh workflow run` line above with that file.

A heading `## Highlights` is added unless the text starts with its own heading. For a big release such as `1.0.0`,
a hand-written summary as the highlights with `-f pr_list=false` can read better than the long PR list.

### On this PC (fallback)

Use this when GitHub Actions cannot build, for example when the licence is broken or the runners are down. Run it
in Git Bash, in a checkout of `master` that matches `origin/master`, with the Unity Editor **closed** on that
checkout:

```bash
Tools/Release/release-local.sh minor --highlights highlights.md
```

The script:

1. Computes the version the same way as the workflow.
2. Builds both players with the project's Unity version (6000.3.11f1 from the Hub folder; `UNITY_EXE` overrides it).
3. Puts back the project settings and render pipeline assets that the batch build re-saved.
4. Packs the players and shows the notes.
5. Asks before `gh release create` (`--yes` skips the question). Add `--dry-run` to build and pack without publishing.

Archives, notes and Unity logs land in `Builds/Release/<version>/`, which is gitignored. Expect the PC to be busy
for about 10–20 minutes, because each target switch re-imports.

## Version in the game

`PlayerSettings.bundleVersion` is `dev` in the repository. `ReleaseBuild` sets it to `X.Y.Z` for the build only, so a
release player reads its version as `Application.version`. The Editor and other builds (for example the goal-run
player) read `dev`. Showing the version in a menu is a separate task.

The product is called **Infinitile** (company `anilas5235`), so the player is `Infinitile.exe` / `Infinitile.x86_64`.
Its saved settings and logs live in `%USERPROFILE%\AppData\LocalLow\anilas5235\Infinitile\` on Windows and
`~/.config/unity3d/anilas5235/Infinitile/` on Linux. Settings saved under the old name (`DefaultCompany/voxel-game`)
are not carried over.

## When something goes wrong

- **Activation fails** in the Build step: the secrets are missing or the licence has changed. See the one-time setup
  above.
- **No space left on device**: the "Free disk space" step frees about 20 GB. If the project grows, set
  `large-packages: true` there, which takes a few minutes longer.
- **The build fails in Unity**: open the job's Build step. The Unity log is inline, and `[ReleaseBuild]` lines give
  the result. A local `--dry-run` with the same version reproduces it on this PC.
- **The tag exists already**: `next-version.sh` refuses to reuse a version. Pick the next bump, or delete the stray
  tag (owner only).
- **Workflow run without a release**: a pull-request run and `dry_run` never publish. Only a run of `master` with
  `dry_run` off does.
