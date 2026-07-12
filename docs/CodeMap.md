# Code map

| Change | Primary owner | Related verification |
| --- | --- | --- |
| Start, pause, stop, plugin failure | `src/TMSpeech.Core/JobManager.cs`, `src/TMSpeech.Core/SessionLifecycle.cs` | lifecycle and failure rollback checks |
| Plugin discovery and dependency loading | `src/TMSpeech.Core/Plugins/PluginManager.cs` | adjacent managed dependency smoke |
| Generic streaming ASR | `src/Plugins/TMSpeech.Recognizer.StreamingAsr/StreamingAsrEngine.cs` | loopback close/error/normal-stop checks |
| Aliyun NLS adapter | `src/Plugins/TMSpeech.Recognizer.AliyunCloud/AliyunCloudRecognizer.cs` | shared-engine build and isolated load smoke |
| DashScope/Fun-ASR lifecycle | `src/Plugins/TMSpeech.Recognizer.LLMAudio/LLMAudioRecognizer.cs` | retry and worker reconnect checks |
| DashScope protocol JSON | `src/Plugins/TMSpeech.Recognizer.LLMAudio/LLMAudioProtocol.cs` | offline protocol check |
| Float audio conversion and RMS | `src/Plugins/TMSpeech.Recognizer.LLMAudio/AudioFrameConverter.cs` | offline frame conversion check |
| Plugin download/install | `src/TMSpeech.Core/Services/Resource/DownloadManager.cs` | warning-free Core rebuild |
| Plugin packaging | `src/TMSpeech/TMSpeech.csproj` | RID-specific publish and duplicate-file inspection |
| UI behavior | `src/TMSpeech.GUI/ViewModels`, `src/TMSpeech.GUI/Views`, `src/TMSpeech.GUI/Controls` | build plus desktop smoke test |

## Runtime flow

`JobManager` owns one generation at a time. It loads the configured `IAudioSource` and `IRecognizer`, wires events, starts both, and publishes `Running` only after resources and the timer exist. A plugin failure schedules one asynchronous stop for that generation; stale generations cannot stop a newer session.

Audio normally flows `IAudioSource.DataAvailable → JobManager → IRecognizer.Feed`. Streaming NLS protocols convert float samples to PCM16 and maintain a capped, drop-oldest backlog. `StreamingAsrEngine` owns connection, receive loop, serialized sends, backpressure, and terminal failure signaling. LLMAudio retains a specialized reconnect/VAD session loop, while its audio conversion and protocol serialization live in focused helpers.

## High-risk invariants

- Never invoke plugin `Stop` from the plugin's own receive/worker callback stack.
- Stop audio source and recognizer independently so one failure cannot skip the other cleanup.
- Publish status events only after internal state is ready for synchronous re-entry.
- Treat plugin load contexts as isolated: `TMSpeech.Core` comes from the default context; companion DLLs come from the plugin directory.
- Publish cleanup must not delete the entry assembly or a plugin's unique dependencies.
