# Builds the friend package: D:\Tools\phasmo-reader-friend\PhasmoReader.zip
# Sounds on screen only (reader + overlay), .NET included, nothing to install, no game files inside.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$outRoot = 'D:\Tools\phasmo-reader-friend'
$pkg = Join-Path $outRoot 'PhasmoReader'

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

Write-Host 'Publishing the reader...'
dotnet publish "$repo\reader\PhasmoReader\PhasmoReader.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o "$pkg\reader" -v q -nologo
if ($LASTEXITCODE) { throw 'reader publish failed' }

Write-Host 'Publishing the overlay...'
dotnet publish "$repo\overlay\OtherSideSound.csproj" -c Release -r win-x64 --self-contained `
    -p:DebugType=none -o "$pkg\overlay" -v q -nologo
if ($LASTEXITCODE) { throw 'overlay publish failed' }

Copy-Item "$repo\overlay\default-config.json" "$pkg\overlay\config.json"
Copy-Item "$PSScriptRoot\Start Phasmo Reader.cmd", "$PSScriptRoot\README.txt" $pkg

$zip = Join-Path $outRoot 'PhasmoReader.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip
Write-Host ("Done: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
