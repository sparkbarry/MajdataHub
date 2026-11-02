using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhythmHub.Updater.Security;

/// <summary>
/// RFC 8785 (JCS) canonical form for the subset of JSON manifests use.
/// Numbers are restricted to integers within ±2^53 so their ES6 form is just the decimal digits;
/// any other number is rejected rather than risk a canonicalization mismatch.
/// </summary>
public static class JsonCanonicalizer
{
    private const long MaxSafeInteger = 9007199254740991;

    public static byte[] Canonicalize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                // JCS sorts by UTF-16 code units, which is what ordinal string comparison does.
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(key, sb);
                    sb.Append(':');
                    Write(value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue value:
                WriteValue(value, sb);
                break;
        }
    }

    private static void WriteValue(JsonValue value, StringBuilder sb)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(value.GetValue<string>(), sb);
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.Number:
                long n;
                if (value.TryGetValue<long>(out var l)) n = l;
                else if (value.TryGetValue<int>(out var i)) n = i;
                else if (value.TryGetValue<JsonElement>(out var e) && e.TryGetInt64(out var el)) n = el;
                else throw new FormatException($"Only safe integers are allowed in signed JSON, got {value.ToJsonString()}.");
                if (n > MaxSafeInteger || n < -MaxSafeInteger)
                    throw new FormatException($"Integer {n} is outside the safe range.");
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new FormatException($"Unexpected JSON value kind {value.GetValueKind()}.");
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}
