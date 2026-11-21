# Shared environment for native Windows builds; PowerShell 7.4 or newer.
#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $IsWindows) { throw 'Run this script on Windows x64.' }
$PenguLODRoot = Split-Path $PSScriptRoot -Parent
Set-Location $PenguLODRoot
$env:DOTNET_CLI_HOME = Join-Path $PenguLODRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $PenguLODRoot '.tools/nuget'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:PYTHONUTF8 = '1'
New-Item -ItemType Directory -Force .tools, .artifacts | Out-Null
if (-not $env:VCPKG_DEFAULT_BINARY_CACHE) {
    $env:VCPKG_DEFAULT_BINARY_CACHE = Join-Path $PenguLODRoot '.tools/vcpkg-cache'
}
New-Item -ItemType Directory -Force $env:VCPKG_DEFAULT_BINARY_CACHE | Out-Null

# vcpkg downloads sources and patches from GitHub, which sometimes answers 503 for a while. Packages that were already built
# stay installed, so trying again continues where the failed attempt stopped.
function Invoke-WithRetry([scriptblock]$Action, [string]$What, [int]$Attempts = 4) {
    for ($attempt = 1; ; $attempt++) {
        try { & $Action; return }
        catch {
            if ($attempt -ge $Attempts) { throw }
            Write-Host "$What failed (attempt $attempt of $Attempts): $($_.Exception.Message) Trying again in 90 seconds."
            Start-Sleep -Seconds 90
        }
    }
}
