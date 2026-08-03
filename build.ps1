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

$SiblingCommon = Join-Path (Split-Path $RepoRoot -Parent) 'SwitchThemeInjector'
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

function Ensure-SwitchThemeInjector {
    if (-not (Test-Path (Join-Path $SiblingCommon 'SwitchThemesCommon\SwitchThemesCommon.projitems'))) {
        Write-Step "Cloning SwitchThemeInjector next to this repo"
        git clone https://github.com/exelix11/SwitchThemeInjector.git $SiblingCommon
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to clone SwitchThemeInjector.'
        }
        return
    }

    Write-Host "Using SwitchThemeInjector at $SiblingCommon"

    if ($UpdateCommon) {
        Write-Step 'Updating SwitchThemeInjector (git pull)'
        Push-Location $SiblingCommon
        try {
            git pull --ff-only
            if ($LASTEXITCODE -ne 0) {
                throw 'git pull failed in SwitchThemeInjector. Resolve conflicts or use a clean sibling checkout.'
            }
        }
        finally {
            Pop-Location
        }
    }
}

Ensure-SwitchThemeInjector

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

Write-Host "`nBuild succeeded." -ForegroundColor Green
Write-Host "Output: $OutDir"

if (-not (Test-Path $ExePath)) {
    throw "Expected executable not found: $ExePath"
}

if ($Run) {
    Write-Step "Launching $ExePath"
    Start-Process -FilePath $ExePath
}
