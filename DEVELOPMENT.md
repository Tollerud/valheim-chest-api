# Developing ChestAPI

This file is for building, maintaining, and releasing the mod itself. If you just want to run it
on your server, see `README.md`.

## Building from source

1. Install a Valheim **dedicated server** somewhere (SteamCMD app id `896660`), e.g. `~/valheim-server`.
2. Point the build at it, either once per shell:
   ```
   export VALHEIM_INSTALL=/path/to/valheim-server
   ```
   or pass it inline:
   ```
   dotnet build -p:VALHEIM_INSTALL=/path/to/valheim-server
   ```
3. That install must already have BepInEx 5.4.x installed on it (gives us `BepInEx.dll` and
   `0Harmony.dll` under `BepInEx/core/`).
4. `dotnet build` from `ChestAPI/`.
5. Copy the built `ChestAPI.dll` into `<server>/BepInEx/plugins/ChestAPI/`.

## Known risk areas (please verify against your Valheim version)

I wrote this against commonly-known Valheim/BepInEx modding conventions, but didn't have the game
assemblies available to compile-check it, so some of this was written against a guessed API surface.
One already turned out wrong in CI — see the first bullet below. The rest haven't broken a build (they
degrade to `"Unknown"`/`null` at runtime instead, by design), but might still be wrong:

- **Localization** (`ReflectionHelpers.TryLocalize`) — the first tag push (`v0.1.0`) failed to build
  with `CS0103: The name 'Localization' does not exist in the current context`, meaning that type
  either isn't named that, isn't public, or isn't in an assembly we reference. Fixed by resolving it
  reflectively by type name across loaded assemblies instead of referencing it directly - if the type
  truly doesn't exist under this name anymore, display names just fall back to the raw localization
  key (e.g. `"$item_wood"` instead of `"Wood"`) rather than breaking the build.
- **Chest privacy** (`ChestRegistry.TryGetPrivacy`) — scans `Container` for any field/property whose
  name contains "privacy" or "private". If your version names it differently, open the plugin in
  dnSpy/ILSpy against your `assembly_valheim.dll`, find the real field, and either rename it directly
  in `BuildDto` or adjust the search terms.
- **Owner name resolution** (`ChestRegistry.TryResolvePlayerName`) — only resolves names for
  *currently connected* peers (via `ZNet.m_peers`), using guessed field names (`m_rpc`, `m_playerName`,
  `m_uid`). Owner *id* (via `Piece.GetCreator()`) should be solid; the name lookup is the soft part.
  If it never resolves anything, inspect `ZNet.m_peers`'s actual element type and fix the field names.
- **Persisted-world scan owner id** (`ChestRegistry.TryGetOwnerFromZdo`) — reads the creator player id
  straight off the ZDO via `zdo.GetLong("creator", 0L)`. `"creator"` is a guessed key name (conventional
  in community modding references for piece ownership); if it never resolves, inspect how `Piece`'s own
  `GetCreator()` reads its ZDO and match that key.
- **Persisted-world scan privacy** — always comes back `"Unknown"` for chests only ever seen via the
  scan (not yet loaded this session). We don't know which ZDO custom-data key the privacy flag is
  stored under, unlike items/creator; loaded chests still get a real value via `TryGetPrivacy`.
- **Persisted-world scan inventory size** (`ChestRegistry.BuildDtoFromZdo`) — reads `m_width`/`m_height`
  off the prefab's template `Container` component via reflection; if those field names are wrong or the
  template hasn't initialized them, it falls back to a generic 8x4 grid. This only affects reported
  `width`/`height` and slot-bounds, not which items are decoded.

If the project fails to build with "member not found" on a Harmony `[HarmonyPatch]` target
(`ZNetScene.Awake` / `Container.Awake` / `Container.Save`; there is deliberately no `Container.OnDestroy` patch, since Valheim's `Container` declares none - unloads are tracked by `ContainerUnloadTracker`), check those exact method names exist
on your installed version the same way.

## Publishing to Thunderstore

Tagging a release (`vX.Y.Z`) builds the plugin and publishes it to
[thunderstore.io](https://thunderstore.io) automatically via `.github/workflows/thunderstore-publish.yml`
and `thunderstore.toml`. Nothing publishes on ordinary commits or PRs — pushing a version tag is the
deliberate "ship it" step, same as it would be for a manual upload.

One-time setup (already done for this repo, kept here for reference/future maintainers):

1. **Create the Thunderstore team** matching the `namespace` in `thunderstore.toml` (`Tollerud`) at
   thunderstore.io's team settings.
2. **Generate an API token**: on thunderstore.io, team → Settings → Service Accounts (or your own
   account settings, depending on how Thunderstore has it organized at the time), scoped to this
   team/package.
3. **Add it as a GitHub secret**: repo Settings → Secrets and variables → Actions → New repository
   secret, name `TCLI_AUTH_TOKEN`.
4. **Add `icon.png`** at the repo root — a 256x256 PNG, required by Thunderstore.
5. Double check the `denikson-BepInExPack_Valheim` version pinned in both `thunderstore.toml`
   (`[package.dependencies]`) and `BEPINEX_VERSION` in the workflow file still matches a real,
   current version on Thunderstore (versions do get superseded).

To release: bump `PluginVersion` in `ChestAPI/Plugin.cs` and `versionNumber` in `thunderstore.toml`
to match, commit, then:

```
git tag v0.2.0
git push origin v0.2.0
```

The workflow downloads the Valheim dedicated server and the pinned BepInExPack anonymously via
SteamCMD/Thunderstore (build-time assembly references only, nothing redistributed), builds the
plugin, and runs `tcli publish`.

## Suggested next steps (not built yet, by request — API first)

- Push model (webhook on inventory change) instead of poll-only, for services that want to react live.
- Filtering query params on `/api/chests` (by biome, by prefab, by "contains item X").
