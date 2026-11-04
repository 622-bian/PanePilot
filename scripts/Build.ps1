param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskRoot = Split-Path $PSScriptRoot
$taskSources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& (Join-Path $taskFramework 'csc.exe') /nologo /target:winexe /optimize+ /platform:x64 "/out:$OutputDirectory\PanePilot.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll "/reference:$taskFramework\WPF\UIAutomationClient.dll" "/reference:$taskFramework\WPF\UIAutomationTypes.dll" "/reference:$taskFramework\WPF\WindowsBase.dll" @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Stop a running public build before replacing its executable.' }
Write-Output "Built: $OutputDirectory\PanePilot.exe"
