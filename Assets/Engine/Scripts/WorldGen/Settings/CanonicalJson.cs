using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Deterministic JSON writer for settings snapshots: object keys sorted ordinally, invariant culture,
    ///     round-trip float formatting, two-space indentation, "\n" line ends. The same settings always produce the
    ///     same text, so the output can be hashed, diffed and used as an experiment manifest.
    /// </summary>
    public static class CanonicalJson{
        /// <summary>
        ///     Serialises <paramref name="value" />: null, bool, integer types, float/double, string, Enum (as its
        ///     name), <c>IDictionary&lt;string, object&gt;</c> (sorted) and any other <c>IEnumerable</c> (as array).
        /// </summary>
        public static string Serialize(object value){
            StringBuilder sb = new();
            Write(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>Shortest round-trip representation of a float, invariant culture ("R" format).</summary>
        public static string Float(float value){
            if (float.IsNaN(value)) return "\"NaN\"";
            if (float.IsInfinity(value)) return value > 0 ? "\"Infinity\"" : "\"-Infinity\"";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Write(StringBuilder sb, object value, int indent){
            switch (value){
                case null:
                    sb.Append("null");
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case float f:
                    sb.Append(Float(f));
                    return;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case int or uint or long or ulong or short or ushort or byte or sbyte:
                    sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> dict:
                    WriteObject(sb, dict, indent);
                    return;
                case IEnumerable enumerable:
                    WriteArray(sb, enumerable, indent);
                    return;
                default:
                    throw new ArgumentException($"CanonicalJson cannot serialise {value.GetType().Name}");
            }
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, int indent){
            if (dict.Count == 0){
                sb.Append("{}");
                return;
            }

            List<string> keys = new(dict.Keys);
            keys.Sort(StringComparer.Ordinal);
            sb.Append("{\n");
            for (int i = 0; i < keys.Count; i++){
                Indent(sb, indent + 1);
                WriteString(sb, keys[i]);
                sb.Append(": ");
                Write(sb, dict[keys[i]], indent + 1);
                sb.Append(i < keys.Count - 1 ? ",\n" : "\n");
            }

            Indent(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable enumerable, int indent){
            List<object> items = new();
            foreach (object item in enumerable) items.Add(item);
            if (items.Count == 0){
                sb.Append("[]");
                return;
            }

            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++){
                Indent(sb, indent + 1);
                Write(sb, items[i], indent + 1);
                sb.Append(i < items.Count - 1 ? ",\n" : "\n");
            }

            Indent(sb, indent);
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s){
            sb.Append('"');
            foreach (char c in s){
                switch (c){
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int indent){
            sb.Append(' ', indent * 2);
        }
    }
}
