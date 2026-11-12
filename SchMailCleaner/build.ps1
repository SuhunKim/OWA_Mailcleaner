# 순천향 메일클리너 배포 빌드: 단일 EXE(.NET 런타임 포함)를 만듭니다.
# 사용: PowerShell에서  .\build.ps1   (결과: .\publish\SchMailCleaner.exe)
# 필요: .NET 8 SDK, 최초 1회 NuGet 접근(런타임 팩 복원)
param([string]$Output = (Join-Path $PSScriptRoot "publish"))

dotnet publish (Join-Path $PSScriptRoot "SchMailCleaner.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $Output
if ($LASTEXITCODE -ne 0) { Write-Error "빌드 실패 (종료 코드 $LASTEXITCODE)"; exit $LASTEXITCODE }
# 단일 EXE만 배포합니다(디버그 심볼·참조 XML 제외).
Get-ChildItem $Output -Include *.pdb, *.xml -Recurse | Remove-Item -Force
Get-ChildItem $Output | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
