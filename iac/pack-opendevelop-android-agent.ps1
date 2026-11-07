#!/usr/bin/env pwsh
#
# Packs the Android debug agents OpenDevelop ships into a NuGet package, so that repository stops
# running a ~10-minute .NET 11 + Android workload + NDK + NativeAOT build that gates every one of
# its platform builds, for content that only changes when this repository does.
#
# The package carries three things, all of which OpenDevelop used to produce itself:
#   agent/<abi>/libSharpDbg.AndroidAgent.so   the three NativeAOT agents (arm64-v8a, x86_64,
#                                            armeabi-v7a) plus the runtime notices they ship
#   spdx/assets/<rid>/...                     the restore graphs, so OpenDevelop's SPDX collector
#                                            still sees the agent's embedded dependencies
#   spdx/packages/<id>/<version>/...          those dependencies' nuspec/license metadata, which
#                                            a host without the Android workload has no cache for
#
# Usage:
#   ./iac/pack-opendevelop-android-agent.ps1 -Version 1.0.0-lextudio.local
#   ./iac/pack-opendevelop-android-agent.ps1 -Version 1.0.0-lextudio.123.1 -NuGetConfigPath /tmp/dap-nuget.config
#
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$DotNet = 'dotnet',
    [string]$AndroidSdkDirectory = $env:ANDROID_HOME,
    [string]$AndroidNdkDirectory = '',
    [string[]]$RuntimeIdentifiers = @('android-arm64', 'android-x64', 'android-arm'),
    # Defaults to artifacts/packages, the folder the publishing workflow uploads.
    [string]$OutputDirectory = '',
    # A restore configuration that can see the OmniSharp DAP fork (GitHub Packages in CI, the local
    # feed of the enclosing OpenDevelop checkout on a developer machine). Without it the repository's
    # own nuget.config applies, which points at a local feed that only exists inside OpenDevelop.
    [string]$NuGetConfigPath = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts/packages'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

# The agent targets net11.0-android, and the repository's global.json pins the desktop SDK. Find the
# 11.x installation the Android workload was installed into, and pin the scratch build to it.
$versions = & $DotNet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'Could not list .NET SDKs.' }
# The parameter is $Version, and PowerShell variable names are case-insensitive: this must not be
# $version, or selecting the SDK would overwrite the package version.
$sdkVersion = @($versions | ForEach-Object { if ($_ -match '^(11\.[^ ]+)\s') { $Matches[1] } } | Sort-Object { [System.Management.Automation.SemanticVersion]::Parse($_) } -Descending) | Select-Object -First 1
if (-not $sdkVersion) { throw 'Building the Android debugger requires a .NET 11 SDK with the Android workload.' }
$sdkLine = @($versions | Where-Object { $_.StartsWith("$sdkVersion ") }) | Select-Object -First 1
if ($sdkLine -notmatch '\[(.+)\]') { throw 'Could not determine the .NET SDK root.' }
$dotnetRoot = Split-Path -Parent $Matches[1]

