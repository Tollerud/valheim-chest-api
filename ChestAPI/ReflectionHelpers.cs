using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ChestAPI
{
    /// <summary>
    /// Small helpers for pulling data off private/internal members of game types without needing
    /// a "publicized" assembly. We build against the stock game DLL, so anything not part of the
    /// public API goes through here. Everything is best-effort and swallows failures, because the
    /// exact private field names can shift between Valheim versions - see README "Known risk areas".
    /// </summary>
    internal static class ReflectionHelpers
    {
        private static readonly ConcurrentDictionary<(Type, string), FieldInfo> FieldCache = new ConcurrentDictionary<(Type, string), FieldInfo>();
        private static readonly ConcurrentDictionary<(Type, string), PropertyInfo> PropertyCache = new ConcurrentDictionary<(Type, string), PropertyInfo>();

        public static T GetField<T>(object instance, string fieldName, T fallback = default)
        {
            if (instance == null) return fallback;
            try
            {
                var type = instance.GetType();
                var fi = FieldCache.GetOrAdd((type, fieldName), key =>
                    key.Item1.GetField(key.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                if (fi == null) return fallback;
                var value = fi.GetValue(instance);
                return value is T t ? t : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static T GetProperty<T>(object instance, string propertyName, T fallback = default)
        {
            if (instance == null) return fallback;
            try
            {
                var type = instance.GetType();
                var pi = PropertyCache.GetOrAdd((type, propertyName), key =>
                    key.Item1.GetProperty(key.Item2, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                if (pi == null) return fallback;
                var value = pi.GetValue(instance);
                return value is T t ? t : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Looks for the first field or property on the instance whose name contains any of the given
        /// substrings (case-insensitive) and returns its value converted to a string via ToString().
        /// Used for things like chest privacy, where the exact member name isn't certain across versions.
        /// </summary>
        public static string FindFirstMemberValueContaining(object instance, params string[] nameFragments)
        {
            if (instance == null) return null;
            try
            {
                var type = instance.GetType();
                var members = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(f => (Member: (MemberInfo)f, Get: (Func<object>)(() => f.GetValue(instance))))
                    .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(p => p.GetIndexParameters().Length == 0)
                        .Select(p => (Member: (MemberInfo)p, Get: (Func<object>)(() => p.GetValue(instance)))));

                foreach (var (member, get) in members)
                {
                    var lower = member.Name.ToLowerInvariant();
                    if (nameFragments.Any(f => lower.Contains(f.ToLowerInvariant())))
                    {
                        try
                        {
                            var value = get();
                            if (value != null) return value.ToString();
                        }
                        catch
                        {
                            // keep looking
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        public static object CallMethod(object instance, string methodName, params object[] args)
        {
            if (instance == null) return null;
            try
            {
                var type = instance.GetType();
                var mi = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return mi?.Invoke(instance, args);
            }
            catch
            {
                return null;
            }
        }

        private static bool _localizationResolved;
        private static Func<object> _getLocalizationInstance;
        private static MethodInfo _localizeMethod;

        /// <summary>
        /// Best-effort localization: looks up the game's "Localization" type by name across loaded
        /// assemblies instead of referencing it directly, since its exact location/accessibility isn't
        /// guaranteed across Valheim versions (see README "Known risk areas"). Falls back to the raw
        /// key if the type, its singleton, or its Localize(string) method can't be found.
        /// </summary>
        public static string TryLocalize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            try
            {
                if (!_localizationResolved) ResolveLocalization();

                var instance = _getLocalizationInstance?.Invoke();
                if (instance == null || _localizeMethod == null) return key;

                return _localizeMethod.Invoke(instance, new object[] { key }) as string ?? key;
            }
            catch
            {
                return key;
            }
        }

        private static void ResolveLocalization()
        {
            _localizationResolved = true;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(t => t.Name == "Localization");
                if (type == null) return;

                var instanceProperty = type.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (instanceProperty != null)
                {
                    _getLocalizationInstance = () => instanceProperty.GetValue(null);
                }
                else
                {
                    var instanceField = type.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instanceField != null)
                    {
                        _getLocalizationInstance = () => instanceField.GetValue(null);
                    }
                }

                _localizeMethod = type.GetMethod("Localize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
            }
            catch
            {
                // leave unresolved - TryLocalize falls back to the raw key
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }
    }
}
