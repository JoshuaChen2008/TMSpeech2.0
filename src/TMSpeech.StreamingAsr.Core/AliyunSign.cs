using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 扩展点：阿里云 NLS 的 "nlsToken" 鉴权——用 AccessKeyId/Secret 调 CreateToken (RPC, HMAC-SHA1)
/// 签发临时 Token。这是无法用纯配置表达的部分（签名算法），故以代码扩展点形式存在。
/// </summary>
public static class AliyunSign
{
    private record TokenResult(string Token, long ExpireTime);

    private static readonly Dictionary<string, TokenResult> _cache = new();
    private static readonly object _lock = new();

    private static string MetaHost(string region) => region == "ap-southeast-1"
        ? "nls-meta.ap-southeast-1.aliyuncs.com"
        : "nls-meta.cn-shanghai.aliyuncs.com";

    public static async Task<string> GetTokenAsync(string accessKeyId, string accessKeySecret,
        string region, CancellationToken ct = default)
    {
        var cacheKey = accessKeyId + "|" + region;
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpireTime - 60 > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return cached.Token;
        }

        var result = await CreateTokenAsync(accessKeyId, accessKeySecret, region, ct);
        lock (_lock)
        {
            _cache[cacheKey] = result;
        }

        return result.Token;
    }

    private static async Task<TokenResult> CreateTokenAsync(string accessKeyId, string accessKeySecret,
        string region, CancellationToken ct)
    {
        var regionId = region == "ap-southeast-1" ? "ap-southeast-1" : "cn-shanghai";

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = accessKeyId,
            ["Action"] = "CreateToken",
            ["Format"] = "JSON",
            ["RegionId"] = regionId,
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = Guid.NewGuid().ToString(),
            ["SignatureVersion"] = "1.0",
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["Version"] = "2019-02-28"
        };

        var canonicalized = string.Join("&", parameters.Select(kv =>
            $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}"));
        var stringToSign = "GET&" + PercentEncode("/") + "&" + PercentEncode(canonicalized);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret + "&"));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var url = $"https://{MetaHost(region)}/?Signature={PercentEncode(signature)}&{canonicalized}";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"阿里云 Token 获取失败 (HTTP {(int)resp.StatusCode})：{body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Token", out var tokenEl))
            throw new InvalidOperationException($"阿里云 Token 响应异常：{body}");

        var id = tokenEl.GetProperty("Id").GetString() ?? "";
        long expire = tokenEl.TryGetProperty("ExpireTime", out var exp) ? exp.GetInt64() : 0;
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException($"阿里云 Token 为空：{body}");

        return new TokenResult(id, expire);
    }

    private static string PercentEncode(string value)
    {
        var encoded = Uri.EscapeDataString(value);
        return encoded.Replace("+", "%20").Replace("*", "%2A").Replace("%7E", "~");
    }
}
