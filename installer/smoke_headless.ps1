#requires -Version 5.1
<#
.SYNOPSIS
    v3.0 秒级无头冒烟自检（accoreconsole.exe + 签名校验 + Roslyn 数据库读写 + ep-mcp.exe 校验）。
#>
[CmdletBinding()]
param(
    [string]$AcCorePath = "",
    [string]$TestDwg = "",
    [string]$HostDll = ""
)

$ErrorActionPreference = "Stop"
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

if ([string]::IsNullOrWhiteSpace($HostDll)) {
    $HostDll = "$env:APPDATA\Autodesk\ApplicationPlugins\EngineeringPlatform.bundle\Contents\Windows\net8\EngineeringHost.dll"
}
$hostDll = $HostDll
$gatewayExe = "$env:LOCALAPPDATA\EngineeringPlatform\bin\ep-mcp.exe"

Write-Host "=== [1/4] Code Signature Check ===" -ForegroundColor Cyan
if (-not (Test-Path $hostDll)) { throw "Missing EngineeringHost.dll at $hostDll" }
$sig = Get-AuthenticodeSignature $hostDll
Write-Host ("  EngineeringHost.dll -> Status={0}" -f $sig.Status)
if ($sig.Status -ne "Valid") { throw "EngineeringHost.dll signature is not Valid: $($sig.Status)" }

Write-Host "=== [2/4] Gateway Binary Check ===" -ForegroundColor Cyan
if (-not (Test-Path $gatewayExe)) {
    $fallbackGw = Join-Path (Split-Path -Parent $PSScriptRoot) "gateway\publish\ep-mcp.exe"
    if (Test-Path $fallbackGw) { $gatewayExe = $fallbackGw }
}
if (Test-Path $gatewayExe) {
    $gwVer = & $gatewayExe --version
    Write-Host ("  ep-mcp.exe -> {0}" -f $gwVer)
} else {
    Write-Host "  [WARN] ep-mcp.exe not installed yet" -ForegroundColor Yellow
}

Write-Host "=== [3/4] Locate accoreconsole.exe ===" -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($AcCorePath)) {
    $candidates = @(
        "D:\AutoCAD\AotoCAD Civil\2025\AutoCAD 2025\accoreconsole.exe",
        "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe",
        "D:\AutoCAD\AotoCAD Civil\AutoCAD 2024\accoreconsole.exe",
        "C:\Program Files\Autodesk\AutoCAD 2024\accoreconsole.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $AcCorePath = $c; break }
    }
}
if ([string]::IsNullOrWhiteSpace($AcCorePath) -or -not (Test-Path $AcCorePath)) {
    Write-Host "  [SKIP] accoreconsole.exe not found; fallback to GUI verification." -ForegroundColor Yellow
    exit 0
}
Write-Host ("  Using: {0}" -f $AcCorePath)

if ([string]::IsNullOrWhiteSpace($TestDwg) -or -not (Test-Path $TestDwg)) {
    $TestDwg = "$env:TEMP\ep-base-write-test.dwg"
}

Write-Host "=== [4/4] Headless accoreconsole Roslyn + DB Read/Write Smoke Test ===" -ForegroundColor Cyan
$runDir = Join-Path $env:TEMP ("ep_smoke_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

try {
    $payloadPath = Join-Path $runDir "payload.json"
    $resultPath  = Join-Path $runDir "result.json"
    $scriptPath  = Join-Path $runDir "verify.scr"

    $code = @'
var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
int before = 0;
foreach (ObjectId id in ms) { before++; }
var pt = new DBPoint(new Point3d(500123.45, 3100456.78, 125.5));
ms.AppendEntity(pt);
tr.AddNewlyCreatedDBObject(pt, true);
log("headless_db_rw_ok:before=" + before + ",handle=" + pt.Handle.ToString());
return "OK:" + before + ":" + pt.Handle.ToString();
'@

    $payloadObj = @{
        code = $code
        args = @{}
        resultPath = $resultPath
    }
    $payloadObj | ConvertTo-Json -Depth 4 | Set-Content -Path $payloadPath -Encoding UTF8

    $dllForScr = $hostDll.Replace("\", "/")
    $payForScr = $payloadPath.Replace("\", "/")
    $scr = @(
        "_.NETLOAD"
        "`"$dllForScr`""
        "ENG_SHADOW_EVAL"
        "`"$payForScr`""
        "_.QUIT"
        "_N"
    ) -join "`r`n"
    Set-Content -Path $scriptPath -Value ($scr + "`r`n") -Encoding ASCII

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $acDir = Split-Path -Parent $AcCorePath
    $isCivil = (Test-Path (Join-Path $acDir "C3D\AeccDbMgd.dll")) -or (Test-Path (Join-Path $acDir "AeccDbMgd.dll"))
    $argsList = @()
    if ($isCivil) { $argsList += @("/p", "<<C3D_Metric>>") }
    if (Test-Path $TestDwg) { $argsList += @("/i", $TestDwg) }
    $argsList += @("/s", $scriptPath)
    & $AcCorePath @argsList | Out-Null
    $ec = $LASTEXITCODE
    $sw.Stop()

    if (-not (Test-Path $resultPath)) {
        throw "Headless smoke test failed: result.json not generated (accoreconsole exit=$ec)"
    }
    $res = Get-Content $resultPath -Raw | ConvertFrom-Json
    Write-Host ("  accoreconsole exit={0} elapsed={1}ms ok={2} engine={3} compile={4}ms exec={5}ms ret={6}" -f `
        $ec, $sw.ElapsedMilliseconds, $res.ok, $res.engine, $res.compileMs, $res.executeMs, $res.returnValue) -ForegroundColor Green
    if (-not $res.ok) {
        throw ("Headless Roslyn smoke failed: " + $res.error)
    }
}
finally {
    Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue
}

$swTotal.Stop()
Write-Host ("=== PASS: Headless verification finished in {0}ms ===" -f $swTotal.ElapsedMilliseconds) -ForegroundColor Green
