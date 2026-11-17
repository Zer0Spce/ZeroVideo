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

# The Microsoft.Playwright NuGet driver lives in a hidden .playwright folder.
# Compress-Archive skips hidden files/folders; losing this directory makes
# Generate Ideas fail at runtime with "Driver not found: ...node.exe".
$driver = Join-Path $out '.playwright\node\win32_x64\node.exe'
if (-not (Test-Path -LiteralPath $driver -PathType Leaf)) {
    throw "Published portable build is missing the Playwright driver: $driver"
}
Write-Host "Bundled Playwright driver: $driver" -ForegroundColor Green

# ZipFile.CreateFromDirectory preserves hidden files (including .playwright).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries |
        Where-Object { $_.FullName -eq '.playwright/node/win32_x64/node.exe' } |
        Select-Object -First 1
    if ($null -eq $entry -or $entry.Length -eq 0) {
        throw 'Playwright driver missing or empty inside portable ZIP.'
    }
}
finally { $archive.Dispose() }
Write-Host "Portable build includes the hidden Playwright driver: $zip" -ForegroundColor Green
