# ValheimChestApi

A BepInEx plugin for the **Valheim dedicated server** that tracks every player-placed chest
(any piece with a `Container` component) and exposes it over a local JSON HTTP API, so other
services can answer questions like "do we have enough iron to build X?".

This is API-only: no UI, no in-game commands, just data.

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

**Important limitation:** the dedicated server only instantiates `Container` components for zones
that are currently loaded (near online players). Chests in far-away, never-visited-this-session
bases will still show up (with `loaded: false` and whatever was last observed), but if the server
has *never* loaded that zone since this plugin was installed, it won't know about that chest yet.
A more complete v2 would read directly from `ZDOMan`'s persisted world state (which exists for the
whole world regardless of zone loading) by replaying `Container`'s own inventory-save bytes through
the game's own `Inventory.Load(ZPackage)` — this is a known, solid technique, just deliberately left
out of this first pass to keep the scope to "get the API working."

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
4. `dotnet build` from `ValheimChestApi/`.
5. Copy the built `ValheimChestApi.dll` into `<server>/BepInEx/plugins/ValheimChestApi/`.
6. Set `VALHEIM_CHEST_API_TOKEN` in the environment the server process runs in (see above), then start it.
7. On the machine running it:
   ```
   curl -H "X-Api-Key: $VALHEIM_CHEST_API_TOKEN" http://127.0.0.1:8085/api/chests
   ```

Config (port, bind address, fallback API key) is written to
`BepInEx/config/mathias.valheim.chestapi.cfg` on first run — edit and restart to change.

## Suggested next steps (not built yet, by request — API first)

- Full-world coverage via direct `ZDOMan` + `Inventory.Load` replay for chests in unloaded zones.
- Push model (webhook on inventory change) instead of poll-only, for services that want to react live.
- Filtering query params on `/api/chests` (by biome, by prefab, by "contains item X").
