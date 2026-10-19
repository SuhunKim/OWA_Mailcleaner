param(
    [string]$Version = '1.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot 'Outlook_Mail_Cleaner.user.js'
$packageRoot = Join-Path $projectRoot 'distribution'
$extensionRoot = Join-Path $packageRoot 'extension'
$tampermonkeyRoot = Join-Path $packageRoot 'tampermonkey'
$manifestPath = Join-Path $extensionRoot 'manifest.json'
$extensionScriptPath = Join-Path $extensionRoot 'content.js'
$tampermonkeyPath = Join-Path $tampermonkeyRoot 'Outlook_Mail_Cleaner.user.js'
$readmePath = Join-Path $packageRoot 'README_설치안내.md'
$zipPath = Join-Path $projectRoot "Outlook_Mail_Cleaner_Distribution_v$Version.zip"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

foreach ($requiredPath in @($sourcePath, $manifestPath, $readmePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "필수 파일을 찾을 수 없습니다: $requiredPath"
    }
}

$source = [System.IO.File]::ReadAllText($sourcePath, [System.Text.Encoding]::UTF8)
$versionMatch = [regex]::Match($source, '(?m)^// @version\s+([^\r\n]+)\r?$')
if (-not $versionMatch.Success) {
    throw '사용자 스크립트에서 버전 정보를 찾을 수 없습니다.'
}
$sourceVersion = $versionMatch.Groups[1].Value.Trim()
if ($sourceVersion -ne $Version) {
    throw "사용자 스크립트 버전($sourceVersion)과 배포 버전($Version)이 다릅니다."
}

$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
if ($manifest.version -ne $Version) {
    throw "확장 프로그램 버전($($manifest.version))과 배포 버전($Version)이 다릅니다."
}

$readme = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
if ($readme -notmatch [regex]::Escape("OWA 메일 클리너 $Version")) {
    throw '설치 안내서의 버전이 배포 버전과 다릅니다.'
}

$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) {
    throw '자동 검증에 필요한 Node.js를 찾을 수 없습니다.'
}
& $node.Source --check $sourcePath
if ($LASTEXITCODE -ne 0) { throw '사용자 스크립트 구문 검사에 실패했습니다.' }
& $node.Source --test (Join-Path $projectRoot 'tests\core.test.js')
if ($LASTEXITCODE -ne 0) { throw '단위 테스트에 실패했습니다.' }

$content = [regex]::Replace($source, '(?s)^// ==UserScript==.*?// ==/UserScript==\r?\n*', '')
if ([string]::IsNullOrWhiteSpace($content)) {
    throw '확장 프로그램 실행 코드를 생성하지 못했습니다.'
}

New-Item -ItemType Directory -Path $extensionRoot -Force | Out-Null
New-Item -ItemType Directory -Path $tampermonkeyRoot -Force | Out-Null
[System.IO.File]::WriteAllText($extensionScriptPath, $content, $utf8NoBom)
[System.IO.File]::WriteAllText($tampermonkeyPath, $source, $utf8NoBom)

$generatedExtension = [System.IO.File]::ReadAllText($extensionScriptPath, [System.Text.Encoding]::UTF8)
$generatedTampermonkey = [System.IO.File]::ReadAllText($tampermonkeyPath, [System.Text.Encoding]::UTF8)
if ($generatedExtension -ne $content) { throw '확장 프로그램 실행 파일이 기준 소스와 일치하지 않습니다.' }
if ($generatedTampermonkey -ne $source) { throw 'Tampermonkey 설치본이 기준 소스와 일치하지 않습니다.' }

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
Write-Output $zipPath
