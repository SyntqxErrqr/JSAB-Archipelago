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
$stage = Join-Path $root "dist\stage"
$plugin = Join-Path $stage "BepInEx\plugins\JSABArchipelago"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $plugin | Out-Null
foreach ($file in "JSABArchipelago.dll", "Archipelago.MultiClient.Net.dll", "Newtonsoft.Json.dll") {
    Copy-Item (Join-Path $out $file) $plugin
}
Copy-Item (Join-Path $root "LICENSE") $plugin

$zip = Join-Path $root "dist\JSABArchipelago-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item -Recurse -Force $stage
Write-Host "Wrote $zip"
