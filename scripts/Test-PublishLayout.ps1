[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [double]$MaxSizeMB = 250,

    [int]$MaxFileCount = 600
)

$ErrorActionPreference = 'Stop'

$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$pluginRoot = Join-Path $publishRoot 'plugins'

if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'TMSpeech.exe') -PathType Leaf)) {
    throw "TMSpeech.exe was not found in '$publishRoot'."
}

if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) {
    throw "Plugin directory was not found in '$publishRoot'."
}

$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$pluginFiles = @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -File)
$sizeBytes = ($files | Measure-Object -Property Length -Sum).Sum
$sizeMB = [Math]::Round($sizeBytes / 1MB, 2)

$runtimeFiles = @(
    'System.Private.CoreLib.dll'
    'coreclr.dll'
    'hostfxr.dll'
    'hostpolicy.dll'
)

$pluginRuntimeCopies = @(
    $pluginFiles | Where-Object { $_.Name -in $runtimeFiles }
)

if ($pluginRuntimeCopies.Count -gt 0) {
    throw "Plugins contain .NET runtime files:`n$($pluginRuntimeCopies.FullName -join [Environment]::NewLine)"
}

foreach ($runtimeFile in @('System.Private.CoreLib.dll', 'coreclr.dll')) {
    $copies = @($files | Where-Object Name -eq $runtimeFile)
    if ($copies.Count -ne 1) {
        throw "Expected exactly one $runtimeFile in the published application, found $($copies.Count)."
    }
}

$expectedPlugins = @(
    'TMSpeech.AudioSource.Windows'
    'TMSpeech.Recognizer.AliyunCloud'
    'TMSpeech.Recognizer.Command'
    'TMSpeech.Recognizer.LLMAudio'
    'TMSpeech.Recognizer.SherpaNcnn'
    'TMSpeech.Recognizer.SherpaOnnx'
    'TMSpeech.Recognizer.StreamingAsr'
)

foreach ($plugin in $expectedPlugins) {
    $directory = Join-Path $pluginRoot $plugin
    foreach ($requiredFile in @("$plugin.dll", 'tmmodule.json')) {
        $path = Join-Path $directory $requiredFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required plugin file is missing: $path"
        }
    }
}

if ($sizeMB -gt $MaxSizeMB) {
    throw "Published application is $sizeMB MB, exceeding the $MaxSizeMB MB budget."
}

if ($files.Count -gt $MaxFileCount) {
    throw "Published application contains $($files.Count) files, exceeding the $MaxFileCount file budget."
}

Write-Output "Publish layout verification passed."
Write-Output "Total size: $sizeMB MB (budget: $MaxSizeMB MB)"
Write-Output "File count: $($files.Count) (budget: $MaxFileCount)"
Write-Output "Plugin files: $($pluginFiles.Count)"
