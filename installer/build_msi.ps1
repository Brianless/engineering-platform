#requires -Version 5.1
<#
.SYNOPSIS
    一键构建 v3.0 商业交付包：
    1) 双目标编译 EngineeringHost.dll (net48 + net8.0-windows)
    2) 单文件自包含发布 ep-mcp.exe (.NET 8 win-x64, 零 Python 依赖)
    3) 自动代码签名（CN=EngineeringPlatform Dev）
    4) WiX v4 编译 EngineeringPlatform-v3.0.msi
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $PSScriptRoot "stage"
$dist  = Join-Path $PSScriptRoot "dist"
$msiOut = Join-Path $dist "EngineeringPlatform-v3.0.msi"

function Invoke-Sign([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq "CN=EngineeringPlatform Dev" } | Select-Object -First 1
    if (-not $cert) { return }
    $sig = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $cert -HashAlgorithm SHA256 -ErrorAction SilentlyContinue
    if ($sig -and $sig.Status -eq "Valid") {
        Write-Host ("[sign] Valid -> " + (Split-Path $Path -Leaf)) -ForegroundColor Green
    }
}

if (-not $SkipBuild) {
    Write-Host "[1/4] Building EngineeringHost (net48 + net8.0-windows)..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repo "host\EngineeringHost.csproj") -c Release -v m
    if ($LASTEXITCODE -ne 0) { throw "EngineeringHost build failed" }

    Write-Host "[2/4] Publishing standalone ep-mcp.exe (win-x64 single-file)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo "gateway\EpMcpGateway.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $repo "gateway\publish") -v m
    if ($LASTEXITCODE -ne 0) { throw "ep-mcp publish failed" }
}

Write-Host "[3/4] Staging signed files for WiX MSI..." -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage "bundle\Contents\Windows\net8") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "net8_deps") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "bin") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "installer") -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$forbidden = @("acmgd.dll", "acdbmgd.dll", "accoremgd.dll", "AecBaseMgd.dll", "AeccDbMgd.dll")

Copy-Item (Join-Path $repo "tools\bundle\EngineeringPlatform.bundle\PackageContents.xml") (Join-Path $stage "bundle\PackageContents.xml") -Force

$net48Dll = Join-Path $repo "host\bin\Release\net48\EngineeringHost.dll"
$net8Dir  = Join-Path $repo "host\bin\Release\net8.0-windows"
$net8Dll  = Join-Path $net8Dir "EngineeringHost.dll"
$gwExe    = Join-Path $repo "gateway\publish\ep-mcp.exe"

Copy-Item $net48Dll (Join-Path $stage "bundle\Contents\Windows\EngineeringHost.dll") -Force
Copy-Item $net8Dll  (Join-Path $stage "bundle\Contents\Windows\net8\EngineeringHost.dll") -Force
Copy-Item $gwExe    (Join-Path $stage "bin\ep-mcp.exe") -Force
Copy-Item (Join-Path $repo "tools\codesign\EngineeringPlatformDev.cer") (Join-Path $stage "installer\EngineeringPlatformDev.cer") -Force
Copy-Item (Join-Path $PSScriptRoot "smoke_headless.ps1") (Join-Path $stage "installer\smoke_headless.ps1") -Force

Get-ChildItem $net8Dir -Filter "*.dll" | Where-Object {
    $_.Name -ne "EngineeringHost.dll" -and ($forbidden -notcontains $_.Name)
} | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $stage "net8_deps\$($_.Name)") -Force
}

Invoke-Sign (Join-Path $stage "bundle\Contents\Windows\EngineeringHost.dll")
Invoke-Sign (Join-Path $stage "bundle\Contents\Windows\net8\EngineeringHost.dll")
Invoke-Sign (Join-Path $stage "bin\ep-mcp.exe")

# Generate WiX v4 Net8Deps.wxs for all Roslyn/.NET 8 runtime dependencies
$depsWxs = Join-Path $stage "Net8Deps.wxs"
$depLines = @(
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
    '  <Fragment>',
    '    <ComponentGroup Id="Net8Deps" Directory="Net8Dir">'
)
$depIdx = 0
Get-ChildItem (Join-Path $stage "net8_deps") -Filter "*.dll" | Sort-Object Name | ForEach-Object {
    $depIdx++
    $compId = "Net8Dep_$depIdx"
    $regName = "Net8Dep_$depIdx"
    $compGuid = "91A2B3C4-2222-4AAA-8BBB-" + $depIdx.ToString("D12")
    $srcPath = $_.FullName
    $depLines += "      <Component Id=`"$compId`" Guid=`"$compGuid`">"
    $depLines += "        <File Source=`"$srcPath`" />"
    $depLines += "        <RegistryValue Root=`"HKCU`" Key=`"Software\EngineeringPlatform\v3\Deps`" Name=`"$regName`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />"
    $depLines += "      </Component>"
}
$depLines += @(
    '    </ComponentGroup>',
    '  </Fragment>',
    '</Wix>'
)
Set-Content -Path $depsWxs -Value ($depLines -join "`r`n") -Encoding UTF8

Write-Host "[4/4] Building WiX v4 MSI -> $msiOut" -ForegroundColor Cyan
& wix build (Join-Path $PSScriptRoot "EngineeringPlatform.wxs") $depsWxs -d "StageDir=$stage" -arch x64 -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed" }

$msiInfo = Get-Item $msiOut
Write-Host ("[OK] Built MSI: {0} ({1:N0} bytes)" -f $msiInfo.FullName, $msiInfo.Length) -ForegroundColor Green
