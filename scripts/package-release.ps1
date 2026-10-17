param([Parameter(Mandatory=$true)][string]$Version, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^v\d+\.\d+\.\d+-[0-9A-Za-z.-]+$') { throw 'A versioned prerelease is required' }
$repo = Split-Path $PSScriptRoot -Parent
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot resolve source revision' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output directory must be new' }
$payload = Join-Path $output 'payload'
$plugin = Join-Path $payload 'plugins/TelematicPlugin'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'bin/Release/net8.0/TelematicPlugin.dll') -Destination $plugin
Copy-Item -LiteralPath (Join-Path $repo 'plugin_telematic_cfg.reference.yml') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $payload 'INSTALL.md')
[ordered]@{ version=$Version; revision=$revision; target='net8.0'; assettoServer='0.0.54'; assettoServerRevision='51737e2c2ee892bc7800df479517eaaf38a828fc'; unsigned=$true; dependencies='Host-supplied dependencies; no additional plugin package references'; createdUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'BUILD.json') -Encoding UTF8
$dllHash = (Get-FileHash -LiteralPath (Join-Path $plugin 'TelematicPlugin.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
"$dllHash  plugins/TelematicPlugin/TelematicPlugin.dll" | Set-Content -LiteralPath (Join-Path $payload 'checksums.sha256') -Encoding ASCII
$zip = Join-Path $output 'telematic-assettoserver-plugin-windows.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  telematic-assettoserver-plugin-windows.zip" | Set-Content -LiteralPath (Join-Path $output 'checksums.sha256') -Encoding ASCII
Write-Output "$zip SHA256=$hash revision=$revision"
