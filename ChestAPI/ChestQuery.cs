using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

namespace ChestAPI
{
    // Everything in this file is plain C# with no Unity or BepInEx types, so it can be unit-tested without a
    // Valheim install. HttpApiServer turns the query string into a ChestFilter and calls into here.

    /// <summary>A named region on the map, in world X/Z (Y is height and is filtered separately).</summary>
    public sealed class Area
    {
        public string Name;

        /// <summary>"rect" or "circle".</summary>
        public string Kind;

        public double MinX, MinZ, MaxX, MaxZ;
        public double X, Z, Radius;

        public bool Contains(double x, double z)
        {
            if (Kind == "circle")
            {
                var dx = x - X;
                var dz = z - Z;
                return dx * dx + dz * dz <= Radius * Radius;
            }

            return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
        }
    }

    public static class AreaParser
    {
        /// <summary>
        /// Parses <c>name:rect:minX,minZ,maxX,maxZ</c> and <c>name:circle:x,z,radius</c> entries separated by
        /// ';' or newlines. Bad entries are skipped and described in <paramref name="problems"/>.
        /// </summary>
        public static Dictionary<string, Area> Parse(string text, out List<string> problems)
        {
            problems = new List<string>();
            var result = new Dictionary<string, Area>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var raw in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;

                var parts = entry.Split(new[] { ':' }, 3);
                if (parts.Length != 3 || parts[0].Trim().Length == 0)
                {
                    problems.Add($"'{entry}': expected name:rect:minX,minZ,maxX,maxZ or name:circle:x,z,radius");
                    continue;
                }

                var name = parts[0].Trim();
                var kind = parts[1].Trim().ToLowerInvariant();
                var nums = new List<double>();
                var numbersOk = true;
                foreach (var n in parts[2].Split(','))
                {
                    if (double.TryParse(n.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) nums.Add(v);
                    else numbersOk = false;
                }

                if (!numbersOk)
                {
                    problems.Add($"'{entry}': coordinates must be numbers");
                    continue;
                }

                if (kind == "rect" && nums.Count == 4)
                {
                    result[name] = new Area
                    {
                        Name = name,
                        Kind = "rect",
                        MinX = Math.Min(nums[0], nums[2]),
                        MaxX = Math.Max(nums[0], nums[2]),
                        MinZ = Math.Min(nums[1], nums[3]),
                        MaxZ = Math.Max(nums[1], nums[3]),
                    };
                }
                else if (kind == "circle" && nums.Count == 3 && nums[2] > 0)
                {
                    result[name] = new Area { Name = name, Kind = "circle", X = nums[0], Z = nums[1], Radius = nums[2] };
                }
                else
                {
                    problems.Add($"'{entry}': expected rect with 4 numbers or circle with 3 numbers and a positive radius");
                }
            }

            return result;
        }
    }

    /// <summary>Which chests a request is about. Built from the query string; see README for the parameters.</summary>
    public sealed class ChestFilter
    {
        private static readonly string[] CommonParams =
        {
            "owner", "prefab", "excludePrefab", "biome", "area", "minX", "maxX", "minZ", "maxZ", "minY", "maxY",
            "x", "z", "radius", "loaded", "item", "includeItems",
        };

        public bool PlayerOnly;
        public HashSet<long> OwnerIds;
        public HashSet<string> Prefabs;
        public HashSet<string> ExcludePrefabs;
        public HashSet<string> Biomes;
        public HashSet<string> Items;
        public List<Area> NamedAreas;
        public Area Circle;
        public double? MinX, MaxX, MinZ, MaxZ, MinY, MaxY;
        public bool? Loaded;
        public bool IncludeItems = true;

