param([ValidateSet('1','17','vr')][string]$Line = '1',
    # MSVC toolset for the plugin and its dependencies, such as '14.44'. Empty uses the newest one installed.
    # Releases name one, so that an update of the build machine does not change the compiler unnoticed.
    [string]$Toolset = '')
. "$PSScriptRoot/windows-env.ps1"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Install Visual Studio C++ build tools and the Windows SDK.' }
Import-Module (Join-Path $visualStudio 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
$toolsets = (Get-ChildItem (Join-Path $visualStudio 'VC/Tools/MSVC') -Directory).Name
$devShell = '-arch=x64 -host_arch=x64'
if ($Toolset) {
    if (-not ($toolsets | Where-Object { $_.StartsWith("$Toolset.") })) {
        throw "MSVC toolset $Toolset is not installed (installed: $($toolsets -join ', ')). Install it, or build without -Toolset."
    }
    $devShell += " -vcvars_ver=$Toolset"
}
Enter-VsDevShell -VsInstallPath $visualStudio -SkipAutomaticLocation -DevCmdArguments $devShell
# Recorded in the build log: a new compiler changes the DLL even when the source has not changed.
Write-Host "MSVC toolset in use: $env:VCToolsVersion (installed: $($toolsets -join ', '))"
if ($Toolset -and -not "$env:VCToolsVersion".StartsWith("$Toolset.")) { throw "Asked for MSVC toolset $Toolset, got $env:VCToolsVersion." }

$manifestDir = switch ($Line) { '1' { 'skse' } '17' { 'skse/cmake/manifest-17' } 'vr' { 'skse/cmake/manifest-vr' } }
$manifest = Get-Content "$manifestDir/vcpkg.json" -Raw | ConvertFrom-Json
$vcpkg = Join-Path $PenguLODRoot ".tools/runtime-vcpkg-$Line"
if (-not (Test-Path "$vcpkg/.git")) {
    git init $vcpkg
    git -C $vcpkg remote add origin https://github.com/microsoft/vcpkg.git
}
git -C $vcpkg fetch --depth 1 origin $manifest.'builtin-baseline'
git -C $vcpkg checkout --detach FETCH_HEAD
& "$vcpkg/bootstrap-vcpkg.bat" -disableMetrics
# Build from source so the CommonLib tree, which holds the OpenVR license notice, is present.
$env:VCPKG_BINARY_SOURCES = 'clear'
$installed = Join-Path $PenguLODRoot ".tools/runtime-deps-$Line"
$build = Join-Path $PenguLODRoot ".tools/runtime-build-$Line"
$runtimeLine = if ($Line -eq 'vr') { '14' } else { $Line }
$options = @("-DCMAKE_TOOLCHAIN_FILE=$vcpkg/scripts/buildsystems/vcpkg.cmake",
    "-DVCPKG_MANIFEST_DIR=$PenguLODRoot/$manifestDir", "-DVCPKG_INSTALLED_DIR=$installed",
    '-DVCPKG_TARGET_TRIPLET=x64-windows-static-md', '-DCMAKE_BUILD_TYPE=Release',
    "-DPENGULOD_RUNTIME_LINE=$runtimeLine")
if ($Line -ne '1') { $options += "-DVCPKG_OVERLAY_PORTS=$PenguLODRoot/skse/cmake/ports-$Line" }
if ($Toolset) {
    # vcpkg picks its own compiler for the dependencies, which are most of the DLL. This copy of the stock triplet names the toolset.
    $triplets = Join-Path $PenguLODRoot ".tools/runtime-triplets-$Line"
    New-Item -ItemType Directory -Force $triplets | Out-Null
    Set-Content "$triplets/x64-windows-static-md.cmake" @('set(VCPKG_TARGET_ARCHITECTURE x64)', 'set(VCPKG_CRT_LINKAGE dynamic)',
        'set(VCPKG_LIBRARY_LINKAGE static)', "set(VCPKG_PLATFORM_TOOLSET_VERSION $Toolset)")
    $options += "-DVCPKG_OVERLAY_TRIPLETS=$triplets"
}
# Configuring installs the dependencies with vcpkg.
Invoke-WithRetry { cmake -S skse -B $build -G Ninja @options } 'Installing the plugin dependencies'
cmake --build $build --parallel 3
$folder = switch ($Line) { '1' { '1.5.97-1.6.1170' } '17' { '1.7.x' } 'vr' { '1.4.15-VR' } }
$out = Join-Path $PenguLODRoot "dist/runtime/$folder"
New-Item -ItemType Directory -Force "$out/SKSE/Plugins", "$out/licenses" | Out-Null
Copy-Item "$build/PenguLOD.dll" "$out/SKSE/Plugins/"
foreach ($package in Get-ChildItem "$installed/x64-windows-static-md/share" -Directory) {
    if (Test-Path "$($package.FullName)/copyright") {
        Copy-Item "$($package.FullName)/copyright" "$out/licenses/$($package.Name).txt"
    }
}
Copy-Item "$installed/vcpkg/status" "$out/licenses/vcpkg-packages.txt"
python tools/package-runtime-licenses.py "$vcpkg/buildtrees/commonlibsse-ng/src" $out
Write-Host "Built PenguLOD runtime for $folder into $out"
