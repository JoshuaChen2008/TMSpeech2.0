# TMSpeech contributor guide

## Scope and architecture

- `src/TMSpeech.Core`: configuration, plugin loading, resource management, and the job/session lifecycle.
- `src/TMSpeech.GUI`: Avalonia views and view models.
- `src/TMSpeech`: desktop entry point and plugin build/publish orchestration.
- `src/Plugins`: dynamically loaded audio-source and recognizer modules.
- `tests/TMSpeech.Core.RuntimeChecks`: offline executable behavior gate with no test-framework dependency.
- `docs/CodeMap.md`: ownership and change-routing map.

## Required workflow

1. Keep session generation and public job state changes inside `SessionLifecycle`/`JobManager`.
2. Reuse `StreamingAsrEngine` for ordinary duplex streaming ASR protocols; do not add another WebSocket worker/queue loop without documenting why its lifecycle differs.
3. Network behavior tests must use `IPAddress.Loopback` or injected delegates. Never require credentials, DNS, or a public endpoint.
4. After a behavioral change run:

   ```powershell
   dotnet build tests/TMSpeech.Core.RuntimeChecks/TMSpeech.Core.RuntimeChecks.csproj -c Debug --no-restore -m:1 -t:Rebuild
   dotnet run --project tests/TMSpeech.Core.RuntimeChecks/TMSpeech.Core.RuntimeChecks.csproj -c Debug --no-build -- all
   ```

5. A clean runtime-check rebuild must remain at zero compiler warnings. Fix asynchronous ownership; do not silence `CS4014` with a discarded task unless completion and failure are observed elsewhere.
6. Plugin dependencies must either appear in the plugin `.deps.json` or be shipped beside the entry DLL so `PluginLoadContext` can resolve them.

## Build notes

- `global.json` pins .NET SDK 10.0.301 so the existing C# 12 syntax compiles; projects still target .NET 6.
- The desktop project is self-contained. A full solution/publish command must specify a RID, for example `-r win-x64`.
- Generated `bin/` and `obj/` contents are not source and must not be committed.
