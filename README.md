# ChestAPI

A BepInEx plugin for the **Valheim dedicated server** that tracks every player-placed chest
(any piece with a `Container` component) and exposes it over a local JSON HTTP API, so other
services can answer questions like "do we have enough iron to build X?".

This is API-only: no UI, no in-game commands, just data.

> **Disclaimer:** I'm not experienced with C# development and used Claude Code (an AI coding
> assistant) to help build this mod. All features are tested before release.

## What it tracks, per chest

- `id` — stable Valheim ZDOID (e.g. `"1234567890:42"`)
- `prefabName` / `displayName` — e.g. `piece_chest_wood` / "Wood chest"
- `position` (x,y,z) and `rotationEuler`
- `biome` — best-effort (e.g. "Meadows")
- `privacy` — `"Public"` / `"Private"` / `"Unknown"` (best-effort, see below)
- `owner` — placer's player id + name, if resolvable
- `loaded` — whether this came from a currently-active zone (fresh) vs. a stale last-known snapshot
- `width` / `height` / `usedSlots`
- `items[]` — each with `prefabName` (use this for recipe matching), localized `displayName`,
  `stack`, `maxStack`, `durability`, `quality`, `variant`, `crafterId`/`crafterName`, grid position

## Endpoints

- `GET /health` — `{ "status": "ok", "utc": ... }`
- `GET /api/chests` — all known chests
- `GET /api/chests/{id}` — one chest by ZDOID
- `GET /api/items/{prefabName}/count` — `{ totalStack, chestCount }` summed across all chests

All responses are JSON. Every request needs header `X-Api-Key: <value>` — **there is no open/unauthenticated
mode**: with no token configured, the API denies every request (fails closed) rather than defaulting to allow.

### Configuring the token

Set it via the `VALHEIM_CHEST_API_TOKEN` environment variable in the environment the dedicated server
process runs in (preferred — it never touches disk, so it won't end up in a config file or log):

```
export VALHEIM_CHEST_API_TOKEN=$(openssl rand -hex 32)
```

For a systemd-managed server, put it in the unit's `EnvironmentFile=` or an `Environment=` line so it's
set before the process starts. If you'd rather use the BepInEx config file instead
(`BepInEx/config/mathias.valheim.chestapi.cfg`, key `ApiKey` under `[Http]`), that works too, but the
env var always wins if both are set. Restart the server after changing either.

## How it works

Harmony patches hook `Container.Awake` (zone loads a chest), `Container.Save` (fires whenever
the chest's inventory changes, and periodically — this is how the snapshot stays fresh without
polling), and `Container.OnDestroy` (zone unloads it, so we mark the last-known data `loaded: false`
instead of dropping it). The HTTP server just reads from an in-memory, thread-safe cache built by
those patches — no game-thread work happens per request.

The dedicated server only instantiates `Container` components for zones that are currently loaded
(near online players), so Harmony hooks alone would miss chests in far-away, nobody's-visited-it-
this-session bases. To cover those too, `ChestRegistry.ScanPersistedWorld()` runs on a timer
(`ScanIntervalSeconds` config, default 60s, see below) and sweeps `ZDOMan`'s full persisted world
state directly — unlike loaded `Container`s, every placed chest's ZDO exists there regardless of
whether its zone is currently loaded. For each container-type ZDO it decodes the stored item bytes
by replaying them through the game's own `Inventory.Load(ZPackage)` rather than re-implementing that
binary format, so it stays correct across versions as long as that method itself exists. Live
`Container` data always wins over a scan result for the same chest (`loaded: true` entries are never
overwritten by the scan), so this only fills in/refreshes chests the Harmony hooks can't currently see.

This runs on the Unity main thread deliberately — `ZDOMan`'s backing dictionary isn't safe to read
from a background thread while the game simultaneously mutates it, so a scan tick does cost a slice
of a server frame. Set `ScanIntervalSeconds` higher (or `ScanUnloadedContainers = false`) on a large
world if that becomes noticeable. It can be disabled entirely via config; Harmony-hook tracking of
loaded containers works independently either way.

## Known risk areas (please verify against your Valheim version)

I wrote this against commonly-known Valheim/BepInEx modding conventions, but didn't have the game
assemblies available to compile-check it. Two spots are reflection-based specifically because I
wasn't fully certain of the exact member name, and will degrade to `"Unknown"`/`null` rather than
crash if the lookup fails:

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
(`Container.Awake` / `Container.Save` / `Container.OnDestroy`), check those exact method names exist
on your installed version the same way.

## Building

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
6. Set `VALHEIM_CHEST_API_TOKEN` in the environment the server process runs in (see above), then start it.
7. On the machine running it:
   ```
   curl -H "X-Api-Key: $VALHEIM_CHEST_API_TOKEN" http://127.0.0.1:8085/api/chests
   ```

Config (port, bind address, fallback API key) is written to
`BepInEx/config/mathias.valheim.chestapi.cfg` on first run — edit and restart to change.

## Publishing to Thunderstore

Tagging a release (`vX.Y.Z`) builds the plugin and publishes it to
[thunderstore.io](https://thunderstore.io) automatically via `.github/workflows/thunderstore-publish.yml`
and `thunderstore.toml`. Nothing publishes on ordinary commits or PRs — pushing a version tag is the
deliberate "ship it" step, same as it would be for a manual upload.

One-time setup before the first release:

1. **Create the Thunderstore team** (if you haven't) matching the `namespace` in `thunderstore.toml`
   (currently `Tollerud`) at thunderstore.io's team settings.
2. **Generate an API token**: on thunderstore.io, go to your team → Settings → Service Accounts (or
   your own account settings, depending on how Thunderstore has it organized at the time), create a
   token scoped to this team/package.
3. **Add it as a GitHub secret**: repo Settings → Secrets and variables → Actions → New repository
   secret, name `TCLI_AUTH_TOKEN`, value the token from step 2.
4. **Add `icon.png`** at the repo root — a 256x256 PNG, required by Thunderstore. Not included here;
   drop your own in before the first real release.
5. Double check the `denikson-BepInExPack_Valheim` version pinned in both `thunderstore.toml`
   (`[package.dependencies]`) and `BEPINEX_VERSION` in the workflow file still matches a real,
   current version on Thunderstore (versions do get superseded).

To release: bump `PluginVersion` in `ChestAPI/Plugin.cs` to match, commit, then:

```
git tag v0.2.0
git push origin v0.2.0
```

The workflow downloads the Valheim dedicated server and the pinned BepInExPack anonymously via
SteamCMD/Thunderstore (build-time assembly references only, nothing redistributed), builds the
plugin, and runs `tcli publish`.

**This pipeline is unverified** — written from current Thunderstore CLI docs and a working reference
workflow, but never actually run (no GitHub Actions access from here, and this project's build can't
be compile-checked in this sandbox either — see "Known risk areas" above). Treat the first tag push
as a test: watch the Actions run, and if `tcli publish` rejects something (category slugs and the
BepInEx dependency version are the most likely culprits — see comments in `thunderstore.toml`), fix
and re-tag.

## Suggested next steps (not built yet, by request — API first)

- Push model (webhook on inventory change) instead of poll-only, for services that want to react live.
- Filtering query params on `/api/chests` (by biome, by prefab, by "contains item X").
