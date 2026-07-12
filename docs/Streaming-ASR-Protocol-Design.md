# TMSpeech 通用流式 ASR —— 协议解耦设计文档

目标：把各家流式语音识别（ASR）在 WebSocket 协议上的差异，抽象成一份**可配置的"协议模板（Protocol Profile）"**，让新服务商通过"预设 + 少量代码扩展点"即可接入，而不必每家重写一个插件。用户层面只需"选预设 + 填密钥/URL"，高级用户可编辑模板。

适用范围：**流式（WebSocket）ASR**。非流式 HTTP（OpenAI `/v1/audio/transcriptions`、MiMo chat 等）是另一条更易统一的路径，本设计预留位置但不作为主线。

---

## 一、现状与问题

当前两个识别器插件都是**按厂商硬编码**的：

- `TMSpeech.Recognizer.AliyunCloud`：阿里云 NLS（StartTranscription/SentenceEnd，AccessKey 签名换 Token，二进制帧）。
- `TMSpeech.Recognizer.LLMAudio`：阿里云百炼 DashScope Fun-ASR（run-task/result-generated，API Key，二进制帧）。

每接一家就复制一份 `IRecognizer`、改协议细节。维护成本随厂商数量线性增长。问题在于：**各家协议没有统一标准**，差异分布在固定的几个维度上（见下）。既然差异维度是有限且可枚举的，就能把"可数据化的部分"抽成配置，把"必须写代码的部分"收敛成少数扩展点。

---

## 二、差异维度（抽象的依据）

以已对接/已查证的三套为样本：

| 维度 | 阿里云 NLS | DashScope(Fun-ASR) | OpenAI Realtime |
|------|-----------|--------------------|-----------------|
| 地址 | `wss://nls-gateway-{region}.../ws/v1` | `wss://dashscope.../api-ws/v1/inference/` | `wss://api.openai.com/v1/realtime?intent=transcription` |
| 鉴权位置 | 头 `X-NLS-Token` | 头 `Authorization: bearer` | 头 `Authorization: Bearer` + `OpenAI-Beta: realtime=v1` |
| 凭证类型 | AccessKey **签名换 Token** | 直接 API Key | 直接 API Key |
| 开始指令 | `StartTranscription` | `run-task` | `session.update` |
| 等待回执 | `TranscriptionStarted` | `task-started` | `session.updated` |
| **音频传输** | 二进制帧 | 二进制帧 | **base64 塞 JSON**（`input_audio_buffer.append`） |
| 事件判别字段 | `header.name` | `header.event` | `type` |
| 结果文本路径 | `payload.result` | `payload.output.sentence.text` | `...transcription.delta/.completed` |
| 中间/最终区分 | 不同事件名 | 布尔 `sentence_end` | 不同事件名 |
| 断句参数 | — | `max_sentence_silence` | `turn_detection.silence_duration_ms` |
| 结束指令 | `StopTranscription` | `finish-task` | 关连接 / commit |
| 错误字段 | `header.status_text` | `header.error_message` | `error.message` |

**可数据化（进配置）**：地址、鉴权头与前缀、凭证、音频格式/采样率、开始/结束指令（JSON 模板 + 占位符）、结果取值（字段路径）、事件判别、断句参数名、错误字段。

**不可纯数据化（留代码扩展点）**：
1. **凭证签名**（NLS 的 HMAC 换 Token）——算法，不能用字符串拼。
2. **音频模式**（二进制帧 vs base64-JSON）——结构分歧，做成枚举 + 模板。
3. **逐条动态字段**（每条消息新 message_id、base64 分片）——占位符 + 代码填充。
4. **握手顺序 / 心跳 ping**——个别协议要多步或定时帧。

---

## 三、总体架构

新增**一个通用插件** `TMSpeech.Recognizer.StreamingAsr`，内部由"协议内核 + 协议模板 + 少量扩展点"组成。原来的两个硬编码插件可逐步并入预设。

