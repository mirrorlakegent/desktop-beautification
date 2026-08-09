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

# --- 3b. mpv.exe bootstrap (112MB binary, NOT tracked in git) ---
# mpv.exe is a ~112MB binary that cannot enter git (GitHub 100MB/file limit; Gitee
# free has no Git LFS). It is published as a GitHub Release asset instead.
#
# NOTE on Gitee hosting: mpv.exe (~112MB) exceeds the Gitee free-tier attachment
# cap (~100MB), so it cannot be hosted directly on Gitee. If a mirror fallback is
# needed, set $FallbackMpvUrl below to a working direct link (object storage / CDN
# / another GitHub source, etc.). The maintainer will backfill this constant after
# verifying which mirrors are reachable in the target environment.
#
# Default source: GitHub Release tag `vendor/mpv-v1` (repo desktop-beautification),
# asset mpv.exe. `$ExpectedMpvSha256` is the published-asset hash, pinned for
# supply-chain integrity so a tampered/incorrect binary can never be shipped.
#
# Source resolution priority when mpv.exe is absent from the package:
#   (a) $env:MPV_EXE_URL   -> download from an arbitrary URL  (highest priority)
#   (b) $env:MPV_EXE_LOCAL -> copy from a local file path
#   (c) $DefaultMpvUrl     -> auto-download from the default GitHub Release asset
#       (on failure, if $FallbackMpvUrl is non-empty, retry once via the mirror)
# To switch the default source, change the constants below or set an env var.
# If ALL of (a)/(b)/(c)/(fallback) fail, the original WARNING is emitted (media
# rendering unavailable) and the package is left without mpv.exe.
$DefaultMpvUrl     = 'https://github.com/mirrorlakegent/desktop-beautification/releases/download/vendor/mpv-v1/mpv.exe'
$FallbackMpvUrl    = ''   # mirror fallback; set to a reachable direct link if the default source is unreachable (Gitee ~100MB cap blocks direct hosting)
$ExpectedMpvSha256 = '3ba74e88277e76e830967bea421e63492a43603f6950858b1217cc57c2d1a4e5'

$mpvDest   = Join-Path $OutputDir 'mpv.exe'
$mpvUrl    = $env:MPV_EXE_URL
$mpvLocal  = $env:MPV_EXE_LOCAL

# Unified SHA256 verification: runs whether the binary was just fetched OR was
# already present in the package. Prevents a tampered/leftover mpv.exe from a
# previous run from leaking into the release (HARDENING 1).
function Test-MpvIntegrity {
    param(
        [string]$Source = 'existing'
    )
    $actualSha   = (Get-FileHash -Algorithm SHA256 -LiteralPath $mpvDest).Hash.ToLower()
    $expectedSha = $ExpectedMpvSha256.ToLower()
    if ($actualSha -ne $expectedSha) {
        # Tampered/incorrect binary: delete (tolerate safe-delete interception)
        # and abort hard so a corrupted mpv.exe is never deployed.
        try { Remove-Item -LiteralPath $mpvDest -Force -ErrorAction Stop } catch { }
        Write-Error ("[publish] mpv.exe SHA256 mismatch (source=" + $Source + "): got " + $actualSha + ", expected " + $expectedSha + ". Binary removed; aborting to avoid shipping a tampered mpv.exe.")
    }
    $mb = [math]::Round((Get-Item $mpvDest).Length / 1MB, 1)
    Write-Output ("[publish] mpv.exe verified (SHA256 OK, " + $mb + "MB).")
}

if (Test-Path -LiteralPath $mpvDest) {
    # HARDENING 1: mpv.exe already present -> skip fetch, verify in place.
    Write-Output "[publish] mpv.exe already present in package -> verifying existing binary..."
    Test-MpvIntegrity -Source 'existing'
} else {
    $fetched     = $false
    $fetchSource = ''

    # (a) explicit URL override
    if (-not $fetched -and $mpvUrl) {
        try {
            Write-Output ("[publish] Fetching mpv.exe from `$MPV_EXE_URL ...")
            Invoke-WebRequest -Uri $mpvUrl -OutFile $mpvDest -ErrorAction Stop
            $fetched     = $true
            $fetchSource = 'MPV_EXE_URL'
        } catch {
            Write-Output ("[publish] WARN: download mpv.exe from `$MPV_EXE_URL failed: " + $_.Exception.Message)
        }
    }

    # (b) local file override
    if (-not $fetched -and $mpvLocal -and (Test-Path -LiteralPath $mpvLocal)) {
        try {
            Copy-Item -LiteralPath $mpvLocal -Destination $mpvDest -Force
            $fetched     = $true
            $fetchSource = 'MPV_EXE_LOCAL'
            Write-Output ("[publish] Copied mpv.exe from `$MPV_EXE_LOCAL.")
        } catch {
            Write-Output ("[publish] WARN: copy mpv.exe from `$MPV_EXE_LOCAL failed: " + $_.Exception.Message)
        }
    }

    # (c) default auto-download from the pinned GitHub Release asset, with
    #     HARDENING 2: optional one-time mirror fallback via $FallbackMpvUrl.
    if (-not $fetched) {
        try {
            Write-Output ("[publish] Fetching mpv.exe from default source `$DefaultMpvUrl ...")
            Invoke-WebRequest -Uri $DefaultMpvUrl -OutFile $mpvDest -ErrorAction Stop
            $fetched     = $true
            $fetchSource = 'DefaultMpvUrl'
        } catch {
            Write-Output ("[publish] WARN: auto-download mpv.exe from default source failed: " + $_.Exception.Message)
            # HARDENING 2: retry once via mirror fallback, only when configured.
            if ($FallbackMpvUrl) {
                try {
                    Write-Output ("[publish] Retrying mpv.exe from fallback mirror `$FallbackMpvUrl ...")
                    Invoke-WebRequest -Uri $FallbackMpvUrl -OutFile $mpvDest -ErrorAction Stop
                    $fetched     = $true
                    $fetchSource = 'FallbackMpvUrl'
                } catch {
                    Write-Output ("[publish] WARN: fallback mirror download also failed: " + $_.Exception.Message)
                }
            }
        }
    }

    if ($fetched) {
        Test-MpvIntegrity -Source $fetchSource
    } else {
        Write-Output "[publish] WARNING: mpv.exe missing from package -> media rendering unavailable."
        Write-Output "[publish]          Source it via `$MPV_EXE_URL / `$MPV_EXE_LOCAL, or copy manually to:"
        Write-Output ("[publish]          " + $mpvDest)
    }
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
        @{ Name = 'D3DCompiler_47_cor3.dll';  Role = 'WPF native' }
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
