<#
.SYNOPSIS
    Builds the Revit AI add-in inside a Windows VM and installs it for each Revit year found.

.DESCRIPTION
    Run from Windows PowerShell, for example with the repo shared from the Mac:

        powershell -ExecutionPolicy Bypass -File \\Mac\Home\Projects\revit-ai\scripts\windows\setup-and-build.ps1

    Steps:
      1. Checks for a .NET SDK 8 or later; offers to install the .NET 8 SDK with winget.
      2. Finds Revit 2025 and 2026 under C:\Program Files\Autodesk\.
      3. Stops if Revit is running (it locks the add-in DLLs).
      4. Copies the repo to a local folder. Building on a shared Mac folder is slow and
         would mix Windows obj\ files with the macOS ones.
      5. Builds Debug for each Revit year. The build deploys itself to
         %APPDATA%\Autodesk\Revit\Addins\<year>\.
      6. Checks that the manifest and DLLs were deployed and match the build output.

    See docs/WINDOWS_VM_SETUP.md.

.PARAMETER BuildDir
    Local folder the repo is copied to. Default: %USERPROFILE%\revit-ai-build.
#>
param(
    [string]$BuildDir = (Join-Path $env:USERPROFILE 'revit-ai-build')
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$SupportedYears = 2025, 2026

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "== $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "   OK  $Text" -ForegroundColor Green
}

function Stop-WithError([string]$Text) {
    Write-Host ""
    Write-Host "ERROR: $Text" -ForegroundColor Red
    exit 1
}

function Get-DotNetSdkMajorVersions {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        return @()
    }
    # Lines look like: 8.0.404 [C:\Program Files\dotnet\sdk]
    return @(dotnet --list-sdks | ForEach-Object { [int]($_ -split '\.')[0] })
}

# --- 1. .NET SDK -------------------------------------------------------------

Write-Step 'Checking for the .NET 8 SDK'

if (-not (Get-DotNetSdkMajorVersions | Where-Object { $_ -ge 8 })) {
    Write-Host '   No .NET SDK 8 or later found.'

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Stop-WithError ('winget is not available. Install the .NET 8 SDK from ' +
            'https://dotnet.microsoft.com/download/dotnet/8.0 and run this script again.')
    }

    $answer = Read-Host '   Install the .NET 8 SDK now with winget? [y/N]'
    if ($answer -notmatch '^(y|yes)$') {
        Stop-WithError 'The .NET 8 SDK is required. Nothing was installed.'
    }

    # winget shows its own agreement prompts; answer them yourself.
    winget install --id Microsoft.DotNet.SDK.8 --exact --source winget
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "winget failed (exit code $LASTEXITCODE)."
    }

    # Pick up the new PATH without reopening the window.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')

    if (-not (Get-DotNetSdkMajorVersions | Where-Object { $_ -ge 8 })) {
        Stop-WithError 'The SDK was installed but dotnet is not found. Open a new PowerShell window and run this script again.'
    }
}

Write-Ok ('.NET SDKs: ' + ((dotnet --list-sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))

# --- 2. Revit ----------------------------------------------------------------

Write-Step 'Looking for Revit 2025 / 2026'

$RevitYears = @($SupportedYears | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" })

if ($RevitYears.Count -eq 0) {
    Stop-WithError 'Neither C:\Program Files\Autodesk\Revit 2025\Revit.exe nor ...\Revit 2026\Revit.exe exists. Install Revit first.'
}

foreach ($year in $RevitYears) {
    Write-Ok "Revit $year found"
}

# --- 3. Revit must be closed -------------------------------------------------

Write-Step 'Checking that Revit is not running'

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    Stop-WithError 'Revit is running. Close it (it locks the add-in DLLs) and run this script again.'
}

Write-Ok 'Revit is not running'

# --- 4. Local copy of the repo -----------------------------------------------

Write-Step "Copying the repo to $BuildDir"

# robocopy /MIR deletes files in the destination, so only ever mirror into our own folder.
if ((Test-Path $BuildDir) -and
    (Get-ChildItem $BuildDir -Force | Select-Object -First 1) -and
    -not (Test-Path (Join-Path $BuildDir 'RevitAi.sln'))) {
    Stop-WithError "$BuildDir exists and is not a copy of this repo. Pass a different -BuildDir."
}

# /XD also keeps those folders from being purged, so incremental builds still work.
robocopy $RepoRoot $BuildDir /MIR /XD bin obj .git .vs .idea /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    Stop-WithError "Copying from $RepoRoot failed (robocopy exit code $LASTEXITCODE)."
}

Write-Ok "Copied from $RepoRoot"

# --- 5 & 6. Build, deploy and verify each Revit year -------------------------

$Project = Join-Path $BuildDir 'src\RevitAi.Addin'

foreach ($year in $RevitYears) {
    Write-Step "Building for Revit $year"

    dotnet build $Project -c Debug "-p:RevitVersion=$year"
    if ($LASTEXITCODE -ne 0) {
        Stop-WithError "Build for Revit $year failed. See the output above."
    }

    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $manifest = Join-Path $addinsDir 'RevitAi.addin'
    if (-not (Test-Path $manifest)) {
        Stop-WithError "Manifest not deployed: $manifest"
    }

    $manifestXml = [xml](Get-Content $manifest)
    $assembly = Join-Path $addinsDir $manifestXml.RevitAddIns.AddIn.Assembly
    if (-not (Test-Path $assembly)) {
        Stop-WithError "The manifest points to $assembly, which does not exist."
    }

    $outputDir = Join-Path $Project "bin\Debug\R$year"
    foreach ($dll in 'RevitAi.Addin.dll', 'RevitAi.Core.dll') {
        $deployed = Join-Path $addinsDir "RevitAi\$dll"
        if (-not (Test-Path $deployed)) {
            Stop-WithError "Not deployed: $deployed"
        }
        if ((Get-FileHash $deployed).Hash -ne (Get-FileHash (Join-Path $outputDir $dll)).Hash) {
            Stop-WithError "$deployed differs from the build output. Is Revit $year holding it open?"
        }
    }

    Write-Ok "Deployed to $addinsDir"
}

# --- Next steps --------------------------------------------------------------

Write-Step 'Done'
Write-Host "   Installed for Revit: $($RevitYears -join ', ')"
Write-Host ''
Write-Host '   Next:'
Write-Host '   1. Start Revit and choose "Always Load" when asked about Revit AI.'
Write-Host '   2. Open a project (a copy is best) and click Add-Ins > Revit AI > Self-test.'
Write-Host "      It runs the automatic checks, undoes everything and saves a report in $env:LOCALAPPDATA\RevitAi\"
Write-Host '   3. Then Add-Ins > Revit AI > Assistant and the short manual list at the top of docs\SMOKE_TESTS.md.'
Write-Host "      Log: $env:LOCALAPPDATA\RevitAi\logs\"
Write-Host '   After changing code on the Mac, close Revit and run this script again.'
