using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace KbmOverlay
{
    // Small helpers around JavaScriptSerializer (ships with .NET Framework).
    internal static class Json
    {
        public static Dictionary<string, object> Parse(string text)
        {
            try
            {
                return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static string Write(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        public static string Str(Dictionary<string, object> map, string key)
        {
            return map != null && map.TryGetValue(key, out object value) ? value as string : null;
        }

        public static bool? Bool(Dictionary<string, object> map, string key)
        {
            return map != null && map.TryGetValue(key, out object value) && value is bool b ? b : (bool?)null;
        }

        public static long? Long(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out object value) || value == null) return null;
            switch (value)
            {
                case int i: return i;
                case long l: return l;
                case decimal d when d >= long.MinValue && d <= long.MaxValue: return (long)d;
                case double f when f >= long.MinValue && f <= long.MaxValue: return (long)f;
                default: return null;
            }
        }

        public static object[] Array(Dictionary<string, object> map, string key)
        {
            return map != null && map.TryGetValue(key, out object value) ? value as object[] : null;
        }
    }
}
