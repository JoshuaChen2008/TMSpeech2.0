using System.Reflection;

namespace TMSpeech.GUI;

internal static class BuildVersionInfo
{
#if SOURCE_ARCHIVE_BUILD
    public static string Version =>
        typeof(BuildVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.1.0-local";

    public const string InternalVersion = "source archive";
#else
    public static string Version => GitVersionInformation.FullSemVer;

    public static string InternalVersion =>
        GitVersionInformation.ShortSha +
        (GitVersionInformation.UncommittedChanges != "0" ? " (dirty)" : "");
#endif
}
