#requires -Version 5.1
<#
.SYNOPSIS
    构建单文件自包含、内嵌全量签名组件的多版本 CAD 一键安装程序：
    installer/dist/EngineeringPlatform-Setup.exe
#>
[CmdletBinding()]
param(
    [switch]$SkipComponentBuild,
    [string]$CertSubject = $(if ($env:EP_CODESIGN_SUBJECT) { $env:EP_CODESIGN_SUBJECT } else { "CN=EngineeringPlatform Dev" }),
    [string]$TimestampUrl = $env:EP_CODESIGN_TIMESTAMP
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$setupDir = Join-Path $repo "setup"
$payloadStage = Join-Path $setupDir "payload\_stage"
$payloadZip = Join-Path $setupDir "payload\payload.zip"
$distDir = Join-Path $repo "installer\dist"

function Invoke-CodeSign {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "[sign] 未找到 '$CertSubject' 证书，跳过签名：$Path"
        return
    }
    if ($TimestampUrl) {
        $sig = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampUrl -ErrorAction Stop
    } else {
        $sig = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop
    }
    Write-Host "[sign] $($sig.Status) -> $(Split-Path $Path -Leaf)"
}

if (-not $SkipComponentBuild) {
    Write-Host "[1/4] Building Host (net48 + net8.0-windows)..."
    dotnet build (Join-Path $repo "host\EngineeringHost.csproj") -c Release -v m
    if ($LASTEXITCODE -ne 0) { throw "Host build failed" }

    Write-Host "[2/4] Publishing Gateway (ep-mcp.exe)..."
    dotnet publish (Join-Path $repo "gateway\EpMcpGateway.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $repo "gateway\publish") -v m
    if ($LASTEXITCODE -ne 0) { throw "Gateway publish failed" }
}

Write-Host "[3/4] Staging signed payload into payload.zip..."
if (Test-Path $payloadStage) { Remove-Item -Recurse -Force $payloadStage }
if (Test-Path $payloadZip) { Remove-Item -Force $payloadZip }

$net48Dest = Join-Path $payloadStage "host\net48"
$net8Dest = Join-Path $payloadStage "host\net8"
$binDest = Join-Path $payloadStage "bin"
$solverDest = Join-Path $payloadStage "route_solver"
$instDest = Join-Path $payloadStage "installer"
New-Item -ItemType Directory -Force $net48Dest, $net8Dest, $binDest, $solverDest, $instDest | Out-Null

$forbidden = @("acmgd.dll", "acdbmgd.dll", "accoremgd.dll", "AecBaseMgd.dll", "AeccDbMgd.dll")

# net48 Host
$net48Src = Join-Path $repo "host\bin\Release\net48\EngineeringHost.dll"
if (-not (Test-Path $net48Src)) { throw "Missing $net48Src" }
Copy-Item -Force $net48Src (Join-Path $net48Dest "EngineeringHost.dll")
Invoke-CodeSign (Join-Path $net48Dest "EngineeringHost.dll")

# net8 Host + deps
$net8SrcDir = Join-Path $repo "host\bin\Release\net8.0-windows"
if (-not (Test-Path (Join-Path $net8SrcDir "EngineeringHost.dll"))) { throw "Missing net8 EngineeringHost.dll" }
Get-ChildItem $net8SrcDir -Filter "*.dll" | Where-Object { $forbidden -notcontains $_.Name } | ForEach-Object {
    Copy-Item -Force $_.FullName (Join-Path $net8Dest $_.Name)
}
Invoke-CodeSign (Join-Path $net8Dest "EngineeringHost.dll")

# Gateway ep-mcp.exe（签发产物同时保留于 gateway\publish 与 payload\bin）
$gwSrc = Join-Path $repo "gateway\publish\ep-mcp.exe"
if (-not (Test-Path $gwSrc)) { throw "Missing $gwSrc" }
Invoke-CodeSign $gwSrc
Copy-Item -Force $gwSrc (Join-Path $binDest "ep-mcp.exe")

# JTG D20 选线与经济造价引擎（融合进统一安装包）
$solverSrc = Join-Path $repo "route_solver"
if (Test-Path $solverSrc) {
    Get-ChildItem $solverSrc -File | Where-Object { $_.Extension -in @(".py", ".txt") } | ForEach-Object {
        Copy-Item -Force $_.FullName (Join-Path $solverDest $_.Name)
    }
}

# 帮助与维护指南（融合进统一安装包）
$helpSrc = Join-Path $repo "docs\HELP_GUIDE.md"
if (Test-Path $helpSrc) {
    $docsDest = Join-Path $payloadStage "docs"
    New-Item -ItemType Directory -Force $docsDest | Out-Null
    Copy-Item -Force $helpSrc (Join-Path $docsDest "使用与维护指南.md")
}

# Cert + headless smoke script
$cerSrc = Join-Path $repo "tools\codesign\EngineeringPlatformDev.cer"
if (Test-Path $cerSrc) { Copy-Item -Force $cerSrc (Join-Path $instDest "EngineeringPlatformDev.cer") }
$smokeSrc = Join-Path $repo "installer\smoke_headless.ps1"
if (Test-Path $smokeSrc) { Copy-Item -Force $smokeSrc (Join-Path $instDest "smoke_headless.ps1") }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadStage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $payloadStage
Write-Host "[pack] payload.zip -> $((Get-Item $payloadZip).Length) bytes"

Write-Host "[4/4] Publishing single-file EngineeringPlatform-Setup.exe..."
New-Item -ItemType Directory -Force $distDir | Out-Null
$pubOut = Join-Path $setupDir "publish"
if (Test-Path $pubOut) { Remove-Item -Recurse -Force $pubOut }

dotnet publish (Join-Path $setupDir "EngineeringPlatformSetup.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $pubOut -v m
if ($LASTEXITCODE -ne 0) { throw "Setup publish failed" }

$finalExe = Join-Path $distDir "EngineeringPlatform-Setup.exe"
Copy-Item -Force (Join-Path $pubOut "EngineeringPlatform-Setup.exe") $finalExe
Invoke-CodeSign $finalExe

$fi = Get-Item $finalExe
Write-Host "[OK] Built self-contained installer: $($fi.FullName) ($($fi.Length) bytes)"
