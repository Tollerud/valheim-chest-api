# ChestAPI

A BepInEx plugin for the **Valheim dedicated server** that tracks every player-placed chest
(any piece with a `Container` component) and exposes it over a local JSON HTTP API, so other
services can answer questions like "do we have enough iron to build X?".

This is API-only: no UI, no in-game commands, just data.

> **AI-assisted development:** This mod was written with the help of Claude Code, an AI coding
> assistant. I review the changes and run it on my own Valheim dedicated server, but it has not been
> tested across many setups or Valheim versions. If something breaks, please report it with your
> Valheim version and the `ChestAPI` lines from `BepInEx/LogOutput.log`. See the
> [changelog](CHANGELOG.md) for what has changed.

## Installation

1. Install via [Thunderstore](https://thunderstore.io) (search "ChestAPI" under the Valheim
   community) using r2modman/the Thunderstore app on your dedicated server, or manually: drop
   `ChestAPI.dll` into `<server>/BepInEx/plugins/ChestAPI/`. Requires BepInEx (any mod manager
   install already includes it; manually, install the `denikson-BepInExPack_Valheim` pack first).
2. Set the API token in the environment the server process runs in, then (re)start the server:
   ```
   export CHESTAPI_API_TOKEN=$(openssl rand -hex 32)
   ```
   See "Configuring the token" below for details and alternatives.
3. Confirm it's up:
   ```
   curl -H "X-Api-Key: $CHESTAPI_API_TOKEN" http://127.0.0.1:8085/api/chests
   ```

## What it tracks, per chest

- `id` — stable Valheim ZDOID (e.g. `"1234567890:42"`)
- `prefabName` / `displayName` — e.g. `piece_chest_wood` / "Wood chest"
- `position` (x,y,z) and `rotationEuler`
- `biome` — best-effort (e.g. "Meadows")
- `privacy` — `"Public"` / `"Private"` / `"Unknown"` (best-effort)
- `owner` — placer's player id + name, if resolvable
- `loaded` — whether this came from a currently-active zone (fresh) vs. a periodic background scan
  of chests outside any loaded zone (see "How it works")
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

Set it via the `CHESTAPI_API_TOKEN` environment variable in the environment the dedicated server
process runs in (preferred — it never touches disk, so it won't end up in a config file or log):

```
export CHESTAPI_API_TOKEN=$(openssl rand -hex 32)
```

For a systemd-managed server, put it in the unit's `EnvironmentFile=` or an `Environment=` line so it's
set before the process starts. If you'd rather use the BepInEx config file instead
(`BepInEx/config/mathias.valheim.chestapi.cfg`, key `ApiKey` under `[Http]`), that works too, but the
env var always wins if both are set. Restart the server after changing either.

## How it works

The plugin tracks chests two ways. Live chests (anywhere a player currently is) update instantly
whenever their contents change, via game hooks — this is where `loaded: true` comes from. Chests far
from any player aren't normally kept in memory by the game at all, so a periodic background scan
(every 60s by default, configurable) reads them directly from the server's saved world data instead;
those show up as `loaded: false`. Either way, every chest the server knows about ends up in
`/api/chests` — `loaded` just tells you whether the data is live or came from that last scan.

Chest location, in other words, doesn't matter — only whether somebody has to be standing nearby for
the data to be fresh in real time.

For how this is implemented, build instructions, and known soft spots in the API detection, see
[DEVELOPMENT.md](DEVELOPMENT.md).
