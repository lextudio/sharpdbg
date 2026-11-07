#!/usr/bin/env pwsh
#
# Packs the DAP adapter OpenDevelop consumes into a NuGet package, so the IDE stops building this
# repository from source on every build (Debugger.AddIn.csproj's BuildSharpDbg target runs a nested
# `dotnet build` for each configuration) and can restore a pinned version instead.
#
# The package is deliberately NOT a dotnet tool: OpenDevelop copies the adapter's whole dependency
# closure next to the add-in and launches it out of process, resolving everything through
# SharpDbg.Cli.deps.json from that folder. So the publish output goes in whole, under lib/net10.0,
# and the consumer copies it with ExcludeAssets="all" + GeneratePathProperty.
#
# Usage:
#   ./iac/pack-opendevelop-cli.ps1 -Version 1.0.0-lextudio.local
#   ./iac/pack-opendevelop-cli.ps1 -Version 1.0.0-lextudio.123.1 -NuGetConfigPath /tmp/dap-nuget.config
#
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    # Defaults to artifacts/packages, the folder the publishing workflow uploads.
    [string]$OutputDirectory = '',
    # A restore configuration that can see the OmniSharp DAP fork (GitHub Packages in CI, the local
    # feed on a developer machine). Without it the repository's own nuget.config applies, which
    # only knows nuget.org and the local feed.
    [string]$NuGetConfigPath = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts/packages'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$stage = Join-Path ([IO.Path]::GetTempPath()) ('sharpdbg-cli-pack-' + [guid]::NewGuid().ToString('N'))
try {
    $cliOutput = Join-Path $stage 'cli'
    $publishArguments = @('publish', (Join-Path $root 'src/SharpDbg.Cli/SharpDbg.Cli.csproj'), '-c', 'Release', '-o', $cliOutput, '-tl:off')
    if ($NuGetConfigPath) {
        $publishArguments += "-p:RestoreConfigFile=$NuGetConfigPath"
    }
    Write-Host '==> Publishing SharpDbg.Cli'
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publishing SharpDbg.Cli failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $cliOutput 'SharpDbg.Cli.dll') -PathType Leaf)) {
        throw "The publish output has no SharpDbg.Cli.dll: $cliOutput"
    }

    # A project that only packs: nothing is compiled, the staged publish output is the payload.
    $packProject = Join-Path $stage 'pack.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <NoWarn>`$(NoWarn);NU5128</NoWarn>
    <PackageId>LeXtudio.SharpDbg.Cli</PackageId>
    <Version>$Version</Version>
    <Authors>lextudio</Authors>
    <Description>The SharpDbg DAP adapter and its dependency closure, as consumed by OpenDevelop.</Description>
    <PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <RepositoryUrl>https://github.com/lextudio/sharpdbg</RepositoryUrl>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <PackageProjectUrl>https://github.com/lextudio/sharpdbg</PackageProjectUrl>
  </PropertyGroup>
  <ItemGroup>
    <None Include="$(Join-Path $cliOutput '**')" Pack="true" PackagePath="lib/net10.0" />
    <None Include="$(Join-Path $root 'LICENSE.txt')" Pack="true" PackagePath="" />
    <None Include="$(Join-Path $root 'README.md')" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $packProject -Encoding utf8

    Write-Host "==> Packing LeXtudio.SharpDbg.Cli $Version"
    & dotnet pack $packProject -c Release -o $OutputDirectory -tl:off
    if ($LASTEXITCODE -ne 0) { throw 'Packing LeXtudio.SharpDbg.Cli failed.' }
    Get-ChildItem -LiteralPath $OutputDirectory -Filter 'LeXtudio.SharpDbg.Cli.*.nupkg' |
        ForEach-Object { Write-Host "    $($_.FullName)" }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
