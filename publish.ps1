# =====================================================
# LuciLink 빌드 & 패키징 스크립트
# 
# 사전 요구사항:
#   1. .NET 9 SDK: winget install Microsoft.DotNet.SDK.9
#   2. Velopack CLI: dotnet tool install -g vpk
# =====================================================

param (
    [string]$Version = "1.0.0",
    [switch]$SkipVelopack
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$ClientProject = Join-Path $ProjectDir "LuciLink.Client\LuciLink.Client.csproj"
$PublishDir = Join-Path $ProjectDir "publish"
$ReleaseDir = Join-Path $ProjectDir "releases"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  LuciLink Build & Package v$Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ===== 1. 클린 빌드 =====
Write-Host "`n[1/4] Cleaning previous build..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

# ===== 2. Publish (Self-contained, win-x64) =====
Write-Host "[2/4] Publishing self-contained build..." -ForegroundColor Yellow
dotnet publish $ClientProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $PublishDir `
    /p:Version=$Version `
    /p:AssemblyVersion="${Version}.0" `
    /p:FileVersion="${Version}.0"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build output: $PublishDir" -ForegroundColor Green

# ===== 3. 네이티브 파일 확인 =====
Write-Host "[3/4] Verifying native files..." -ForegroundColor Yellow
$requiredFiles = @(
    "Resources\adb.exe",
    "Resources\scrcpy-server.jar",
    "ffmpeg\avcodec-62.dll",
    "ffmpeg\avutil-60.dll",
    "ffmpeg\swscale-9.dll"
)

$missing = @()
foreach ($f in $requiredFiles) {
    $fullPath = Join-Path $PublishDir $f
    if (-not (Test-Path $fullPath)) {
        $missing += $f
    }
}

if ($missing.Count -gt 0) {
    Write-Host "Missing files:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Copy these files manually to $PublishDir" -ForegroundColor Yellow
} else {
    Write-Host "All native files present!" -ForegroundColor Green
}

# ===== 4. Velopack 패키징 (인스톨러 + 업데이트 패키지) =====
if (-not $SkipVelopack) {
    Write-Host "[4/4] Creating Velopack package..." -ForegroundColor Yellow
    
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        Write-Host "Installing Velopack CLI..." -ForegroundColor Yellow
        dotnet tool install -g vpk
    }
    
    if (-not (Test-Path $ReleaseDir)) { New-Item $ReleaseDir -ItemType Directory | Out-Null }
    
    vpk pack `
        --packId "LuciLink" `
        --packVersion $Version `
        --packDir $PublishDir `
        --mainExe "LuciLink.exe" `
        --outputDir $ReleaseDir `
        --packTitle "LuciLink" `
        --packAuthors "Lucitella" `
        --icon "LuciLink.Client\Resources\App.ico"
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "`nVelopack package created!" -ForegroundColor Green
        Write-Host "Output: $ReleaseDir" -ForegroundColor Green
        Write-Host "`nFiles:" -ForegroundColor Cyan
        Get-ChildItem $ReleaseDir | ForEach-Object {
            Write-Host "  $($_.Name) ($([math]::Round($_.Length / 1MB, 1)) MB)" -ForegroundColor White
        }
    } else {
        Write-Host "Velopack packaging failed." -ForegroundColor Red
    }
} else {
    Write-Host "[4/4] Skipping Velopack (use -SkipVelopack to skip)" -ForegroundColor Yellow
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Done!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. Test: Run $PublishDir\LuciLink.exe" -ForegroundColor White
Write-Host "  2. Upload: Push releases/ files to GitHub Releases" -ForegroundColor White
Write-Host "  3. Distribute: Share the Setup installer" -ForegroundColor White
