# Offline behavior checks

This executable is the repository's dependency-free behavior test gate. It covers
session lifecycle, retry, failure propagation, and WebSocket shutdown behavior
without contacting an external service.

All socket-based checks bind a random port on `IPAddress.Loopback`; tests must not
use DNS names, public addresses, API credentials, or downloaded fixtures. Protocol
behavior should otherwise be injected through internal constructors and delegates.

The repository `global.json` pins the .NET 6 SDK used by the application. This
prevents newer SDK end-of-life diagnostics from changing the build result.

Run after the repository has been restored:

```powershell
dotnet run --project tests/TMSpeech.Core.RuntimeChecks/TMSpeech.Core.RuntimeChecks.csproj --no-restore -- all
```

The process exits `0` only when every check passes. This project is included in
`TMSpeech.sln`, so compile failures are caught by normal solution builds.
