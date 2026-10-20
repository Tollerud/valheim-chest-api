# Changelog

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
