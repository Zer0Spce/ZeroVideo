$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Write-Host "ZeroVideo developer setup" -ForegroundColor Green
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host ".NET SDK was not found. Installing .NET 10 SDK with winget..."
  winget install --id Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements
}
dotnet restore "$root\ZeroVideo.sln"
Write-Host "Restore complete. Microsoft Edge is used for browser automation." -ForegroundColor Green