        /// <summary>
        /// Returns null and sets <paramref name="error"/> when the request is invalid. Unknown parameters are an
        /// error on purpose: a misspelled "minx" silently returning every chest is worse than a 400.
        /// </summary>
        public static ChestFilter Parse(
            NameValueCollection query,
            bool playerOnly,
            IDictionary<string, Area> areas,
            PlayerNames names,
            string[] extraParams,
            out string error)
        {
            error = null;
            var allowed = new HashSet<string>(CommonParams.Concat(extraParams ?? new string[0]), StringComparer.OrdinalIgnoreCase);
            foreach (var key in query.AllKeys)
            {
                if (key != null && !allowed.Contains(key))
                {
                    error = $"Unknown parameter '{key}'.";
                    return null;
                }
            }

            var f = new ChestFilter { PlayerOnly = playerOnly };

            f.Prefabs = List(query, "prefab");
            f.ExcludePrefabs = List(query, "excludePrefab");
            f.Biomes = List(query, "biome");
            f.Items = List(query, "item");

            var owners = List(query, "owner");
            if (owners != null)
            {
                f.OwnerIds = new HashSet<long>();
                foreach (var o in owners)
                {
                    if (long.TryParse(o, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        f.OwnerIds.Add(id);
                        continue;
                    }

                    var ids = names == null ? new List<long>() : names.FindIds(o);
                    if (ids.Count == 0)
                    {
                        error = $"Unknown player '{o}'. Use a player id, or see /api/players for known names.";
                        return null;
                    }

                    foreach (var id2 in ids) f.OwnerIds.Add(id2);
                }
            }

            var areaNames = List(query, "area");
            if (areaNames != null)
            {
                f.NamedAreas = new List<Area>();
                foreach (var a in areaNames)
                {
                    if (areas == null || !areas.TryGetValue(a, out var area))
                    {
                        error = $"Unknown area '{a}'. See /api/areas.";
                        return null;
                    }

                    f.NamedAreas.Add(area);
                }
            }

            if (!Number(query, "minX", out f.MinX, ref error) || !Number(query, "maxX", out f.MaxX, ref error)
                || !Number(query, "minZ", out f.MinZ, ref error) || !Number(query, "maxZ", out f.MaxZ, ref error)
                || !Number(query, "minY", out f.MinY, ref error) || !Number(query, "maxY", out f.MaxY, ref error))
            {
                return null;
            }

            if (!Number(query, "x", out var cx, ref error) || !Number(query, "z", out var cz, ref error)
                || !Number(query, "radius", out var radius, ref error))
            {
                return null;
            }

            var circleParts = (cx.HasValue ? 1 : 0) + (cz.HasValue ? 1 : 0) + (radius.HasValue ? 1 : 0);
            if (circleParts == 3)
            {
                if (radius.Value <= 0)
                {
                    error = "radius must be greater than 0.";
                    return null;
                }

                f.Circle = new Area { Kind = "circle", X = cx.Value, Z = cz.Value, Radius = radius.Value };
            }
            else if (circleParts != 0)
            {
                error = "x, z and radius must be given together.";
                return null;
            }

            if (f.MinX > f.MaxX || f.MinZ > f.MaxZ || f.MinY > f.MaxY)
            {
                error = "A min value is greater than its max.";
                return null;
            }

            if (!Bool(query, "loaded", out f.Loaded, ref error)) return null;

            if (!Bool(query, "includeItems", out var includeItems, ref error)) return null;
            if (includeItems.HasValue) f.IncludeItems = includeItems.Value;

            return f;
        }

        public bool Matches(ChestDto c)
        {
            if (PlayerOnly && !c.PlayerPlaced) return false;
            if (OwnerIds != null && (c.Owner == null || !OwnerIds.Contains(c.Owner.PlayerId))) return false;
            if (Prefabs != null && !Prefabs.Contains(c.PrefabName ?? string.Empty)) return false;
            if (ExcludePrefabs != null && ExcludePrefabs.Contains(c.PrefabName ?? string.Empty)) return false;
            if (Biomes != null && !Biomes.Contains(c.Biome ?? string.Empty)) return false;
            if (Loaded.HasValue && c.Loaded != Loaded.Value) return false;

            if (Items != null && !c.Items.Any(i => Items.Contains(i.PrefabName ?? string.Empty))) return false;

            var pos = c.Position;
            if (pos == null)
            {
                // Without a position it can only match when nothing positional was asked for.
                return NamedAreas == null && Circle == null && !MinX.HasValue && !MaxX.HasValue
                    && !MinZ.HasValue && !MaxZ.HasValue && !MinY.HasValue && !MaxY.HasValue;
            }

            if (MinX.HasValue && pos.X < MinX.Value) return false;
            if (MaxX.HasValue && pos.X > MaxX.Value) return false;
            if (MinZ.HasValue && pos.Z < MinZ.Value) return false;
            if (MaxZ.HasValue && pos.Z > MaxZ.Value) return false;
            if (MinY.HasValue && pos.Y < MinY.Value) return false;
            if (MaxY.HasValue && pos.Y > MaxY.Value) return false;
            if (Circle != null && !Circle.Contains(pos.X, pos.Z)) return false;
            if (NamedAreas != null && !NamedAreas.Any(a => a.Contains(pos.X, pos.Z))) return false;

            return true;
        }

        public List<ChestDto> Apply(IEnumerable<ChestDto> chests) => chests.Where(Matches).ToList();

        // Case-insensitive set from a comma-separated value, or from repeated parameters (?item=a&item=b).
        private static HashSet<string> List(NameValueCollection q, string key)
        {
            var values = q.GetValues(key);
            if (values == null) return null;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in values)
            {
                foreach (var part in v.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length > 0) set.Add(t);
                }
            }

