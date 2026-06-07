# PREFLIGHT — session-start checklist

Work this list at the top of every working session, before making changes.

## 1. Workspace state

- [ ] `git status` — working tree clean? If not, understand what's pending
      before adding to it (finish/commit/stash deliberately, don't pile on).
- [ ] `git log --oneline -5` — re-orient on where the last session left off.
- [ ] Any `WIP`/`TODO` notes in the last commit message or open feature doc?

## 2. Boundaries

- [ ] Reference material (Beelzebub workspace, Learning Mods, Prefabs dump) is
      **read-only** from this session. Reading is encouraged; editing is not.
- [ ] Working on a feature? Open its `docs/features/*.md` design doc FIRST.
      If the session's work changes the design, update the doc in the same
      commit as the code.

## 3. Build & deploy safety

- [ ] Is the local V Rising dedicated server **running**? It file-locks
      `Uriel.dll` — stop it before any build that deploys.
- [ ] Compile-check only (no deploy):
      `dotnet build Uriel.sln -c Release -p:VRisingServerPath=C:\__nodeploy__`

## 4. Release intent

- [ ] If this session will end in a version bump: re-read CLAUDE.md →
      "Release & changelog discipline" (six surfaces move together) and run
      `tools/preflight.ps1` before the `chore(release)` commit.

## 5. Live-server data

- [ ] If a feature persists state (e.g. public-container registry under
      `BepInEx/config/Uriel/`), check whether a schema change needs a
      migration path for data already on the user's server. Never silently
      drop player state.
