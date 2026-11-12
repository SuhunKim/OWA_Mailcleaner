$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageRoot = Join-Path $PSScriptRoot "artifacts\probe-$stamp"
if (Test-Path -LiteralPath $packageRoot) { throw 'Output already exists.' }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'OwaDesktopProbe.csproj') -c Release --no-restore --self-contained false -o $packageRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. No package was produced.' }
foreach ($name in @('OwaDesktopProbe.exe','OwaDesktopProbe.dll','OwaDesktopProbe.runtimeconfig.json','probe.js','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll','runtimes\win-x64\native\WebView2Loader.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot $name))) { throw "Missing publish output: $name" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'IMPLEMENTATION-PREP.md') -Destination $packageRoot
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TEST-RESULTS.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST-RESULTS.md') -Destination $packageRoot
}
$base = $packageRoot.TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash, $_.FullName.Substring($base.Length)
} | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Encoding utf8
$zipPath = "$packageRoot.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath
Write-Output "Portable validation package: $zipPath"
Write-Output 'Requires .NET 8 Windows Desktop Runtime and WebView2 Runtime. Not a production cleaner.'
