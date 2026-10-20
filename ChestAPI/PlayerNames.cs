using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ChestAPI
{
    /// <summary>
    /// player id -> last known player name. Valheim only stores the creator's numeric id on a chest, and the
    /// server only knows names of players who are connected right now, so a chest owned by someone offline would
    /// otherwise always have <c>PlayerName: null</c>. Names are learned whenever they are visible (a connected
    /// peer, or the crafter name stamped on any item in any chest) and kept in a small JSON file next to the
    /// BepInEx config so they survive restarts.
    /// </summary>
    public sealed class PlayerNames
    {
        private readonly ConcurrentDictionary<long, string> _byId = new ConcurrentDictionary<long, string>();
        private readonly string _path;
        private readonly object _saveLock = new object();
        private volatile bool _dirty;

        /// <param name="path">File to persist to, or null to keep names in memory only.</param>
        public PlayerNames(string path)
        {
            _path = path;
        }

        public int Count => _byId.Count;

        public void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;

            var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded == null) return;

            foreach (var pair in loaded)
            {
                if (long.TryParse(pair.Key, out var id) && id != 0 && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    _byId[id] = pair.Value;
                }
            }
        }

        /// <summary>Records a name. Returns true if it was new or changed.</summary>
        public bool Learn(long playerId, string name)
        {
            if (playerId == 0 || string.IsNullOrWhiteSpace(name)) return false;

            var changed = false;
            _byId.AddOrUpdate(
                playerId,
                _ => { changed = true; return name; },
                (_, existing) =>
                {
                    if (existing == name) return existing;
                    changed = true;
                    return name;
                });

            if (changed) _dirty = true;
            return changed;
        }

        public string GetName(long playerId) => _byId.TryGetValue(playerId, out var name) ? name : null;

        /// <summary>All ids whose name matches (case-insensitive). Usually one; empty if the name was never seen.</summary>
        public List<long> FindIds(string name)
        {
            return _byId
                .Where(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToList();
        }

        public void SaveIfDirty()
        {
            if (!_dirty || string.IsNullOrEmpty(_path)) return;

            lock (_saveLock)
            {
                if (!_dirty) return;
                _dirty = false;

                var snapshot = _byId.ToDictionary(p => p.Key.ToString(), p => p.Value);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
        }
    }
}
