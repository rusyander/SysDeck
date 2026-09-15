// SysDeck — минимальный JSON-DOM с сохранением порядка и незнакомых полей
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    // ================================================================== //
    //  Данные браузеров: закладки, сохранённые группы вкладок,
    //  список для чтения, открытый сеанс.
    //
    //  Всё читается напрямую из файлов профиля, без расширений и API:
    //   - Bookmarks          — JSON (правится нами, поэтому нужен свой парсер,
    //                          сохраняющий порядок и незнакомые поля);
    //   - Sync Data/LevelDB  — LevelDB + snappy + protobuf: там лежат
    //                          сохранённые группы вкладок и список для чтения;
    //   - Sessions/Session_* — формат SNSS: окна, вкладки и группы текущего сеанса.
    // ================================================================== //

    public enum JKind { Null, Bool, Num, Str, Arr, Obj }

    // Минимальный JSON-DOM. Нужен именно свой: DataContractJsonSerializer теряет
    // неизвестные поля (meta_info, power_bookmark_meta и т.п.) и порядок ключей,
    // а файл закладок мы переписываем обратно — терять там ничего нельзя.
    public class JVal
    {
        public JKind Kind;
        public bool B;
        public string Raw;                                  // Num — исходный текст, Str — уже раскодированная строка
        public List<string> K;                              // ключи объекта
        public List<JVal> V;                                // значения объекта / элементы массива

        public static JVal NewObj() { JVal j = new JVal(); j.Kind = JKind.Obj; j.K = new List<string>(); j.V = new List<JVal>(); return j; }
        public static JVal NewArr() { JVal j = new JVal(); j.Kind = JKind.Arr; j.V = new List<JVal>(); return j; }
        public static JVal NewStr(string s) { JVal j = new JVal(); j.Kind = JKind.Str; j.Raw = s ?? ""; return j; }
        public static JVal NewNum(string raw) { JVal j = new JVal(); j.Kind = JKind.Num; j.Raw = raw; return j; }

        public JVal Get(string name)
        {
            if (Kind != JKind.Obj || K == null) return null;
            for (int i = 0; i < K.Count; i++) if (K[i] == name) return V[i];
            return null;
        }

        public string GetStr(string name)
        {
            JVal v = Get(name);
            return v == null ? null : (v.Kind == JKind.Str || v.Kind == JKind.Num ? v.Raw : null);
        }

        public void Set(string name, JVal val)
        {
            if (Kind != JKind.Obj) return;
            for (int i = 0; i < K.Count; i++) if (K[i] == name) { V[i] = val; return; }
            K.Add(name); V.Add(val);
        }

        public bool Remove(string name)
        {
            if (Kind != JKind.Obj || K == null) return false;
            for (int i = 0; i < K.Count; i++) if (K[i] == name) { K.RemoveAt(i); V.RemoveAt(i); return true; }
            return false;
        }
    }

    public static class Jsn
    {
        public static JVal Parse(string s)
        {
            int i = 0;
            JVal v = ParseValue(s, ref i);
            return v;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static JVal ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("json: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                JVal o = JVal.NewObj();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("json: key expected");
                    string key = ParseString(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("json: ':' expected");
                    i++;
                    JVal val = ParseValue(s, ref i);
                    o.K.Add(key); o.V.Add(val);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    throw new FormatException("json: ',' or '}' expected");
                }
                return o;
            }
            if (c == '[')
            {
                JVal a = JVal.NewArr();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.V.Add(ParseValue(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    throw new FormatException("json: ',' or ']' expected");
                }
                return a;
            }
            if (c == '"') return JVal.NewStr(ParseString(s, ref i));
            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; JVal j = new JVal(); j.Kind = JKind.Bool; j.B = true; return j; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; JVal j = new JVal(); j.Kind = JKind.Bool; j.B = false; return j; }
            if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; JVal j = new JVal(); j.Kind = JKind.Null; return j; }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) throw new FormatException("json: bad value at " + i);
            return JVal.NewNum(s.Substring(start, i - start));
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // "
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("json: unterminated string");
        }

        // Пишем так же, как Chrome: отступ в 3 пробела, не-ASCII экранируется в \uXXXX.
        public static string Write(JVal v)
        {
            StringBuilder sb = new StringBuilder(1 << 20);
            WriteVal(sb, v, 0);
            sb.Append("\n");
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int n)
        {
            sb.Append('\n');
            sb.Append(' ', n * 3);
        }

        private static void WriteVal(StringBuilder sb, JVal v, int lvl)
        {
            switch (v.Kind)
            {
                case JKind.Null: sb.Append("null"); break;
                case JKind.Bool: sb.Append(v.B ? "true" : "false"); break;
                case JKind.Num: sb.Append(v.Raw); break;
                case JKind.Str: WriteStr(sb, v.Raw); break;
                case JKind.Arr:
                    if (v.V.Count == 0) { sb.Append("[  ]"); break; }
                    sb.Append("[ ");
                    for (int i = 0; i < v.V.Count; i++)
                    {
                        if (i > 0) { sb.Append(','); Indent(sb, lvl + 1); }
                        WriteVal(sb, v.V[i], lvl + 1);
                    }
                    sb.Append(" ]");
                    break;
                case JKind.Obj:
                    if (v.K.Count == 0) { sb.Append("{\n"); sb.Append(' ', lvl * 3); sb.Append("}"); break; }
                    sb.Append("{");
                    for (int i = 0; i < v.K.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Indent(sb, lvl + 1);
                        WriteStr(sb, v.K[i]);
                        sb.Append(": ");
                        WriteVal(sb, v.V[i], lvl + 1);
                    }
                    Indent(sb, lvl);
                    sb.Append("}");
                    break;
            }
        }

        // Как JSON.stringify(v, null, 2) у Node: отступ 2, не-ASCII как есть. Для файлов, которые пишет Node-программа
        // (settings.json Claude Code), — чтобы добавление одной записи не переформатировало весь файл.
        public static string WriteNodeStyle(JVal v)
        {
            StringBuilder sb = new StringBuilder(1 << 16);
            WriteNode(sb, v, 0);
            sb.Append("\n");
            return sb.ToString();
        }

        private static void WriteNode(StringBuilder sb, JVal v, int lvl)
        {
            switch (v.Kind)
            {
                case JKind.Null: sb.Append("null"); break;
                case JKind.Bool: sb.Append(v.B ? "true" : "false"); break;
                case JKind.Num: sb.Append(v.Raw); break;
                case JKind.Str: WriteNodeStr(sb, v.Raw); break;
                case JKind.Arr:
                case JKind.Obj:
                    {
                        bool obj = v.Kind == JKind.Obj;
                        if (v.V.Count == 0) { sb.Append(obj ? "{}" : "[]"); break; }
                        sb.Append(obj ? '{' : '[');
                        for (int i = 0; i < v.V.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append('\n').Append(' ', (lvl + 1) * 2);
                            if (obj) { WriteNodeStr(sb, v.K[i]); sb.Append(": "); }
                            WriteNode(sb, v.V[i], lvl + 1);
                        }
                        sb.Append('\n').Append(' ', lvl * 2).Append(obj ? '}' : ']');
                        break;
                    }
            }
        }

        private static void WriteNodeStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
                foreach (char c in s)
                {
                    if (c == '"') sb.Append("\\\"");
                    else if (c == '\\') sb.Append("\\\\");
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    else if (c == '\b') sb.Append("\\b");
                    else if (c == '\f') sb.Append("\\f");
                    else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
            sb.Append('"');
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
                foreach (char c in s)
                {
                    if (c == '"') sb.Append("\\\"");
                    else if (c == '\\') sb.Append("\\\\");
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
            sb.Append('"');
        }
    }
}
