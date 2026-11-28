# ============================================================
# WorkHub Desktop — 원클릭 인스톨러 빌드 스크립트
#
# 동작:
#   1. 본체는 .NET 10 framework-dependent, 업데이터는 self-contained로 게시
#   2. Inno Setup 6 (iscc.exe) 호출하여 .exe 인스톨러 생성
#
# 산출물:
#   publish\release\Portfolio.OfficeSystem-Setup-{version}.exe
#
# 선결 조건:
#   - .NET 10 SDK (dotnet 명령 사용 가능)
#   - Inno Setup 6   (winget install JRSoftware.InnoSetup 으로 설치 가능)
# ============================================================

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FullOnly,    # FULL(.exe 인스톨러)만 생성 (패치 zip 생략)
    [switch]$PatchOnly    # PATCH(.zip)만 생성 (Inno Setup 생략)
)

if ($FullOnly -and $PatchOnly) { throw "-FullOnly 와 -PatchOnly 는 동시에 쓸 수 없습니다." }

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

# 버전 추출 (csproj <Version>) — 패치 zip 이름/로그에 사용
$csprojPath = Join-Path $projectRoot "WorkSystemWindow.csproj"
$version = (Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1).Matches.Groups[1].Value
if (-not $version) { $version = "0.0.0" }
$mode = if ($FullOnly) { "FULL only" } elseif ($PatchOnly) { "PATCH only" } else { "FULL + PATCH" }
Write-Host "==> 빌드 모드: $mode (v$version)"

Write-Host "==> 1. 이전 빌드 산출물 정리"
$payload = Join-Path $projectRoot "publish\installer-payload"
$baseline = Join-Path $projectRoot "publish\baseline-payload"
$patchStage = Join-Path $projectRoot "publish\patch-stage"
$release = Join-Path $projectRoot "publish\release"
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
if (-not (Test-Path $release)) { New-Item -ItemType Directory -Path $release | Out-Null }

