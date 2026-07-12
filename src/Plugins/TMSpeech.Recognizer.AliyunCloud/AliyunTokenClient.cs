using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TMSpeech.Recognizer.AliyunCloud;

/// <summary>
/// 通过 AccessKeyId / AccessKeySecret 调用阿里云 CreateToken (RPC) 接口签发临时 Token。
/// 签名算法见：https://help.aliyun.com/document_detail/72153.html
/// </summary>
public static class AliyunTokenClient
{
    private record TokenResult(string Token, long ExpireTime);

    // 简单的进程内缓存：key = akId|region
    private static readonly Dictionary<string, TokenResult> _cache = new();
    private static readonly object _lock = new();

    public static string MetaHost(string region) => region == "ap-southeast-1"
        ? "nls-meta.ap-southeast-1.aliyuncs.com"
        : "nls-meta.cn-shanghai.aliyuncs.com";

    /// <summary>
    /// 获取有效 Token（带缓存，过期前 60 秒视为失效）。
    /// </summary>
    public static async Task<string> GetTokenAsync(string accessKeyId, string accessKeySecret,
        string region, CancellationToken ct = default)
    {
        var cacheKey = accessKeyId + "|" + region;
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpireTime - 60 > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return cached.Token;
            }
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
        // 注意：CreateToken 的 RegionId 固定使用对应的服务区域；元服务 host 也随之切换。
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

        // 1. 构造规范化请求字符串
        var canonicalized = string.Join("&", parameters.Select(kv =>
            $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}"));

        // 2. 构造待签名字符串
        var stringToSign = "GET&" + PercentEncode("/") + "&" + PercentEncode(canonicalized);

        // 3. 计算签名（key 末尾要加 '&'）
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret + "&"));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        // 4. 拼接最终 URL
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

    /// <summary>阿里云 POP 规范的百分号编码（RFC3986 + 三处替换）。</summary>
    private static string PercentEncode(string value)
    {
        var encoded = Uri.EscapeDataString(value);
        return encoded.Replace("+", "%20").Replace("*", "%2A").Replace("%7E", "~");
    }
}