```
IRecognizer (StreamingAsrRecognizer)
        │  读取所选 Profile（预设或用户自定义）
        ▼
StreamingAsrEngine（协议内核，厂商无关）
   ├─ IAuthProvider          ← 扩展点①：鉴权（apiKeyHeader / nlsToken / none）
   ├─ IAudioTransport        ← 扩展点②：音频（binary / base64Json）
   ├─ TemplateRenderer       ← 占位符渲染（{model}{task_id}{audio_base64}…）
   ├─ JsonPathResolver       ← 按路径取结果字段
   └─ ResultClassifier       ← 中间/最终判定（事件名 或 布尔标志）
        │
        ▼
ProfileStore（内置预设 + 用户自定义 JSON）
```

内核只做一件事：**按 Profile 连接 → 鉴权 → 发开始指令 → 等回执 → 推音频 → 解析结果 → 收尾**。所有厂商差异要么来自 Profile（数据），要么来自被 Profile 按名字选中的扩展点（代码）。

---

## 四、协议模板（Protocol Profile）数据模型

```csharp
public class StreamingAsrProfile
{
    public string Name;                 // 预设名，如 "阿里云 Fun-ASR"
    public string UrlTemplate;          // 支持 {region}{model} 占位
    public Dictionary<string,string> RegionUrls; // 可选：region -> url 片段/完整 url

    public AuthSpec Auth;
    public AudioSpec Audio;

    public string? StartMessageTemplate; // JSON 字符串，含占位符；null=无开始指令
    public string? StartAckEvent;        // 需等待的回执事件取值；null=不等待
    public string? StopMessageTemplate;  // JSON；null=直接关连接

    public ResultSpec Result;
    public ErrorSpec Error;

    public Dictionary<string,string> Params; // 透传给模板的额外参数（如断句阈值字段名/值）
}

public class AuthSpec
{
    public string Provider;             // "apiKeyHeader" | "nlsToken" | "none"
    public string? HeaderName;          // apiKeyHeader：如 "Authorization" / "X-NLS-Token"
    public string? Scheme;              // "Bearer" | "bearer" | ""（直接放值）
    public Dictionary<string,string>? ExtraHeaders; // 静态附加头，如 OpenAI-Beta
}

public enum AudioMode { Binary, Base64Json }
public class AudioSpec
{
    public AudioMode Mode = AudioMode.Binary;
    public string Format = "pcm";
    public int SampleRate = 16000;
    public string? MessageTemplate;     // Base64Json：含 {audio_base64}（可含 {message_id}）
}

public class ResultSpec
{
    public string EventPath;            // 事件判别字段路径，如 "header.event" / "type"
    public string[] PartialEvents;      // 表示"中间结果"的事件取值
    public string[] FinalEvents;        // 表示"整句完成"的事件取值（事件模式）
    public string TextPath;             // 文本路径，如 "payload.output.sentence.text"
    public string? FinalFlagPath;       // 布尔标志模式：如 "payload.output.sentence.sentence_end"
}

public class ErrorSpec
{
    public string? EventValue;          // 如 "task-failed"
    public string? MessagePath;         // 如 "header.error_message"
}
```

**中间/最终的两种判定模式**（覆盖三家）：
- **事件模式**：`EventPath` 的取值落在 `FinalEvents` → 整句；落在 `PartialEvents` → 中间。（NLS、OpenAI）
- **标志模式**：同一种事件，看 `FinalFlagPath` 的布尔值。（DashScope）
两者择一配置即可。

---

## 五、占位符与模板渲染

内核维护一个变量字典 `vars`，渲染所有模板（URL、开始/结束指令、音频 JSON、鉴权）：

