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

All responses are JSON. Every request needs header `X-Api-Key: <value>` — **there is no open/unauthenticated
mode**: with no token configured, the API denies every request (fails closed) rather than defaulting to allow.

| Endpoint | Returns |
| --- | --- |
| `GET /health` | `{ "status": "ok", "utc": ... }` |
| `GET /api/playerchests` | chests **players built**, with contents |
| `GET /api/playerchests/items` | totals per item across those chests, most plentiful first |
| `GET /api/playerchests/items/{prefab}/count` | `{ TotalStack, ChestCount }` for one item |
| `GET /api/playerchests/check?need=Iron:20,Wood:50` | have / needed / missing per item (add `&where=true` for which chests hold it) |
| `GET /api/players` | players who own chests, with chest counts (for a "whose chests?" picker) |
| `GET /api/areas` | the named areas configured on the server |
| `GET /api/chests`, `/api/chests/items`, `/api/chests/items/{prefab}/count`, `/api/chests/check` | the same, over **every** container, including dungeon and world chests |
| `GET /api/chests/{id}` | one chest by ZDOID |
| `GET /api/items/{prefab}/count` | older alias of `/api/chests/items/{prefab}/count` |

A **player chest** is one that carries a creator id, meaning a player placed it. Dungeon and world chests
(`TreasureChest_*` and similar) have none, so they only show up under `/api/chests`. Ships and carts that hold
items also have a creator; leave them out with `excludePrefab=Karve,VikingShip,Cart`.

### Choosing which chests

These work on every list, totals, count and check endpoint. Combine them freely; all of them must match.
Names are case-insensitive, lists are comma-separated (or repeat the parameter), and an unknown parameter
is a `400` rather than being silently ignored.

| Parameter | Meaning |
| --- | --- |
| `owner=Mathias,297184578` | chests owned by these players, by name or player id |
| `minX`, `maxX`, `minZ`, `maxZ` | a rectangle on the map (the "grid"); leave a side out for no limit on it |
| `x`, `z`, `radius` | a circle: chests within `radius` metres of that point (all three together) |
| `minY`, `maxY` | height, e.g. `minY=1000` for underground dungeons |
| `area=base,farm` | named areas from the server config (see below); several are combined |
| `prefab=piece_chest_wood`, `excludePrefab=...` | only / not these chest types |
| `biome=Meadows,BlackForest` | only chests in these biomes |
| `item=Iron,Chain` | only chests holding one of these items (on `/items`, also limits which items are listed) |
| `loaded=true` | only chests in a loaded zone, i.e. with live contents |
| `includeItems=false` | omit the item lists from the chest list, for a much smaller response |

Map coordinates are Valheim world coordinates: `x` east-west, `z` north-south, `y` height.

Examples:

```
# How much iron do the player chests inside a 200 m circle around my base hold?
GET /api/playerchests/items/Iron/count?x=120&z=-2300&radius=200

# Ola's chests in a rectangle, without the item lists
GET /api/playerchests?owner=Ola&minX=0&maxX=500&minZ=-2500&maxZ=-2000&includeItems=false

# Can we build this? What is missing, and where is the rest?
GET /api/playerchests/check?need=Iron:150,Chain:20,Resin:300&area=base&where=true
```

Every totals, count, check and list response carries a `Meta` block: how many chests matched, how many are
live (`LiveChests`) versus only known from the periodic scan (`ScannedChests`), and `OldestUpdateUtc`, the
age of the stalest snapshot. A calculator can use it to say how fresh its numbers are.

### Player names

Valheim only stores a chest's creator as a number, and the server only knows the names of players who are
online. ChestAPI therefore remembers names: from players currently connected and from the crafter name
stamped on any item in any chest. They are kept in `BepInEx/config/mathias.valheim.chestapi.players.json` and
survive restarts. A player nobody has seen or whose crafted items are in no chest has no name yet; use their
player id until they next log in.

### Named areas

Define regions once on the server and let callers say `?area=base`. In the BepInEx config, `[Areas]`
`Definitions`, separate entries with `;`:

```
base:circle:120,-2300,200;farm:rect:-300,100,-200,200
```

`circle` is `x,z,radius`; `rect` is `minX,minZ,maxX,maxZ`. Bad entries are skipped and logged at startup.

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

## License

[MIT](LICENSE).
