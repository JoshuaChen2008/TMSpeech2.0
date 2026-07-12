# Offline behavior checks

This executable is TMSpeech2.0's dependency-free behavior test gate. It covers
session lifecycle, retry, failure propagation, and WebSocket shutdown behavior
without contacting an external service.

All socket-based checks bind a random port on `IPAddress.Loopback`; tests must not
use DNS names, public addresses, API credentials, or downloaded fixtures. Protocol
behavior should otherwise be injected through internal constructors and delegates.

The application projects target .NET 6, while the repository `global.json` pins
the .NET SDK to 10.0.301. This keeps local and automated builds on the same SDK
feature band while preserving the application's existing target framework.

Run from the repository root after dependencies have been restored:

```powershell
dotnet run --project tests/TMSpeech.Core.RuntimeChecks/TMSpeech.Core.RuntimeChecks.csproj --no-restore -- all
```

The process exits `0` only when every check passes. This project is included in
`TMSpeech.sln`, so compile failures are caught by normal solution builds.
