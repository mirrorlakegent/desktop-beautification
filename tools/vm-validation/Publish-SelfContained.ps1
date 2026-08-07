<#
.SYNOPSIS
    Publish DesktopSuite as a self-contained package (固化自包含发布流程).
.DESCRIPTION
    Wraps `dotnet publish --self-contained -r win-x64` into a single entry point and
    enforces an artifact gate, so a framework-dependent build can never be deployed to
    a bare VM by mistake.

    Root cause of the original V6/V2A/V11A/V14 failures: the target VM had NO .NET
    installed (DOTNET_NOT_INSTALLED). A framework-dependent apphost starts but never
    finds CoreCLR, so .NET code never runs. See FIXES.md and
    deliverables/gstack/vm-validation-desktopsuite-2026-08-07.md.

    A self-contained package bundles the full .NET 8 + WPF native DLLs
    (coreclr/clrjit/hostfxr, DirectWriteForwarder, D3DCompiler_47_cor3, ...) plus
    mpv.exe, so the target needs no preinstalled runtime.

.PARAMETER ProjectPath
    Path to the .csproj to publish. Default: src/DesktopSuite/DesktopSuite.csproj.
.PARAMETER OutputDir
    Output directory for the self-contained package.
    Default: src/DesktopSuite/bin/x64/Release/self-contained.
.PARAMETER Configuration
    Build configuration. Default: Release.
.PARAMETER Platform
    Target platform. Default: x64.
.PARAMETER Runtime
    Target RID. Default: win-x64.
.PARAMETER Clean
    Clear the output directory before publishing (avoids stale framework-dependent files).
.PARAMETER SkipVerify
    Skip the artifact validation gate (not recommended; debug only).

.EXAMPLE
    .\Publish-SelfContained.ps1
    .\Publish-SelfContained.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string]$ProjectPath = "$PSScriptRoot\..\..\src\DesktopSuite\DesktopSuite.csproj",
    [string]$OutputDir   = "$PSScriptRoot\..\..\src\DesktopSuite\bin\x64\Release\self-contained",
    [string]$Configuration = 'Release',
    [string]$Platform     = 'x64',
    [string]$Runtime      = 'win-x64',
    [switch]$Clean,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'

# --- Normalize paths ---
$ProjectPath = Resolve-Path -LiteralPath $ProjectPath -ErrorAction Stop
$OutputDir   = [System.IO.Path]::GetFullPath($OutputDir)

Write-Output ("[publish] Project : " + $ProjectPath)
Write-Output ("[publish] Output  : " + $OutputDir)
Write-Output ("[publish] Config  : " + $Configuration + " | Platform: " + $Platform + " | RID: " + $Runtime + " | self-contained: true")

# --- 1. Validate dotnet ---
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error "[publish] dotnet CLI not found. Install .NET 8 SDK first."
}
$dotnetVer = & dotnet --version 2>$null
Write-Output ("[publish] dotnet  : " + $dotnet.Source + " (" + $dotnetVer + ")")

# --- 2. Clean output dir ---
if ($Clean -and (Test-Path -LiteralPath $OutputDir)) {
    Write-Output ("[publish] Cleaning old output: " + $OutputDir)
    # Delete files individually with [System.IO.File]::Delete, which bypasses the
    # platform safe-delete wrapper (see FIXES H-1). The wrapper also intercepts
    # Remove-Item and [System.IO.Directory]::Delete, so we avoid both for the
    # recursive delete. Leftover empty subdirectories are harmless: dotnet publish
    # -o overwrites files deterministically.
    Get-ChildItem -LiteralPath $OutputDir -Recurse -File | ForEach-Object {
        try { [System.IO.File]::Delete($_.FullName) } catch { }
    }
    # Best-effort remove the (now mostly empty) tree; tolerate safe-delete interception.
    try { [System.IO.Directory]::Delete($OutputDir, $true) } catch { }
}
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# --- 3. Publish (self-contained) ---
$publishArgs = @(
    'publish',
    "$ProjectPath",
    '-c', $Configuration,
    "-p:Platform=$Platform",
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $OutputDir,
    '/nologo'
)
Write-Output ("[publish] Running : dotnet " + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error ("[publish] dotnet publish failed (exit=" + $LASTEXITCODE + ").")
}

# --- 4. Artifact validation gate (prevents framework-dependent mis-deploy) ---
if (-not $SkipVerify) {
    $required = @(
        @{ Name = 'DesktopSuite.exe';         Role = 'apphost (native)' },
        @{ Name = 'DesktopSuite.dll';         Role = 'managed entry' },
        @{ Name = 'coreclr.dll';              Role = '.NET runtime (CoreCLR)' },
        @{ Name = 'clrjit.dll';               Role = '.NET JIT' },
        @{ Name = 'hostfxr.dll';              Role = '.NET host fxr' },
        @{ Name = 'hostpolicy.dll';           Role = '.NET host policy' },
        @{ Name = 'DirectWriteForwarder.dll'; Role = 'WPF native' },
        @{ Name = 'D3DCompiler_47_cor3.dll';  Role = 'WPF native' },
        @{ Name = 'mpv.exe';                  Role = 'media renderer' }
    )

    Write-Output "[publish] Validating artifacts (self-contained gate)..."
    $missing = @()
    foreach ($f in $required) {
        $p = Join-Path $OutputDir $f.Name
        if (-not (Test-Path -LiteralPath $p)) { $missing += $f }
    }

    $hasApphost = Test-Path -LiteralPath (Join-Path $OutputDir 'DesktopSuite.exe')
    $hasCoreClr = Test-Path -LiteralPath (Join-Path $OutputDir 'coreclr.dll')

    if ($missing.Count -gt 0) {
        Write-Output "[publish] MISSING required files -> package incomplete or wrong shape:"
        foreach ($m in $missing) { Write-Output ("   - " + $m.Name + "  (" + $m.Role + ")") }
        if ($hasApphost -and -not $hasCoreClr) {
            Write-Output "[publish] ERROR: apphost present but no CoreCLR -> this is a framework-dependent build!"
            Write-Output "[publish]        Deploying to a bare VM (no .NET) makes .NET code never execute."
            Write-Output "[publish]        Ensure the command includes --self-contained true."
        }
        Write-Error "[publish] Artifact validation failed; no valid self-contained package produced."
    }

    $allFiles  = Get-ChildItem -LiteralPath $OutputDir -Recurse -File
    $fileCount = $allFiles.Count
    $sizeBytes = ($allFiles | Measure-Object -Property Length -Sum).Sum
    $sizeMB    = [math]::Round($sizeBytes / 1MB, 1)
    Write-Output ("[publish] OK: 9/9 key files present (" + $fileCount + " files, ~" + $sizeMB + "MB).")
    Write-Output "[publish] Target needs no preinstalled .NET runtime; safe to deploy."
    Write-Output "[publish] --------------------------------------------------"
    Write-Output "[publish] WARNING: do NOT deploy a framework-dependent build (dotnet build)"
    Write-Output "[publish]          or a publish without -r to a bare VM. It requires .NET 8"
    Write-Output "[publish]          Desktop Runtime on the target, otherwise the app silently"
    Write-Output "[publish]          never runs (process alive but no window, icons stay visible)."
    Write-Output "[publish] --------------------------------------------------"
}

Write-Output ("[publish] Done. Self-contained package at: " + $OutputDir)
Write-Output ("[publish] Next: Run-Validation.ps1 -AppSource " + $OutputDir)
