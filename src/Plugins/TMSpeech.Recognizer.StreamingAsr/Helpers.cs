using System.Text;
using System.Text.Json;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>占位符模板渲染：把 {key} 替换为 vars[key]。</summary>
public static class TemplateRenderer
{
    public static string Render(string? template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        var sb = new StringBuilder(template);
        foreach (var kv in vars)
            sb.Replace("{" + kv.Key + "}", kv.Value);
        return sb.ToString();
    }
}

/// <summary>点路径取值：把 "a.b.c" 在 JSON 对象上逐层取下。仅支持对象路径。</summary>
public static class JsonPathResolver
{
    public static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out var next))
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    public static string GetString(JsonElement root, string path)
    {
        if (!TryGet(root, path, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    public static bool GetBool(JsonElement root, string path)
    {
        return TryGet(root, path, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