function Get-RelativeFileMap {
    param([string]$Root)
    $map = @{}
    if (-not (Test-Path $Root)) { return $map }
    $rootResolved = (Resolve-Path $Root).Path.TrimEnd('\')
    Get-ChildItem -Path $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootResolved.Length).TrimStart('\')
        $map[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Test-PatchExcludedFile {
    param([string]$RelativePath)
    $fileName = Split-Path -Leaf $RelativePath
    return $fileName -ieq "Portfolio.OfficeSystem.Updater.exe" -or $fileName -ieq "Portfolio.OfficeSystem.Updater.pdb"
}

function New-PatchPackage {
    param(
        [string]$CurrentRoot,
        [string]$BaselineRoot,
        [string]$OutputZip
    )

    if (-not (Test-Path $BaselineRoot)) {
        Write-Host "    baseline 이 없어 패치 패키지를 만들지 않습니다."
        return $false
    }

    $currentMap = Get-RelativeFileMap -Root $CurrentRoot
    $baselineMap = Get-RelativeFileMap -Root $BaselineRoot
    $changed = New-Object System.Collections.Generic.List[string]
    $removed = New-Object System.Collections.Generic.List[string]

    foreach ($entry in $currentMap.GetEnumerator()) {
        if (Test-PatchExcludedFile -RelativePath $entry.Key) { continue }
        if (-not $baselineMap.ContainsKey($entry.Key) -or $baselineMap[$entry.Key] -ne $entry.Value) {
            $changed.Add($entry.Key)
        }
    }

    foreach ($entry in $baselineMap.GetEnumerator()) {
        if (Test-PatchExcludedFile -RelativePath $entry.Key) { continue }
        if (-not $currentMap.ContainsKey($entry.Key)) {
            $removed.Add($entry.Key)
        }
    }

    if ($changed.Count -eq 0 -and $removed.Count -eq 0) {
        Write-Host "    변경된 파일이 없어 패치 패키지를 만들지 않습니다."
        return $false
    }

    if (Test-Path $patchStage) { Remove-Item $patchStage -Recurse -Force }
    New-Item -ItemType Directory -Path $patchStage | Out-Null

    foreach ($relative in $changed) {
        $source = Join-Path $CurrentRoot $relative
        $target = Join-Path $patchStage $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
        Copy-Item $source -Destination $target -Force
    }

    if ($removed.Count -gt 0) {
        $removed | Set-Content -Path (Join-Path $patchStage 'removed.txt') -Encoding utf8
    }

    if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($patchStage, $OutputZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $patchStage -Recurse -Force
    return $true
}

Write-Host "==> 2. 메인 앱 게시 (framework-dependent, $Runtime, $Configuration)"
& dotnet publish -c $Configuration -r $Runtime --self-contained false -p:PublishSingleFile=false -o $payload
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (exit=$LASTEXITCODE)" }

Write-Host "==> 2-1. 업데이트 도우미 게시 (self-contained single file)"
$updaterPayload = Join-Path $projectRoot "publish\updater-payload"
if (Test-Path $updaterPayload) { Remove-Item $updaterPayload -Recurse -Force }
& dotnet publish (Join-Path $projectRoot "updater\Portfolio.OfficeSystem.Updater.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $updaterPayload
if ($LASTEXITCODE -ne 0) { throw "Updater publish 실패 (exit=$LASTEXITCODE)" }

$updaterFiles = @(
    "Portfolio.OfficeSystem.Updater.exe",
    "Portfolio.OfficeSystem.Updater.pdb"
)
foreach ($file in $updaterFiles) {
    $source = Join-Path $updaterPayload $file
    if (Test-Path $source) {
        Copy-Item $source -Destination (Join-Path $payload $file) -Force
        $pendingName = $file -replace '\.(exe|pdb)$', '.pending.$1'
        Copy-Item $source -Destination (Join-Path $payload $pendingName) -Force
    }
}

$exeSize = (Get-Item (Join-Path $payload "Portfolio.OfficeSystem.exe")).Length / 1MB
$totalSize = (Get-ChildItem $payload -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("    payload 총 크기 = {0:F1} MB (실행파일 {1:F1} MB)" -f $totalSize, $exeSize)

if (-not $PatchOnly) {
    Write-Host "==> 3. Inno Setup 컴파일 (FULL .exe)"
    $isccCandidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\iscc.exe",
        "C:\Program Files (x86)\Inno Setup 6\iscc.exe",
        "C:\Program Files\Inno Setup 6\iscc.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6이 설치되지 않았습니다. 'winget install JRSoftware.InnoSetup' 으로 설치하세요."
    }
    Write-Host "    iscc = $iscc"

    $iss = Join-Path $PSScriptRoot "Portfolio.OfficeSystem.iss"
    & "$iscc" "/DMyAppVersion=$version" "$iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 빌드 실패 (exit=$LASTEXITCODE)" }

    $setup = Get-ChildItem (Join-Path $release "Portfolio.OfficeSystem-Setup-*.exe") | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $setup) { throw "인스톨러 산출물이 생성되지 않았습니다." }
    Write-Host ("    FULL 완료: {0} ({1:F1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
} else {
    Write-Host "==> 3. (PatchOnly) Inno Setup 생략"
}

if (-not $FullOnly) {
    Write-Host "==> 4. 패치 패키지 생성 (PATCH .zip — 직전 baseline 대비 변경분)"
    $patchZip = Join-Path $release "Portfolio.OfficeSystem-Patch-$version.zip"
    if (New-PatchPackage -CurrentRoot $payload -BaselineRoot $baseline -OutputZip $patchZip) {
        $patchInfo = Get-Item $patchZip
        Write-Host ("    PATCH 완료: {0} ({1:F1} MB)" -f $patchInfo.FullName, ($patchInfo.Length / 1MB))
    } else {
        Write-Host "    (변경분 없음 또는 baseline 없음 — 패치 zip 미생성. 첫 빌드는 FULL만 배포하세요.)"
    }
} else {
    Write-Host "==> 4. (FullOnly) 패치 생성 생략"
}

# 다음 패치 diff 기준점 갱신 (현재 payload 를 baseline 으로 보관)
if (Test-Path $baseline) { Remove-Item $baseline -Recurse -Force }
Copy-Item $payload $baseline -Recurse
Write-Host "==> 완료. baseline 갱신됨 (다음 빌드의 패치 기준)."
