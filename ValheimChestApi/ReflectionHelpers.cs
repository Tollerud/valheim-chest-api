using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace ValheimChestApi
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
    }
}
