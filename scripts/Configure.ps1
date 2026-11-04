param(
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'dist'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$taskShortcutPath = (Resolve-Path -LiteralPath $ShortcutPath).Path
if ([IO.Path]::GetExtension($taskShortcutPath) -ne '.lnk') { throw 'Select the installed Edge ChatGPT web-app .lnk shortcut.' }
$taskWsh = New-Object -ComObject WScript.Shell
$taskShell = New-Object -ComObject Shell.Application
try {
    $taskLink = $taskWsh.CreateShortcut($taskShortcutPath)
    if ([IO.Path]::GetFileName($taskLink.TargetPath) -notin @('msedge_proxy.exe', 'msedge.exe')) { throw 'The shortcut must launch Microsoft Edge.' }
    $taskProfile = [regex]::Match($taskLink.Arguments, '--profile-directory=(?:"([^"]+)"|(\S+))')
    $taskApp = [regex]::Match($taskLink.Arguments, '--app-id=([a-p]{32})(?:\s|$)')
    if (-not $taskProfile.Success -or -not $taskApp.Success) { throw 'The shortcut must contain an Edge profile and installed PWA app ID.' }
    $taskFolder = $taskShell.NameSpace((Split-Path -Parent $taskShortcutPath))
    $taskItem = $taskFolder.ParseName((Split-Path -Leaf $taskShortcutPath))
    $taskWindowId = [string]$taskItem.ExtendedProperty('System.AppUserModel.ID')
    if ($taskWindowId -notmatch '^chatgpt\.com-[A-Za-z0-9_!.-]+$') { throw 'The shortcut does not expose the installed ChatGPT PWA identity.' }
    $taskProfileName = if ($taskProfile.Groups[1].Success) { $taskProfile.Groups[1].Value } else { $taskProfile.Groups[2].Value }
    if ($taskProfileName -notmatch '^[A-Za-z0-9 _-]+$') { throw 'Unsupported Edge profile directory.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $taskConfigPath = Join-Path $OutputDirectory 'config.json'
    if ((Test-Path -LiteralPath $taskConfigPath) -and -not $Force) { throw 'Configuration already exists; use -Force to replace it.' }
    $taskConfig = [ordered]@{ EdgePath = $taskLink.TargetPath; ProfileDirectory = $taskProfileName; PwaAppId = $taskApp.Groups[1].Value; WindowAppId = $taskWindowId }
    [IO.File]::WriteAllText($taskConfigPath, ($taskConfig | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Write-Output "Local configuration saved: $taskConfigPath (do not commit this file)."
} finally {
    foreach ($taskCom in @($taskItem, $taskFolder, $taskLink, $taskShell, $taskWsh)) {
        if ($null -ne $taskCom) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($taskCom) }
    }
}