| 占位符 | 来源 |
|--------|------|
| `{api_key}` | 用户配置 |
| `{model}` | 用户配置 |
| `{region}` | 用户配置（映射到 URL） |
| `{sample_rate}` | 固定 16000（音频源决定） |
| `{task_id}` | 会话开始时生成的 32 位十六进制，整场不变 |
| `{message_id}` | **每条消息**新生成（部分协议要求） |
| `{audio_base64}` | Base64Json 模式下，当前音频帧的 base64 |
| `{max_sentence_silence}` | 用户配置（断句阈值），未填则不注入 |

渲染规则：简单字符串替换即可；JSON 模板先替换再作为整体发送。`{message_id}` 在"每次发送前"重算。

---

## 六、内核流程（伪代码）

```
Start():
  vars = { api_key, model, region, sample_rate, task_id=hex32(), ...用户参数 }
  url  = render(profile.UrlTemplate or RegionUrls[region], vars)
  ws   = new ClientWebSocket()
  authProvider(profile.Auth).Apply(ws, vars)   // 设置头；nlsToken 在这步内部签名换 token
  await ws.Connect(url)

  startReceiveLoop()                            // 后台收消息
  if profile.StartMessageTemplate != null:
     send(render(StartMessageTemplate, vars))
  if profile.StartAckEvent != null:
     await ackReceived(StartAckEvent)  (超时则报错)

  transport = audioTransport(profile.Audio.Mode)
  foreach pcmFrame in feedQueue:               // Feed() 把 float→PCM16 入队
     transport.Send(ws, pcmFrame, profile.Audio, vars)
       // Binary:    ws.Send(pcm, Binary)
       // Base64Json: vars[audio_base64]=b64(pcm); vars[message_id]=hex32();
       //             ws.Send(render(Audio.MessageTemplate, vars), Text)

ReceiveLoop(msg):
  ev = jsonPath(msg, Result.EventPath)
  if ev == Error.EventValue:  raise( jsonPath(msg, Error.MessagePath) )
  if ev == StartAckEvent:     ackReceived = true
  if isResult(ev):                              // ev in Partial/FinalEvents 或就是结果事件
     text = jsonPath(msg, Result.TextPath)
     final = Result.FinalFlagPath != null
                ? bool(jsonPath(msg, FinalFlagPath))
                : FinalEvents.Contains(ev)
     if final: SentenceDone(text) else TextChanged(text)

Stop():
  if StopMessageTemplate != null: send(render(StopMessageTemplate, vars))
  close ws; 回收线程
```

`Feed()`、PCM16 转换、发送队列、背压——和现有 `AliyunCloudRecognizer` 完全一致，可直接复用。

---

## 七、扩展点（不能纯配置的部分）

### ① 鉴权 IAuthProvider
```csharp
public interface IAuthProvider {
    Task ApplyAsync(ClientWebSocket ws, StreamingAsrProfile p,
                    IDictionary<string,string> vars, CancellationToken ct);
}
```
内置实现：
- `ApiKeyHeaderAuth`：`ws.Options.SetRequestHeader(HeaderName, Scheme+" "+api_key)` + ExtraHeaders。覆盖 DashScope / OpenAI。
- `NlsTokenAuth`：用 AccessKeyId/Secret 走 HMAC-SHA1 **签名换 Token**（即现有 `AliyunTokenClient` 的逻辑），再设 `X-NLS-Token`。覆盖 NLS。
- `NoAuth`：占位。

Profile 通过 `Auth.Provider` 字符串选择；要支持新签名体系时只加一个类。

### ② 音频传输 IAudioTransport
```csharp
public interface IAudioTransport {
    Task SendAsync(ClientWebSocket ws, byte[] pcm16, AudioSpec a,
                   IDictionary<string,string> vars, CancellationToken ct);
}
```
内置：`BinaryTransport`（直接发字节）、`Base64JsonTransport`（按 `MessageTemplate` 包 JSON）。枚举固定，基本不需扩展。

### ③（可选）握手/心跳钩子
极少数协议需要多步握手或定时 ping，预留 `ISessionHook { OnConnected, OnTick }`，默认空实现。

---

## 八、用户配置界面

复用现有 `PluginConfigView`（已支持 Text/Password/Option/Number）：

