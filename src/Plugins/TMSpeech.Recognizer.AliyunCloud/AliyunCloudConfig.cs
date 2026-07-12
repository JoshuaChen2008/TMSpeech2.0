using System.Text.Json.Serialization;

namespace TMSpeech.Recognizer.AliyunCloud;

/// <summary>
/// 阿里云智能语音交互（NLS）识别器配置。
/// 字段对应设置界面：Appkey / AccessKeyId / AccessKeySecret / 服务区域。
/// </summary>
public class AliyunCloudConfig
{
    [JsonPropertyName("appKey")] public string AppKey { get; set; } = "";

    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";

    [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";

    /// <summary>服务区域：cn-shanghai（中国）/ ap-southeast-1（海外·新加坡）。</summary>
    [JsonPropertyName("region")] public string Region { get; set; } = "cn-shanghai";

    /// <summary>
    /// 可选：直接粘贴一个有效 Token，跳过用 AccessKey 自动签发。
    /// 留空则用 AccessKeyId/AccessKeySecret 自动获取并缓存 Token。
    /// </summary>
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}
