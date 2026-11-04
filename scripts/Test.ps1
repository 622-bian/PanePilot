param([switch]$Native, [switch]$Resident)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskRoot = Split-Path $PSScriptRoot
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$taskSources += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'tests') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$taskEntry = if ($Resident) { 'ResidentSmoke' } elseif ($Native) { 'NativeLayoutSmoke' } else { 'LayoutTests' }
$taskTarget = if ($Native -or $Resident) { 'winexe' } else { 'exe' }
$taskOutputDirectory = Join-Path $taskRoot 'dist\tests'
New-Item -ItemType Directory -Path $taskOutputDirectory -Force | Out-Null
$taskOutput = Join-Path $taskOutputDirectory ($taskEntry + '.exe')
& (Join-Path $taskFramework 'csc.exe') /nologo "/target:$taskTarget" /optimize+ /platform:x64 "/main:$taskEntry" "/out:$taskOutput" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll "/reference:$taskFramework\WPF\UIAutomationClient.dll" "/reference:$taskFramework\WPF\UIAutomationTypes.dll" "/reference:$taskFramework\WPF\WindowsBase.dll" @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($Native -or $Resident) { Write-Output "Built optional desktop test: $taskOutput"; return }
& $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