- **协议预设**（Option 下拉）：阿里云 Fun-ASR / 阿里云 NLS / OpenAI Realtime / 自定义…
- 选定预设后动态展示该预设需要的字段（靠 `IPluginConfigEditor.FormItemsUpdated` 动态刷新表单）：
  - 通用：API Key（Password）、模型（Text）、区域（Option）、断句静音（Number）
  - NLS 预设额外：AppKey、AccessKeyId、AccessKeySecret
- **高级（可选）**："编辑协议模板" → 一个多行文本框，直接贴/改 Profile JSON，供接入未预置的新厂商。

> 这正好接上之前做的**命名引擎实例（EngineProfile）**：一个 EngineProfile = 选定的预设 + 填好的参数，可命名、可多份共存、可在"语音引擎"下拉切换。

---

## 九、三套预设示例

**DashScope Fun-ASR**
```jsonc
{
  "name": "阿里云 Fun-ASR",
  "regionUrls": {
    "beijing":  "wss://dashscope.aliyuncs.com/api-ws/v1/inference/",
    "singapore":"wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference/"
  },
  "auth": { "provider": "apiKeyHeader", "headerName": "Authorization", "scheme": "bearer" },
  "audio": { "mode": "Binary", "format": "pcm", "sampleRate": 16000 },
  "startMessageTemplate": "{\"header\":{\"action\":\"run-task\",\"task_id\":\"{task_id}\",\"streaming\":\"duplex\"},\"payload\":{\"task_group\":\"audio\",\"task\":\"asr\",\"function\":\"recognition\",\"model\":\"{model}\",\"parameters\":{\"format\":\"pcm\",\"sample_rate\":16000},\"input\":{}}}",
  "startAckEvent": "task-started",
  "stopMessageTemplate": "{\"header\":{\"action\":\"finish-task\",\"task_id\":\"{task_id}\",\"streaming\":\"duplex\"},\"payload\":{\"input\":{}}}",
  "result": {
    "eventPath": "header.event",
    "partialEvents": ["result-generated"],
    "finalEvents": [],
    "textPath": "payload.output.sentence.text",
    "finalFlagPath": "payload.output.sentence.sentence_end"
  },
  "error": { "eventValue": "task-failed", "messagePath": "header.error_message" }
}
```

**阿里云 NLS**
```jsonc
{
  "name": "阿里云 NLS",
  "regionUrls": {
    "cn-shanghai":   "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1",
    "ap-southeast-1":"wss://nls-gateway-ap-southeast-1.aliyuncs.com/ws/v1"
  },
  "auth": { "provider": "nlsToken", "headerName": "X-NLS-Token" },   // 内部用 AccessKey 签名
  "audio": { "mode": "Binary", "format": "pcm", "sampleRate": 16000 },
  "startMessageTemplate": "{\"header\":{\"message_id\":\"{message_id}\",\"task_id\":\"{task_id}\",\"namespace\":\"SpeechTranscriber\",\"name\":\"StartTranscription\",\"appkey\":\"{app_key}\"},\"payload\":{\"format\":\"pcm\",\"sample_rate\":16000,\"enable_intermediate_result\":true,\"enable_punctuation_prediction\":true}}",
  "startAckEvent": "TranscriptionStarted",
  "stopMessageTemplate": "{\"header\":{\"message_id\":\"{message_id}\",\"task_id\":\"{task_id}\",\"namespace\":\"SpeechTranscriber\",\"name\":\"StopTranscription\",\"appkey\":\"{app_key}\"}}",
  "result": {
    "eventPath": "header.name",
    "partialEvents": ["TranscriptionResultChanged"],
    "finalEvents": ["SentenceEnd"],
    "textPath": "payload.result",
    "finalFlagPath": null
  },
  "error": { "eventValue": "TaskFailed", "messagePath": "header.status_text" }
}
```