if (-not $AndroidSdkDirectory) { $AndroidSdkDirectory = $env:ANDROID_SDK_ROOT }
if (-not $AndroidSdkDirectory) {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ($IsMacOS) { $AndroidSdkDirectory = Join-Path $userProfile 'Library/Android/sdk' }
    elseif ($IsWindows) { $AndroidSdkDirectory = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    else { $AndroidSdkDirectory = Join-Path $userProfile 'Android/Sdk' }
}
if (-not (Test-Path -LiteralPath $AndroidSdkDirectory -PathType Container)) { throw "Android SDK not found: $AndroidSdkDirectory" }
if (-not $AndroidNdkDirectory) {
    $ndks = Join-Path $AndroidSdkDirectory 'ndk'
    if (Test-Path -LiteralPath $ndks) {
        $AndroidNdkDirectory = (Get-ChildItem -LiteralPath $ndks -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    }
}
if (-not $AndroidNdkDirectory -or -not (Test-Path -LiteralPath $AndroidNdkDirectory -PathType Container)) { throw 'An Android NDK is required.' }

$abis = @{ 'android-arm64' = 'arm64-v8a'; 'android-x64' = 'x86_64'; 'android-arm' = 'armeabi-v7a' }
foreach ($rid in $RuntimeIdentifiers) { if (-not $abis.ContainsKey($rid)) { throw "Unsupported Android RID: $rid" } }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('sharpdbg-android-agent-' + [guid]::NewGuid().ToString('N'))
$originalDirectory = Get-Location
try {
    New-Item -ItemType Directory -Path (Join-Path $scratch 'src') -Force | Out-Null
    foreach ($project in @('SharpDbg.AndroidAgent', 'SharpDbg.Application', 'SharpDbg.Infrastructure')) {
        $projectRoot = Join-Path (Join-Path $root 'src') $project
        $destination = Join-Path (Join-Path $scratch 'src') $project
        Get-ChildItem -LiteralPath $projectRoot -File -Recurse | Where-Object {
            $_.FullName -notmatch '[/\\](bin|obj)[/\\]'
        } | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
            $target = Join-Path $destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'Directory.Build.props') -Destination $scratch

    # The scratch directory is outside the repository, so NuGet's walk up from the project finds no
    # config at all. Supply one: either the caller's (CI: GitHub Packages with the job token) or one
    # built around the local feed an OpenDevelop checkout keeps at artifacts/openavalon-feed.
    if ($NuGetConfigPath) {
        if (-not (Test-Path -LiteralPath $NuGetConfigPath -PathType Leaf)) { throw "NuGet configuration not found: $NuGetConfigPath" }
        Copy-Item -LiteralPath $NuGetConfigPath -Destination (Join-Path $scratch 'nuget.config')
    } else {
        $dapFeed = Join-Path $root '../../artifacts/openavalon-feed'
        if (-not (Test-Path -LiteralPath $dapFeed -PathType Container)) {
            throw "The OmniSharp DAP fork feed was not found at $dapFeed. Run the enclosing OpenDevelop checkout's ./sync-openavalon-feed.ps1, or pass -NuGetConfigPath."
        }
        @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
    <add key="dap-local" value="$([System.Security.SecurityElement]::Escape($dapFeed))" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="dap-local"><package pattern="OmniSharp.Extensions.*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $scratch 'nuget.config') -Encoding utf8
    }
    @{ sdk = @{ version = $sdkVersion; allowPrerelease = $true; rollForward = 'disable' } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'global.json') -Encoding utf8

    Set-Location $scratch
    $projectFile = Join-Path $scratch 'src/SharpDbg.AndroidAgent/SharpDbg.AndroidAgent.csproj'
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages' }
    $stage = Join-Path $scratch 'stage'
    $agentStage = Join-Path $stage 'agent'
    $spdxStage = Join-Path $stage 'spdx'
    $assetsStage = Join-Path $spdxStage 'assets'
    $metadataStage = Join-Path $spdxStage 'packages'

    foreach ($rid in $RuntimeIdentifiers) {
        Write-Host "Publishing the Android debugger for $rid..."
        & $DotNet publish $projectFile -c Release -r $rid '-p:UseArtifactsOutput=false' "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:AndroidNdkDirectory=$AndroidNdkDirectory" '-tl:off'
        if ($LASTEXITCODE -ne 0) { throw "Android debugger publish failed for $rid." }
        $publishRoot = Join-Path $scratch "src/SharpDbg.AndroidAgent/bin/Release/net11.0-android/$rid"
        $library = Join-Path $publishRoot 'native/libSharpDbg.AndroidAgent.so'
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw "Publish produced no Android debugger library: $library" }

        # Never publish an agent for the wrong ABI: the three RIDs map to two ELF classes and three
        # machines, and a mismatch only shows up when a device loads the library.
        $expected = @{ 'android-arm64' = @{ Class = 2; Machine = 183 }; 'android-x64' = @{ Class = 2; Machine = 62 }; 'android-arm' = @{ Class = 1; Machine = 40 } }[$rid]
        $stream = [IO.File]::OpenRead($library)
        try {
            $header = [byte[]]::new(20)
            if ($stream.Read($header, 0, $header.Length) -ne $header.Length) { throw "Truncated Android debugger library: $library" }
        } finally { $stream.Dispose() }
        if ($header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or $header[4] -ne $expected.Class -or $header[5] -ne 1 -or [BitConverter]::ToUInt16($header, 18) -ne $expected.Machine) {
            throw "The Android debugger library for $rid has the wrong ELF architecture: $library"
        }

        $agentDirectory = Join-Path $agentStage $abis[$rid]
        New-Item -ItemType Directory -Path $agentDirectory -Force | Out-Null
        Copy-Item -LiteralPath $library -Destination $agentDirectory

        $assetsDirectory = Join-Path $assetsStage $rid
        New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $scratch 'src/SharpDbg.AndroidAgent/obj/project.assets.json') -Destination (Join-Path $assetsDirectory 'project.assets.json')

        $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'SharpDbg.AndroidAgent.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        $properties = & $DotNet msbuild $projectFile '-getProperty:_AndroidRuntimePackId,AndroidNETSdkVersion' "-p:RuntimeIdentifier=$rid" '-p:Configuration=Release' "-p:AndroidSdkDirectory=$AndroidSdkDirectory" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $runtimeVersion -or -not $properties.Properties._AndroidRuntimePackId -or -not $properties.Properties.AndroidNETSdkVersion) { throw "Could not determine runtime pack versions for $rid." }

        # The notices the packaged app ships beside each agent, and the metadata its SBOM needs. The
        # runtime packs themselves are not in a host's NuGet cache (they come with the Android
        # workload), which is why they travel inside this package.
        $runtimeId = "Microsoft.NETCore.App.Runtime.NativeAOT.$rid"
        $runtimeDirectory = Join-Path (Join-Path $packageRoot $runtimeId.ToLowerInvariant()) $runtimeVersion.ToLowerInvariant()
        $androidId = "Microsoft.Android.Runtime.NativeAOT.$($properties.Properties._AndroidRuntimePackId).$rid"
        $androidDirectory = Join-Path (Join-Path $dotnetRoot "packs/$androidId") $properties.Properties.AndroidNETSdkVersion
        $packs = @(
            @{ Id = $runtimeId; Version = $runtimeVersion; Directory = $runtimeDirectory; Name = 'dotnet' },
            @{ Id = $androidId; Version = $properties.Properties.AndroidNETSdkVersion; Directory = $androidDirectory; Name = 'android' }
        )
        foreach ($pack in $packs) {
            $noticeDirectory = Join-Path (Join-Path $agentDirectory 'licenses') $pack.Name
            New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
            foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
                $noticeSource = Join-Path $pack.Directory $notice
                if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) { throw "Missing $notice in runtime pack $($pack.Id)." }
                Copy-Item -LiteralPath $noticeSource -Destination $noticeDirectory
            }
            $nuspec = Get-ChildItem -LiteralPath $pack.Directory -Filter '*.nuspec' -File | Select-Object -First 1
            if (-not $nuspec) { throw "Missing NuGet metadata for runtime pack $($pack.Id)." }
            $metadata = Join-Path (Join-Path $metadataStage $pack.Id.ToLowerInvariant()) $pack.Version.ToLowerInvariant()
            New-Item -ItemType Directory -Path $metadata -Force | Out-Null
            # NuGet's pack task drops every .nuspec file from the package (it treats them as package
            # manifests), so the consumer's SPDX fallback looks for <id>.nuspec.xml instead.
            Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $metadata "$($pack.Id).nuspec.xml")
            Copy-Item -LiteralPath (Join-Path $pack.Directory 'LICENSE.TXT') -Destination $metadata
            $runtimeAssets = @{ targets = @{ native = @{ "$($pack.Id)/$($pack.Version)" = @{ type = 'package' } } } }
            $runtimeAssetsDirectory = Join-Path $assetsDirectory "runtime-$($pack.Name)"
            New-Item -ItemType Directory -Path $runtimeAssetsDirectory -Force | Out-Null
            $runtimeAssets | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeAssetsDirectory 'project.assets.json') -Encoding utf8
        }
    }

    # Every package the agent's own restore resolved, with the license file its nuspec names. A host
    # restoring only this package would not have the Android-only entries in its cache.
    foreach ($rid in $RuntimeIdentifiers) {
        $assets = Get-Content -LiteralPath (Join-Path (Join-Path $assetsStage $rid) 'project.assets.json') -Raw | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $parts = $library.Name -split '/', 2
            $packageDirectory = Join-Path (Join-Path $packageRoot $parts[0].ToLowerInvariant()) $parts[1].ToLowerInvariant()
            $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $nuspec) { throw "Missing NuGet metadata for $($library.Name)." }
            $destination = Join-Path (Join-Path $metadataStage $parts[0].ToLowerInvariant()) $parts[1].ToLowerInvariant()
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $destination "$($parts[0]).nuspec.xml")
            [xml]$package = Get-Content -LiteralPath $nuspec.FullName -Raw
            $license = $package.SelectSingleNode("//*[local-name()='license']")
            if ($license -and $license.type -eq 'file') {
                $licenseSource = Join-Path $packageDirectory $license.InnerText.Trim()
                if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) { throw "Missing license file for $($library.Name)." }
                $licenseTarget = Join-Path $destination $license.InnerText.Trim()
                New-Item -ItemType Directory -Path (Split-Path -Parent $licenseTarget) -Force | Out-Null
                Copy-Item -LiteralPath $licenseSource -Destination $licenseTarget
            }
        }
    }

    # A project that only packs: nothing is compiled, the staged tree is the payload.
    $packProject = Join-Path $scratch 'pack.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <!-- The pack project sits beside the scratch copy of src/, so the default glob would compile
         SharpDbg's sources into this project (without their package references). It packs only. -->
    <EnableDefaultItems>false</EnableDefaultItems>
    <NoWarn>`$(NoWarn);NU5128</NoWarn>
    <PackageId>LeXtudio.SharpDbg.AndroidAgent</PackageId>
    <Version>$Version</Version>
    <Authors>lextudio</Authors>
    <Description>The SharpDbg NativeAOT debug agents for Android (arm64-v8a, x86_64, armeabi-v7a), with the license and SBOM metadata OpenDevelop needs to redistribute them.</Description>
    <PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <RepositoryUrl>https://github.com/lextudio/sharpdbg</RepositoryUrl>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <PackageProjectUrl>https://github.com/lextudio/sharpdbg</PackageProjectUrl>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$(Join-Path $agentStage '**')" Pack="true" PackagePath="agent" />
    <None Include="$(Join-Path $spdxStage '**')" Pack="true" PackagePath="spdx" />
    <None Include="$(Join-Path $root 'LICENSE.txt')" Pack="true" PackagePath="" />
    <None Include="$(Join-Path $root 'README.md')" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $packProject -Encoding utf8

    Write-Host "Packing LeXtudio.SharpDbg.AndroidAgent $Version"
    & $DotNet pack $packProject -c Release -o $OutputDirectory -tl:off
    if ($LASTEXITCODE -ne 0) { throw 'Packing LeXtudio.SharpDbg.AndroidAgent failed.' }
    Get-ChildItem -LiteralPath $OutputDirectory -Filter 'LeXtudio.SharpDbg.AndroidAgent.*.nupkg' |
        ForEach-Object { Write-Host "    $($_.FullName)" }
} finally {
    Set-Location $originalDirectory
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