            return set.Count == 0 ? null : set;
        }

        private static bool Number(NameValueCollection q, string key, out double? value, ref string error)
        {
            value = null;
            var raw = q[key];
            if (string.IsNullOrWhiteSpace(raw)) return true;

            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                value = v;
                return true;
            }

            error = $"'{key}' must be a number.";
            return false;
        }

        private static bool Bool(NameValueCollection q, string key, out bool? value, ref string error)
        {
            value = null;
            var raw = q[key];
            if (string.IsNullOrWhiteSpace(raw)) return true;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                    value = true;
                    return true;
                case "false":
                case "0":
                    value = false;
                    return true;
            }

            error = $"'{key}' must be true or false.";
            return false;
        }
    }

    public class ResultMeta
    {
        public int ChestCount;

        /// <summary>Chests in a loaded zone: contents are live.</summary>
        public int LiveChests;

        /// <summary>Chests only known from the periodic world scan: contents are as of the last scan.</summary>
        public int ScannedChests;

        /// <summary>When the stalest matching snapshot was taken, so a caller can judge how fresh the numbers are.</summary>
        public DateTime? OldestUpdateUtc;

        public DateTime GeneratedAtUtc;
    }

    public class ItemTotal
    {
        public string PrefabName;
        public string DisplayName;
        public int TotalStack;
        public int ChestCount;
    }

    public class ItemLocation
    {
        public string ChestId;
        public string ChestPrefab;
        public Vec3Dto Position;
        public int Stack;
    }

    public class NeedLine
    {
        public string PrefabName;
        public string DisplayName;
        public int Needed;
        public int Have;
        public int Missing;

        /// <summary>Which chests hold it, largest stack first. Only present with where=true.</summary>
        public List<ItemLocation> Where;
    }

    public class NeedResult
    {
        public bool AllSatisfied;
        public List<NeedLine> Items;
        public ResultMeta Meta;
    }

    public class PlayerSummary
    {
        public long PlayerId;
        public string PlayerName;
        public int ChestCount;
    }

    public static class ChestAggregates
    {
        public static ResultMeta Meta(IList<ChestDto> chests)
        {
            return new ResultMeta
            {
                ChestCount = chests.Count,
                LiveChests = chests.Count(c => c.Loaded),
                ScannedChests = chests.Count(c => !c.Loaded),
                OldestUpdateUtc = chests.Count == 0 ? (DateTime?)null : chests.Min(c => c.LastUpdatedUtc),
                GeneratedAtUtc = DateTime.UtcNow,
            };
        }

        /// <summary>Per-item totals across the given chests, most plentiful first. <paramref name="only"/> restricts which items are listed.</summary>
        public static List<ItemTotal> Totals(IEnumerable<ChestDto> chests, ISet<string> only)
        {
            var totals = new Dictionary<string, ItemTotal>(StringComparer.OrdinalIgnoreCase);
            foreach (var chest in chests)
            {
                var seenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in chest.Items)
                {
                    var key = item.PrefabName ?? string.Empty;
                    if (only != null && !only.Contains(key)) continue;

                    if (!totals.TryGetValue(key, out var t))
                    {
                        t = new ItemTotal { PrefabName = key, DisplayName = item.DisplayName };
                        totals[key] = t;
                    }

                    t.TotalStack += item.Stack;
                    if (seenHere.Add(key)) t.ChestCount++;
                }
            }

            return totals.Values.OrderByDescending(t => t.TotalStack).ThenBy(t => t.PrefabName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Parses <c>Iron:20</c>, <c>Wood=50</c> or a bare <c>Stone</c> (meaning 1), from comma-separated and/or repeated values.</summary>
        public static List<KeyValuePair<string, int>> ParseNeeds(string[] raw, out string error)
        {
            error = null;
            var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var chunk in raw ?? new string[0])
            {
                foreach (var part in chunk.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length == 0) continue;

                    var sep = t.IndexOfAny(new[] { ':', '=' });
                    var name = sep < 0 ? t : t.Substring(0, sep).Trim();
                    var qty = 1;
                    if (sep >= 0 && !int.TryParse(t.Substring(sep + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty))
                    {
                        error = $"'{t}': the amount must be a whole number.";
                        return null;
                    }

                    if (name.Length == 0 || qty <= 0)
                    {
                        error = $"'{t}': need an item name and an amount above 0.";
                        return null;
                    }

                    if (!merged.ContainsKey(name))
                    {
                        merged[name] = 0;
                        order.Add(name);
                    }

                    merged[name] += qty;
                }
            }

            if (order.Count == 0)
            {
                error = "Give at least one item, e.g. need=Iron:20,Wood:50.";
                return null;
            }

            return order.Select(n => new KeyValuePair<string, int>(n, merged[n])).ToList();
        }

        public static NeedResult Check(IList<ChestDto> chests, List<KeyValuePair<string, int>> needs, bool includeWhere)
        {
            var names = new HashSet<string>(needs.Select(n => n.Key), StringComparer.OrdinalIgnoreCase);
            var totals = Totals(chests, names).ToDictionary(t => t.PrefabName, StringComparer.OrdinalIgnoreCase);

            var lines = new List<NeedLine>();
            foreach (var need in needs)
            {
                totals.TryGetValue(need.Key, out var total);
                var have = total?.TotalStack ?? 0;
                var line = new NeedLine
                {
                    PrefabName = total?.PrefabName ?? need.Key,
                    DisplayName = total?.DisplayName,
                    Needed = need.Value,
                    Have = have,
                    Missing = Math.Max(0, need.Value - have),
                };

                if (includeWhere)
                {
                    line.Where = chests
                        .Select(c => new ItemLocation
                        {
                            ChestId = c.Id,
                            ChestPrefab = c.PrefabName,
                            Position = c.Position,
                            Stack = c.Items.Where(i => string.Equals(i.PrefabName, need.Key, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Stack),
                        })
                        .Where(l => l.Stack > 0)
                        .OrderByDescending(l => l.Stack)
                        .ToList();
                }

                lines.Add(line);
            }

            return new NeedResult { AllSatisfied = lines.All(l => l.Missing == 0), Items = lines, Meta = Meta(chests) };
        }

        /// <summary>Player-placed chests grouped by owner, so a UI can offer a "whose chests?" picker.</summary>
        public static List<PlayerSummary> Players(IEnumerable<ChestDto> chests, PlayerNames names)
        {
            return chests
                .Where(c => c.PlayerPlaced)
                .GroupBy(c => c.Owner.PlayerId)
                .Select(g => new PlayerSummary
                {
                    PlayerId = g.Key,
                    PlayerName = g.Select(c => c.Owner.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? names?.GetName(g.Key),
                    ChestCount = g.Count(),
                })
                .OrderByDescending(p => p.ChestCount)
                .ToList();
        }
    }
}
