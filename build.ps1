<#
.SYNOPSIS
  Builds StigGpoManager.exe and runs the unit tests. Run on a CONNECTED build machine
  (Windows with the .NET 8 SDK); NuGet is only needed here, never in the enclave.
.OUTPUTS
  dist\StigGpoManager.exe and dist\StigGpoManager.exe.sha256 (record the hash for the transfer).
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not $SkipTests) {
    dotnet test tests\StigGpoManager.Tests\StigGpoManager.Tests.csproj -c Release
    if ($LASTEXITCODE) { throw "Tests failed." }
}
dotnet build src\App\StigGpoManager.csproj -c Release
if ($LASTEXITCODE) { throw "Build failed." }
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item src\App\bin\Release\net48\StigGpoManager.exe dist\ -Force
Copy-Item config.sample.json dist\ -Force
$hash = (Get-FileHash dist\StigGpoManager.exe -Algorithm SHA256).Hash
"$hash  StigGpoManager.exe" | Set-Content dist\StigGpoManager.exe.sha256 -Encoding ASCII
Write-Host "dist\StigGpoManager.exe  SHA-256 $hash"
