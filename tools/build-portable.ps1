$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 7) { $PSNativeCommandUseErrorActionPreference = $true }
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\ZeroVideo-win-x64'
$zip = Join-Path $root 'dist\ZeroVideo-win-x64.zip'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
New-Item $out -ItemType Directory -Force | Out-Null

dotnet publish "$root\src\ZeroVideo.App\ZeroVideo.App.csproj" -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item "$root\profiles" "$out\profiles" -Recurse -Force
Copy-Item "$root\README.md" "$out\README.md" -Force
Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable build: $zip" -ForegroundColor Green
