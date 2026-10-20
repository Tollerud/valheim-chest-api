using System;
using System.Collections.Generic;

namespace ChestAPI
{
    public class Vec3Dto
    {
        public float X;
        public float Y;
        public float Z;
    }

    public class OwnerDto
    {
        public long PlayerId;
        public string PlayerName;
    }

    public class ItemDto
    {
        /// <summary>Stable prefab name (e.g. "Wood", "BronzeNails"). Use this for matching against recipes.</summary>
        public string PrefabName;

        /// <summary>Raw localization key (e.g. "$item_wood").</summary>
        public string NameKey;

        /// <summary>Localized display name, best-effort (e.g. "Wood").</summary>
        public string DisplayName;

        public int Stack;
        public int MaxStack;
        public float Durability;
        public int Quality;
        public int Variant;
        public bool Equipped;

        public long CrafterId;
        public string CrafterName;

        public int GridX;
        public int GridY;
    }

    public class ChestDto
    {
        /// <summary>Valheim ZDOID as a string, e.g. "1234567890:42". Stable unique identifier for this chest.</summary>
        public string Id;

        /// <summary>Prefab name, e.g. "piece_chest_wood".</summary>
        public string PrefabName;

        /// <summary>Best-effort localized piece name, e.g. "Wood chest".</summary>
        public string DisplayName;

        public Vec3Dto Position;
        public Vec3Dto RotationEuler;

        /// <summary>Best-effort biome lookup (e.g. "Meadows"). Null if it couldn't be determined.</summary>
        public string Biome;

        /// <summary>"Public", "Private", or "Unknown" (detection is best-effort, see README).</summary>
        public string Privacy = "Unknown";

        /// <summary>Best-effort placer/owner. Null if not determinable.</summary>
        public OwnerDto Owner;

        /// <summary>True if this chest is in a currently-loaded zone (data is live). False means data came from
        /// a background scan of persisted world state and may be slightly stale.</summary>
        public bool Loaded;

        public DateTime LastUpdatedUtc;

        public int Width;
        public int Height;
        public int UsedSlots;

        public List<ItemDto> Items = new List<ItemDto>();
    }

    public class ChestListResponse
    {
        public int Count;
        public DateTime GeneratedAtUtc;
        public List<ChestDto> Chests;
    }

    public class ItemCountResponse
    {
        public string PrefabName;
        public int TotalStack;
        public int ChestCount;
        public DateTime GeneratedAtUtc;
    }

    public class ErrorResponse
    {
        public string Error;
    }
}
