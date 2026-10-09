param(
    [string]$MSBuildPath = "MSBuild.exe",
    [string]$GameDirectory = ""
)
$ErrorActionPreference = "Stop"
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$assembliesDirectory = Join-Path $workspaceRoot "assemblies"
$gameReferences = @("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PopsApiWrapper.dll",
    "ColossalManaged.dll", "ICities.dll", "UnityEngine.dll", "UnityEngine.UI.dll")

if ($GameDirectory) {
    $managedDirectory = Join-Path $GameDirectory "Cities_Data\Managed"
    foreach ($reference in $gameReferences) {
        $source = Join-Path $managedDirectory $reference
        if (!(Test-Path -LiteralPath $source)) {
            throw "Game reference missing: $source"
        }
    }
    foreach ($reference in $gameReferences) {
        Copy-Item -LiteralPath (Join-Path $managedDirectory $reference) -Destination (Join-Path $assembliesDirectory $reference) -Force
    }
}

foreach ($reference in $gameReferences) {
    if (!(Test-Path -LiteralPath (Join-Path $assembliesDirectory $reference))) {
        throw "Missing $reference. Run this script with -GameDirectory pointing to the original game installation."
    }
}

$assetsPath = Join-Path $workspaceRoot "src\basegame\obj\project.assets.json"
if (!(Test-Path -LiteralPath $assetsPath)) {
    throw "Restore the CSM solution's NuGet dependencies first."
}
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$harmonyPackage = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "CitiesHarmony.Harmony/*" } | Select-Object -First 1
if (!$harmonyPackage) {
    throw "CitiesHarmony.Harmony is missing from the restored assets."
}

$harmonyAssembly = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder ($harmonyPackage.ToLowerInvariant() + "/lib/net35/CitiesHarmony.Harmony.dll")
    if (Test-Path -LiteralPath $candidate) {
        $harmonyAssembly = $candidate
        break
    }
}
if (!$harmonyAssembly) {
    throw "The restored CitiesHarmony.Harmony assembly cannot be found."
}

$buildArguments = @((Join-Path $workspaceRoot "tests\BuildingStatusSync.Tests.csproj"), "/t:Build",
    "/p:Configuration=Release", "/p:PostBuildEvent=", "/p:HarmonyAssemblyPath=$harmonyAssembly", "/verbosity:minimal", "/nologo")
$sdkDirectory = Join-Path ([Environment]::GetFolderPath("ProgramFilesX86")) "Windows Kits\10"
if (Test-Path -LiteralPath $sdkDirectory) {
    $buildArguments += "/p:TargetPlatformSdkPath=$sdkDirectory"
    $buildArguments += "/p:TargetPlatformDisplayName=Windows"
}

& $MSBuildPath @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Building status regression runner failed to build."
}

& (Join-Path $workspaceRoot "tests\bin\Release\BuildingStatusSync.Tests.exe")
if ($LASTEXITCODE -ne 0) {
    throw "Building status regression checks failed."
}
