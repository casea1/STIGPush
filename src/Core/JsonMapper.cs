using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StigGpoManager.Core
{
    /// <summary>Marks a property that JsonMapper must not serialize.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class JsonIgnoreAttribute : Attribute { }

    /// <summary>
    /// Maps plain classes (public read/write properties of string, numbers, bool, DateTime, enums,
    /// List&lt;T&gt;, Dictionary&lt;string,T&gt; and nested classes) to and from the Json object model.
    /// </summary>
    public static class JsonMapper
    {
        public static string Serialize(object obj) => Json.Serialize(ToJsonValue(obj));

        public static T Deserialize<T>(string json) where T : new() => (T)FromJsonValue(Json.Parse(json), typeof(T));

        public static object ToJsonValue(object value)
        {
            if (value == null) return null;
            var t = value.GetType();
            if (value is string || value is bool || value is int || value is long || value is double || value is decimal) return value;
            if (value is DateTime dt) return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            if (t.IsEnum) return value.ToString();
            if (value is IDictionary dict)
            {
                var o = Json.NewObject();
                foreach (DictionaryEntry kv in dict) o[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)] = ToJsonValue(kv.Value);
                return o;
            }
            if (value is IEnumerable list)
            {
                var l = new List<object>();
                foreach (var item in list) l.Add(ToJsonValue(item));
                return l;
            }
            var obj = Json.NewObject();
            foreach (var p in Props(t))
                obj[p.Name] = ToJsonValue(p.GetValue(value, null));
            return obj;
        }

        public static object FromJsonValue(object json, Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (json == null) return type.IsValueType && nullable == null ? Activator.CreateInstance(type) : null;
            if (nullable != null) type = nullable;

            if (type == typeof(string)) return Convert.ToString(json, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return json is bool b ? b : bool.Parse(Convert.ToString(json, CultureInfo.InvariantCulture));
            if (type == typeof(int)) return Convert.ToInt32(json, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return Convert.ToInt64(json, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return Convert.ToDouble(json, CultureInfo.InvariantCulture);
            if (type == typeof(DateTime))
                return DateTime.Parse(Convert.ToString(json, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(json, CultureInfo.InvariantCulture), true);
            if (type == typeof(object)) return json;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var valueType = type.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(type, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in (Dictionary<string, object>)json) dict[kv.Key] = FromJsonValue(kv.Value, valueType);
                return dict;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var itemType = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in (List<object>)json) list.Add(FromJsonValue(item, itemType));
                return list;
            }

            var src = json as Dictionary<string, object>;
            if (src == null) throw new FormatException($"Expected a JSON object for {type.Name}.");
            var target = Activator.CreateInstance(type);
            foreach (var p in Props(type))
            {
                if (src.TryGetValue(p.Name, out var v))
                    p.SetValue(target, FromJsonValue(v, p.PropertyType), null);
            }
            return target;
        }

        private static IEnumerable<PropertyInfo> Props(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0);
    }
}