**OpenAI Realtime**（字段名以官方文档为准，落地前需联调校正）
```jsonc
{
  "name": "OpenAI Realtime",
  "regionUrls": { "default": "wss://api.openai.com/v1/realtime?intent=transcription" },
  "auth": {
    "provider": "apiKeyHeader", "headerName": "Authorization", "scheme": "Bearer",
    "extraHeaders": { "OpenAI-Beta": "realtime=v1" }
  },
  "audio": {
    "mode": "Base64Json", "format": "pcm16", "sampleRate": 16000,
    "messageTemplate": "{\"type\":\"input_audio_buffer.append\",\"audio\":\"{audio_base64}\"}"
  },
  "startMessageTemplate": "{\"type\":\"session.update\",\"session\":{\"input_audio_format\":\"pcm16\",\"input_audio_transcription\":{\"model\":\"{model}\"}}}",
  "startAckEvent": "session.updated",
  "stopMessageTemplate": null,
  "result": {
    "eventPath": "type",
    "partialEvents": ["conversation.item.input_audio_transcription.delta"],
    "finalEvents":   ["conversation.item.input_audio_transcription.completed"],
    "textPath": "transcript",
    "finalFlagPath": null
  },
  "error": { "eventValue": "error", "messagePath": "error.message" }
}
```

> 注：OpenAI 的中间结果在 `delta` 事件里字段可能是 `delta` 而非 `transcript`，最终结果在 `completed` 里是 `transcript`。若两事件文本字段不同名，`ResultSpec` 需要支持"按事件区分 textPath"——这是已知的一个小扩展（见风险）。

---

## 十、与现有代码的关系 & 落地步骤

1. **抽内核**：新建 `StreamingAsrEngine` + `StreamingAsrProfile` + 两个扩展点接口，把现有 `AliyunCloudRecognizer` 的连接/队列/PCM/收发循环搬进来（已验证可用，风险低）。
2. **迁移鉴权**：`AliyunTokenClient` 包成 `NlsTokenAuth`；新增 `ApiKeyHeaderAuth`。
3. **做两个 transport**：Binary（搬现有）、Base64Json（新写，简单）。
4. **内置三套预设**（上面 JSON），用 Option 下拉选择。
5. **新插件 `StreamingAsr`** 实现 `IRecognizer`，包一层 Engine。
6. **逐步弃用**原 AliyunCloud / LLMAudio 两个硬编码插件（保留一段时间向后兼容）。
7. **联调校正** OpenAI Realtime 预设字段。

每步都可独立编译/测试；建议先 1–4 跑通 DashScope（已知正确），再加 NLS、OpenAI。

---

## 十一、风险与取舍

- **"全数据驱动"的边界**：路径取值、模板渲染能覆盖绝大多数；但"同一结果不同事件用不同文本字段""逐字时间戳/情绪等额外结构""多步握手"会突破纯配置，需要在 `ResultSpec`/Hook 上做小扩展。建议**先支持到能跑三家，复杂结构按需加**，不要一开始追求万能。
- **模板易错**：手写 JSON 模板 + 占位符对普通用户门槛高。所以坚持**"预设为主、编辑为辅"**——普通用户永不碰模板。
- **协议版本漂移**：厂商会改字段（尤其 OpenAI 仍在演进）。预设内置版本号，便于随程序更新修正。
- **鉴权安全**：签名/密钥仅在内存与本地配置，不写日志；密码框输入。
- **测试**：每个预设最好有一段固定音频的回归用例；OpenAI 这类未联调的，先标注"实验性"。

---

## 十二、一句话总结

把"地址/鉴权/指令/取值"做成**数据（Profile）**，把"签名/音频模式/动态字段"做成**少数命名扩展点（代码）**，再用**预设 + 可编辑模板**两档暴露给用户。这样新增一家流式 ASR，多数情况只是加一份预设 JSON；遇到新签名或新音频结构，才加一小段代码。既解耦、可自定义，又不把协议复杂度甩给普通用户。
