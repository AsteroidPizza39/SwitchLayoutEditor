#Requires -Version 5.1
<#
.SYNOPSIS
  Restore NuGet packages and build Switch Layout Editor locally.

.EXAMPLE
  .\build.ps1
.EXAMPLE
  .\build.ps1 -Run
.EXAMPLE
  .\build.ps1 -Configuration Debug -SkipRestore
.EXAMPLE
  .\build.ps1 -UpdateCommon
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$UpdateCommon,
    [switch]$Run,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

$ReposParent = Split-Path $RepoRoot -Parent
$SiblingCommon = Join-Path $ReposParent 'SwitchThemeInjector'
$SiblingToolbox = Join-Path $ReposParent 'Switch-Toolbox'
$PackagesDir = Join-Path $RepoRoot 'packages'
$ToolsDir = Join-Path $RepoRoot '.tools'
$Solution = Join-Path $RepoRoot 'SwitchLayoutEditor.sln'
$PackagesConfig = Join-Path $RepoRoot 'BflytPreview\packages.config'
$NuGetVersion = '6.9.1'
$NuGetUrl = "https://dist.nuget.org/win-x86-commandline/v$NuGetVersion/nuget.exe"

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Get-MsBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if ($found -and (Test-Path $found)) {
            return $found
        }
    }

    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw 'MSBuild not found. Install Visual Studio or Build Tools with the .NET desktop build tools workload.'
}

function Get-NuGetPath {
    $cmd = Get-Command nuget -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    if (-not (Test-Path $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir | Out-Null
    }

    $localNuGet = Join-Path $ToolsDir 'nuget.exe'
    if (-not (Test-Path $localNuGet)) {
        Write-Step "Downloading nuget.exe $NuGetVersion to .tools\"
        Invoke-WebRequest -Uri $NuGetUrl -OutFile $localNuGet -UseBasicParsing
    }

    return $localNuGet
}

function Ensure-SiblingRepo([string]$Path, [string]$RepoUrl, [string]$MarkerRelativePath, [string]$Name) {
    $marker = Join-Path $Path $MarkerRelativePath
    if (-not (Test-Path $marker)) {
        Write-Step "Cloning $Name next to this repo"
        git clone $RepoUrl $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to clone $Name."
        }
        return
    }

    Write-Host "Using $Name at $Path"

    if ($UpdateCommon) {
        Write-Step "Updating $Name (git pull)"
        Push-Location $Path
        try {
            git pull --ff-only
            if ($LASTEXITCODE -ne 0) {
                throw "git pull failed in $Name. Resolve conflicts or use a clean sibling checkout."
            }
        }
        finally {
            Pop-Location
        }
    }
}

Ensure-SiblingRepo $SiblingCommon 'https://github.com/exelix11/SwitchThemeInjector.git' 'SwitchThemesCommon\SwitchThemesCommon.projitems' 'SwitchThemeInjector'
Ensure-SiblingRepo $SiblingToolbox 'https://github.com/KillzXGaming/Switch-Toolbox.git' 'Switch_Toolbox_Library\Toolbox_Library.csproj' 'Switch-Toolbox'

$tegraDll = Join-Path $SiblingToolbox 'Toolbox\tegra_swizzle_x64.dll'
if (-not (Test-Path $tegraDll)) {
    throw "Missing tegra_swizzle_x64.dll at $tegraDll (required for BNTX texture preview)."
}

$msbuild = Get-MsBuildPath
Write-Host "MSBuild: $msbuild"

if (-not $SkipRestore) {
    $nuget = Get-NuGetPath
    Write-Host "NuGet:   $nuget"

    if (-not (Test-Path $PackagesDir)) {
        New-Item -ItemType Directory -Path $PackagesDir | Out-Null
    }

    Write-Step 'Restoring NuGet packages'
    & $nuget install $PackagesConfig -OutputDirectory $PackagesDir -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        throw 'NuGet restore failed.'
    }
}
else {
    Write-Host 'Skipping NuGet restore (-SkipRestore)'
}

Write-Step "Building solution ($Configuration)"
& $msbuild $Solution `
    -restore:false `
    -p:Configuration=$Configuration `
    -p:Platform='Any CPU' `
    -m `
    -v:m
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed.'
}

$OutDir = Join-Path $RepoRoot "BflytPreview\bin\$Configuration"
$ExePath = Join-Path $OutDir 'Switch Layout Editor.exe'

# Ensure texture-preview natives are present even if MSBuild Copy targets were skipped
$nativeSources = @(
    @{ Src = Join-Path $SiblingToolbox 'Toolbox\tegra_swizzle_x64.dll'; Dst = Join-Path $OutDir 'tegra_swizzle_x64.dll' },
    @{ Src = Join-Path $SiblingToolbox 'Toolbox\tegra_swizzle_x86.dll'; Dst = Join-Path $OutDir 'tegra_swizzle_x86.dll' },
    @{ Src = Join-Path $SiblingToolbox 'packages\DirectXTexNet.1.0.0-rc3\lib\net40\DirectXTexNet.dll'; Dst = Join-Path $OutDir 'DirectXTexNet.dll' },
    @{ Src = Join-Path $SiblingToolbox 'Toolbox\Lib\Plugins\DirectXTex.dll'; Dst = Join-Path $OutDir 'DirectXTex.dll' }
)
foreach ($item in $nativeSources) {
    if ((Test-Path $item.Src) -and -not (Test-Path $item.Dst)) {
        Copy-Item $item.Src $item.Dst -Force
    }
}
$implSrc = Join-Path $SiblingToolbox 'Toolbox\x64\DirectXTexNetImpl.dll'
$implDstDir = Join-Path $OutDir 'x64'
$implDst = Join-Path $implDstDir 'DirectXTexNetImpl.dll'
if ((Test-Path $implSrc) -and -not (Test-Path $implDst)) {
    New-Item -ItemType Directory -Path $implDstDir -Force | Out-Null
    Copy-Item $implSrc $implDst -Force
}

Write-Host "`nBuild succeeded." -ForegroundColor Green
Write-Host "Output: $OutDir"

if (-not (Test-Path $ExePath)) {
    throw "Expected executable not found: $ExePath"
}

if ($Run) {
    Write-Step "Launching $ExePath"
    Start-Process -FilePath $ExePath
}
