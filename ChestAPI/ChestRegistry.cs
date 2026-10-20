using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChestAPI
{
    /// <summary>
    /// Thread-safe in-memory snapshot of every chest/container we've observed. Populated by Harmony
    /// patches on Container (see Patches.cs) as the server loads/saves/unloads zones, so reads from the
    /// HTTP layer never touch the Unity main thread.
    /// </summary>
    internal class ChestRegistry
    {
        public static readonly ChestRegistry Instance = new ChestRegistry();

        private readonly ConcurrentDictionary<string, ChestDto> _chests = new ConcurrentDictionary<string, ChestDto>();

        /// <summary>Prefab name hash -> prefab template GameObject, for every prefab in the scene that has a
        /// Container component. Used by the persisted-world scan to recognize container ZDOs for chests that
        /// aren't currently loaded, and to read their template inventory size (width/height).</summary>
        private readonly ConcurrentDictionary<int, GameObject> _containerPrefabs = new ConcurrentDictionary<int, GameObject>();

        public void BuildContainerPrefabIndex(ZNetScene scene)
        {
            try
            {
                var prefabs = ReflectionHelpers.GetField<List<GameObject>>(scene, "m_prefabs");
                if (prefabs == null) return;

                foreach (var prefab in prefabs)
                {
                    if (prefab == null) continue;
                    if (prefab.GetComponent<Container>() == null) continue;

                    var hash = prefab.name.GetStableHashCode();
                    _containerPrefabs[hash] = prefab;
                }

                Plugin.Log?.LogInfo($"[ChestAPI] Indexed {_containerPrefabs.Count} container prefab(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Failed to build container prefab index: {ex.Message}");
            }
        }

        /// <summary>
        /// Sweeps ZDOMan's full persisted world state (which exists for every chest the server knows about,
        /// loaded or not) and fills in/refreshes entries for chests that aren't currently in a loaded zone.
        /// Must be called on the Unity main thread - it's the same thread that mutates ZDOMan, and that
        /// dictionary isn't safe to enumerate concurrently from a background thread.
        /// </summary>
        public void ScanPersistedWorld()
        {
            try
            {
                if (ZDOMan.instance == null || _containerPrefabs.IsEmpty) return;

                var allZdos = ReflectionHelpers.GetField<System.Collections.IDictionary>(ZDOMan.instance, "m_objectsByID");
                if (allZdos == null) return;

                // Snapshot the values first - we're on the main thread so there's no real race, this just
                // avoids "collection was modified" if anything we call re-enters ZDOMan.
                var zdos = allZdos.Values.OfType<ZDO>().ToList();

                int matched = 0, refreshed = 0;
                foreach (var zdo in zdos)
                {
                    if (!_containerPrefabs.TryGetValue(zdo.GetPrefab(), out var prefab)) continue;
                    matched++;

                    var id = zdo.m_uid.ToString();
                    if (_chests.TryGetValue(id, out var existing) && existing.Loaded)
                    {
                        continue; // live Container data is fresher than anything we'd decode from the ZDO
                    }

                    var dto = BuildDtoFromZdo(zdo, prefab);
                    if (dto != null)
                    {
                        _chests[id] = dto;
                        refreshed++;
                    }
                }

                Plugin.Log?.LogInfo($"[ChestAPI] Persisted-world scan: {matched} container ZDO(s) found, {refreshed} unloaded snapshot(s) refreshed.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Persisted-world scan failed: {ex.Message}");
            }
        }

        public void OnContainerLoaded(Container container)
        {
            try
            {
                var dto = BuildDto(container);
                if (dto != null)
                {
                    _chests[dto.Id] = dto;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Failed to snapshot container: {ex.Message}");
            }
        }

        public void OnContainerUnloaded(Container container)
        {
            try
            {
                var nview = container.GetComponent<ZNetView>();
                var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo == null) return;

                var id = zdo.m_uid.ToString();
                if (_chests.TryGetValue(id, out var existing))
                {
                    existing.Loaded = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Failed to mark container unloaded: {ex.Message}");
            }
        }

        public List<ChestDto> GetAll() => _chests.Values.ToList();

        public ChestDto GetById(string id) => _chests.TryGetValue(id, out var dto) ? dto : null;

        private ChestDto BuildDto(Container container)
        {
            var nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return null;

            var zdo = nview.GetZDO();
            var inventory = container.GetInventory();
            if (inventory == null) return null;

            var dto = new ChestDto
            {
                Id = zdo.m_uid.ToString(),
                PrefabName = nview.gameObject.name.Replace("(Clone)", string.Empty),
                Loaded = true,
                LastUpdatedUtc = DateTime.UtcNow,
                Width = inventory.GetWidth(),
                Height = inventory.GetHeight(),
            };

            var pos = zdo.GetPosition();
            dto.Position = new Vec3Dto { X = pos.x, Y = pos.y, Z = pos.z };

            var rot = zdo.GetRotation().eulerAngles;
            dto.RotationEuler = new Vec3Dto { X = rot.x, Y = rot.y, Z = rot.z };

            dto.Biome = TryGetBiome(pos);
            dto.DisplayName = TryGetDisplayName(container);
            dto.Privacy = TryGetPrivacy(container);
            dto.Owner = TryGetOwner(container);

            var items = inventory.GetAllItems();
            dto.UsedSlots = items.Count;
            foreach (var item in items)
            {
                dto.Items.Add(BuildItemDto(item));
            }

            return dto;
        }

        /// <summary>
        /// Builds a chest snapshot straight from persisted world state, without an instantiated Container.
        /// Reuses the game's own Inventory.Load(ZPackage) to decode the stored item bytes instead of
        /// re-implementing that binary format, so it stays correct across versions as long as the method
        /// itself exists.
        /// </summary>
        private ChestDto BuildDtoFromZdo(ZDO zdo, GameObject prefab)
        {
            try
            {
                var templateContainer = prefab.GetComponent<Container>();
                var width = ReflectionHelpers.GetField(templateContainer, "m_width", 0);
                var height = ReflectionHelpers.GetField(templateContainer, "m_height", 0);
                if (width <= 0) width = 8;
                if (height <= 0) height = 4;

                var inventory = new Inventory(prefab.name, null, width, height);
                var itemBytes = zdo.GetByteArray("items");
                if (itemBytes != null && itemBytes.Length > 0)
                {
                    inventory.Load(new ZPackage(itemBytes));
                }

                var dto = new ChestDto
                {
                    Id = zdo.m_uid.ToString(),
                    PrefabName = prefab.name,
                    Loaded = false,
                    LastUpdatedUtc = DateTime.UtcNow,
                    Width = inventory.GetWidth(),
                    Height = inventory.GetHeight(),
                };

                var pos = zdo.GetPosition();
                dto.Position = new Vec3Dto { X = pos.x, Y = pos.y, Z = pos.z };

                var rot = zdo.GetRotation().eulerAngles;
                dto.RotationEuler = new Vec3Dto { X = rot.x, Y = rot.y, Z = rot.z };

                dto.Biome = TryGetBiome(pos);
                dto.DisplayName = TryGetDisplayNameFromPrefab(prefab);
                // Privacy is read off the live Container component elsewhere (see TryGetPrivacy) and we
                // don't know which ZDO custom-data key it's persisted under, so this stays "Unknown" for
                // chests we've only ever seen via this scan - see README "Known risk areas".
                dto.Privacy = "Unknown";
                dto.Owner = TryGetOwnerFromZdo(zdo);

                var items = inventory.GetAllItems();
                dto.UsedSlots = items.Count;
                foreach (var item in items)
                {
                    dto.Items.Add(BuildItemDto(item));
                }

                return dto;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ChestAPI] Failed to decode persisted container {zdo.m_uid}: {ex.Message}");
                return null;
            }
        }

        private string TryGetDisplayNameFromPrefab(GameObject prefab)
        {
            try
            {
                var piece = prefab.GetComponent<Piece>();
                var nameKey = ReflectionHelpers.GetField<string>(piece, "m_name");
                if (string.IsNullOrEmpty(nameKey)) return null;
                return ReflectionHelpers.TryLocalize(nameKey);
            }
            catch
            {
                return null;
            }
        }

        private OwnerDto TryGetOwnerFromZdo(ZDO zdo)
        {
            try
            {
                // "creator" matches the key Piece ownership is conventionally persisted under - best-effort,
                // see README "Known risk areas". Falls back to no owner rather than a wrong one.
                var creatorId = zdo.GetLong("creator", 0L);
                if (creatorId == 0L) return null;

                return new OwnerDto
                {
                    PlayerId = creatorId,
                    PlayerName = TryResolvePlayerName(creatorId),
                };
            }
            catch
            {
                return null;
            }
        }

        private ItemDto BuildItemDto(ItemDrop.ItemData item)
        {
            var shared = item.m_shared;
            var gridPos = item.m_gridPos;

            string displayName = null;
            try
            {
                displayName = ReflectionHelpers.TryLocalize(shared.m_name);
            }
            catch
            {
                displayName = shared.m_name;
            }

            return new ItemDto
            {
                PrefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : shared.m_name,
                NameKey = shared.m_name,
                DisplayName = displayName,
                Stack = item.m_stack,
                MaxStack = shared.m_maxStackSize,
                Durability = item.m_durability,
                Quality = item.m_quality,
                Variant = item.m_variant,
                Equipped = item.m_equipped,
                CrafterId = item.m_crafterID,
                CrafterName = item.m_crafterName,
                GridX = gridPos.x,
                GridY = gridPos.y,
            };
        }

        private string TryGetDisplayName(Container container)
        {
            try
            {
                var piece = container.GetComponent<Piece>();
                var nameKey = ReflectionHelpers.GetField<string>(piece, "m_name");
                if (string.IsNullOrEmpty(nameKey)) return null;
                return ReflectionHelpers.TryLocalize(nameKey);
            }
            catch
            {
                return null;
            }
        }

        private string TryGetPrivacy(Container container)
        {
            // Private-chest support/the exact member name has moved around across Valheim updates.
            // Best-effort: look for anything on Container whose name suggests a privacy flag/enum.
            var value = ReflectionHelpers.FindFirstMemberValueContaining(container, "privacy", "private");
            if (value == null) return "Unknown";

            if (bool.TryParse(value, out var boolValue))
            {
                return boolValue ? "Private" : "Public";
            }

            return value; // likely an enum ToString() like "Private" / "Public"
        }

        private OwnerDto TryGetOwner(Container container)
        {
            try
            {
                var piece = container.GetComponent<Piece>();
                if (piece == null) return null;

                var creatorId = (long)(ReflectionHelpers.CallMethod(piece, "GetCreator") ?? 0L);
                if (creatorId == 0L) return null;

                return new OwnerDto
                {
                    PlayerId = creatorId,
                    PlayerName = TryResolvePlayerName(creatorId),
                };
            }
            catch
            {
                return null;
            }
        }

        private string TryResolvePlayerName(long playerId)
        {
            try
            {
                if (ZNet.instance == null) return null;
                var peers = ReflectionHelpers.GetField<System.Collections.IList>(ZNet.instance, "m_peers");
                if (peers == null) return null;

                foreach (var peer in peers)
                {
                    var rpc = ReflectionHelpers.GetProperty<object>(peer, "m_rpc") ?? ReflectionHelpers.GetField<object>(peer, "m_rpc");
                    var playerName = ReflectionHelpers.GetField<string>(peer, "m_playerName");
                    var pid = ReflectionHelpers.GetField<long>(peer, "m_uid");
                    if (pid == playerId && !string.IsNullOrEmpty(playerName))
                    {
                        return playerName;
                    }
                }
            }
            catch
            {
                // best effort only
            }
            return null;
        }

        private string TryGetBiome(Vector3 pos)
        {
            try
            {
                if (WorldGenerator.instance == null) return null;
                var biome = WorldGenerator.instance.GetBiome(pos);
                return biome.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
