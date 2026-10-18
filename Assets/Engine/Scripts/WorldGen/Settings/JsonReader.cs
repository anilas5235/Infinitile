using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Minimal JSON reader for settings import (TRN-05), the counterpart of <see cref="CanonicalJson" />.
    ///     Objects become <c>Dictionary&lt;string, object&gt;</c>, arrays <c>List&lt;object&gt;</c>, numbers
    ///     <c>double</c>, plus string, bool and null. <c>JsonUtility</c> cannot read the polymorphic spline values
    ///     (number or nested object), hence this reader.
    /// </summary>
    public static class JsonReader{
        /// <summary>Parses <paramref name="json" />; throws <see cref="FormatException" /> on malformed input.</summary>
        public static object Parse(string json){
            if (json == null) throw new ArgumentNullException(nameof(json));
            int pos = 0;
            object value = ReadValue(json, ref pos);
            SkipWhitespace(json, ref pos);
            if (pos != json.Length) throw Error(json, pos, "trailing characters");
            return value;
        }

        private static object ReadValue(string s, ref int pos){
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw Error(s, pos, "unexpected end of input");
            char c = s[pos];
            switch (c){
                case '{': return ReadObject(s, ref pos);
                case '[': return ReadArray(s, ref pos);
                case '"': return ReadString(s, ref pos);
                case 't': return ReadLiteral(s, ref pos, "true", true);
                case 'f': return ReadLiteral(s, ref pos, "false", false);
                case 'n': return ReadLiteral(s, ref pos, "null", null);
                default:
                    if (c == '-' || char.IsDigit(c)) return ReadNumber(s, ref pos);
                    throw Error(s, pos, $"unexpected character '{c}'");
            }
        }

        private static Dictionary<string, object> ReadObject(string s, ref int pos){
            Dictionary<string, object> result = new();
            pos++; // {
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}'){
                pos++;
                return result;
            }

            while (true){
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw Error(s, pos, "expected a key");
                string key = ReadString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw Error(s, pos, "expected ':'");
                pos++;
                result[key] = ReadValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw Error(s, pos, "unterminated object");
                if (s[pos] == ','){
                    pos++;
                    continue;
                }

                if (s[pos] == '}'){
                    pos++;
                    return result;
                }

                throw Error(s, pos, "expected ',' or '}'");
            }
        }

        private static List<object> ReadArray(string s, ref int pos){
            List<object> result = new();
            pos++; // [
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']'){
                pos++;
                return result;
            }

            while (true){
                result.Add(ReadValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw Error(s, pos, "unterminated array");
                if (s[pos] == ','){
                    pos++;
                    continue;
                }

                if (s[pos] == ']'){
                    pos++;
                    return result;
                }

                throw Error(s, pos, "expected ',' or ']'");
            }
        }

        private static string ReadString(string s, ref int pos){
            StringBuilder sb = new();
            pos++; // opening quote
            while (true){
                if (pos >= s.Length) throw Error(s, pos, "unterminated string");
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\'){
                    sb.Append(c);
                    continue;
                }

                if (pos >= s.Length) throw Error(s, pos, "unterminated escape");
                char e = s[pos++];
                switch (e){
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw Error(s, pos, "short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw Error(s, pos - 1, $"unknown escape '\\{e}'");
                }
            }
        }

        private static object ReadNumber(string s, ref int pos){
            int start = pos;
            if (s[pos] == '-') pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-'))
                pos++;
            string text = s.Substring(start, pos - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw Error(s, start, $"bad number '{text}'");
            return value;
        }

        private static object ReadLiteral(string s, ref int pos, string literal, object value){
            if (string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0) throw Error(s, pos, $"expected '{literal}'");
            pos += literal.Length;
            return value;
        }

        private static void SkipWhitespace(string s, ref int pos){
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static FormatException Error(string s, int pos, string message){
            int line = 1, column = 1;
            for (int i = 0; i < pos && i < s.Length; i++){
                if (s[i] == '\n'){
                    line++;
                    column = 1;
                }
                else column++;
            }

            return new FormatException($"JSON error at line {line}, column {column}: {message}.");
        }
    }
}
