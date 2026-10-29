<#  Builds, tests and packages DeepVault on Windows.
    Requirements: .NET 10 SDK (https://dotnet.microsoft.com/download).  Run:  powershell -ExecutionPolicy Bypass -File build.ps1  #>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'artifacts'
$pkg = Join-Path $out 'DeepVault-1.0.1-win-x64'
if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
if (-not $SkipTests) { dotnet test tests/DeepVault.Tests -c Release; if ($LASTEXITCODE) { throw 'tests failed' } }
dotnet publish src/DeepVault.App -c Release -r win-x64 --self-contained true -o $pkg; if ($LASTEXITCODE) { throw 'publish failed' }
dotnet publish src/DeepVault.Cli -c Release -r win-x64 --self-contained true -o $pkg; if ($LASTEXITCODE) { throw 'publish failed' }
Copy-Item README.md, LICENSE.txt, THIRD_PARTY_NOTICES.txt $pkg
Copy-Item docs (Join-Path $pkg 'docs') -Recurse
New-Item -ItemType Directory -Force (Join-Path $pkg 'test-images') | Out-Null
Copy-Item tests/fixtures/images/*.gz, tests/fixtures/images/manifest.json (Join-Path $pkg "test-images")
Compress-Archive -Path $pkg -DestinationPath "$pkg.zip" -Force
Write-Host "Package: $pkg.zip"
