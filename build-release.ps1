# Builds the mod and packs a release zip into dist\. Extracting the zip into the game folder installs the mod.
#   .\build-release.ps1 [-GameDir "X:\path\to\Just Shapes & Beats"]
# Without -GameDir, the game path comes from GamePath.user.props or the default Steam location (GameReferences.props).
param(
    [string]$GameDir = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\JSABArchipelago\JSABArchipelago.csproj"
[xml]$csproj = Get-Content $project
$version = @($csproj.Project.PropertyGroup | Where-Object { $_.Version })[0].Version
if (-not $version) { throw "No <Version> in $project" }

$buildArgs = @($project, "-c", "Release", "-nologo")
if ($GameDir) { $buildArgs += "-p:JSABGameDir=$GameDir" }
dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$out = Join-Path $root "src\JSABArchipelago\bin\Release\net6.0"
$sources = @("JSABArchipelago.dll", "Archipelago.MultiClient.Net.dll", "Newtonsoft.Json.dll" | ForEach-Object { Join-Path $out $_ })
$sources += Join-Path $root "LICENSE"

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "JSABArchipelago-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue

# Written entry by entry: Compress-Archive in Windows PowerShell stores backslash paths, which some extractors unpack
# as one file named "BepInEx\plugins\..." instead of folders.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($source in $sources) {
        $entry = "BepInEx/plugins/JSABArchipelago/" + (Split-Path $source -Leaf)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $source, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Wrote $zip"
