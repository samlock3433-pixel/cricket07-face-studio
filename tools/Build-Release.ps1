# Builds the shareable Face Studio package: release\FaceStudio-<version>-win-x64.zip
# Self-contained (no .NET install needed), English only, no debug files, local folder names removed from the
# template catalog, README + third-party notices + licence texts added.
param([string]$Version = '1.0')
$ErrorActionPreference = 'Stop'
$studio = Split-Path $PSScriptRoot -Parent
$out = Join-Path $studio 'release\FaceStudio'
$zip = Join-Path $studio "release\FaceStudio-$Version-win-x64.zip"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

# NuGet.Config clears package sources; the runtime packs come from nuget.org.
dotnet restore "$studio\src\FaceStudio.App" -r win-x64 -p:SelfContained=true --source https://api.nuget.org/v3/index.json | Out-Null
dotnet publish "$studio\src\FaceStudio.App" -c Release -r win-x64 --self-contained true --no-restore `
    -p:DebugType=None -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Get-ChildItem $out -Recurse -Include *.lib, *.pdb | Remove-Item -Force
if (Test-Path "$out\workspace") { Remove-Item -Recurse -Force "$out\workspace" }

# The catalog records where each template was extracted from on the author's disk; keep only the game-relative path.
$catalog = "$out\templates\catalog.json"
$text = [IO.File]::ReadAllText($catalog) -replace '"Source":\s*"[^"]*?(players\\\\[^"]*)"', '"Source":  "$1"'
[IO.File]::WriteAllText($catalog, $text, (New-Object Text.UTF8Encoding($true)))
if ($text -match '[A-Za-z]:\\\\') { throw 'An absolute path is still in the template catalog.' }

Copy-Item "$studio\packaging\README.txt", "$studio\packaging\THIRD-PARTY-NOTICES.txt" $out
Copy-Item "$studio\LICENSE" "$out\LICENSE.txt"
$lic = New-Item -ItemType Directory -Force "$out\licenses"
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'
$dotnet = Join-Path $env:ProgramFiles 'dotnet'
Copy-Item "$studio\vendor\otools\otools-release-0179\LICENSE - read before using OTools.txt" "$lic\OTools-LICENSE.txt"
Copy-Item "$nuget\helixtoolkit.wpf\2.27.0\LICENSE" "$lic\HelixToolkit-LICENSE.txt"
Copy-Item "$nuget\microsoft.ml.onnxruntime\1.20.1\LICENSE" "$lic\OnnxRuntime-LICENSE.txt"
Copy-Item "$nuget\microsoft.ml.onnxruntime\1.20.1\ThirdPartyNotices.txt" "$lic\OnnxRuntime-ThirdPartyNotices.txt"
Copy-Item "$dotnet\LICENSE.txt" "$lic\DotNet-LICENSE.txt"
Copy-Item "$dotnet\ThirdPartyNotices.txt" "$lic\DotNet-ThirdPartyNotices.txt"
# Apache 2.0 (MediaPipe face model): the full licence text as published inside ONNX Runtime's notices.
$notices = Get-Content "$nuget\microsoft.ml.onnxruntime\1.20.1\ThirdPartyNotices.txt"
$start = ($notices | Select-String -Pattern '^\s+Apache License\s*$' | Select-Object -First 1).LineNumber
$end = ($notices | Select-String -Pattern 'END OF TERMS AND CONDITIONS' | Select-Object -First 1).LineNumber
$notices[($start - 1)..($end - 1)] | Set-Content "$lic\Apache-2.0.txt" -Encoding UTF8

if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
'{0} ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB)
