# Changelog

## 0.2.0

- New `/api/playerchests` endpoints that only look at chests players built, leaving out dungeon and world
  chests such as `TreasureChest_*`: the chest list, per-item totals, a single item's count, and a
  `check` endpoint that compares what you need against what you have and lists what is missing (and, with
  `where=true`, which chests hold it).
- Choose which chests to include with `owner` (player name or id), a map rectangle (`minX`/`maxX`/`minZ`/`maxZ`),
  a circle (`x`/`z`/`radius`), height, `prefab`, `biome`, `item`, `loaded`, or a named `area`. The same
  parameters also work on the existing `/api/chests` and item-count endpoints.
- New `[Areas]` config for naming regions, plus `/api/areas` and `/api/players`.
- Chest owners now get a player name even when offline: names are remembered in a small file next to the
  config. Previously `PlayerName` was `null` unless the owner happened to be connected.
- Responses carry a `Meta` block saying how many chests are live versus from the periodic scan, and how old
  the stalest snapshot is.
- Unknown or malformed query parameters now return `400` instead of being ignored.
- `ChestDto` gained `PlayerPlaced`. Existing fields and endpoints are unchanged, and `/api/items/{prefab}/count`
  still works (it still counts every container; use `/api/playerchests/items/{prefab}/count` for player chests).

## 0.1.3

- Fixed every request through a reverse proxy or tunnel failing with `400 Bad Request (Invalid host)`.
  Binding `0.0.0.0` now listens on all hostnames instead of only requests addressed to `0.0.0.0`.

## 0.1.2

- Fixed the plugin failing to start on current Valheim dedicated servers: it tried to patch
  `Container.OnDestroy`, which no longer exists, and the resulting error stopped the HTTP API from
  ever starting. Chests that unload are now tracked without that patch.
- A hook that cannot be applied after a future Valheim update is now logged and skipped instead of
  stopping the whole plugin.

## 0.1.1

- Fixed the build failing on the `Localization` reference. Item display names are now looked up at
  runtime and fall back to the raw localization key if unavailable.
- Added the Thunderstore package icon.
- Known issue (fixed in 0.1.2): the plugin did not start on current Valheim servers.

## 0.1.0

- Initial release. This version was not published to Thunderstore because its build failed.
