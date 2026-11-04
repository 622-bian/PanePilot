param([Parameter(Mandatory = $true)][string]$Message)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
Push-Location $taskRoot
try {
    # The staging allowlist excludes local deployment, personal settings and generated files.
    $taskAllowed = '^(src/[^/]+\.cs|tests/[^/]+\.cs|scripts/[^/]+\.ps1|docs/[^/]+\.md|\.github/workflows/[^/]+\.yml|README\.md|LICENSE|CONTRIBUTING\.md|AGENTS\.md|\.gitignore|config\.example\.json)$'
    $taskPreStaged = @(git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the Git index.' }
    foreach ($taskFile in $taskPreStaged) {
        if ($taskFile -notmatch $taskAllowed) { throw "Unexpected staged file: $taskFile. Review it before publishing." }
    }
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory (Join-Path $taskRoot 'dist\publish')
    & (Join-Path $PSScriptRoot 'Test.ps1')
    git add -- src tests scripts docs .github README.md LICENSE CONTRIBUTING.md .gitignore config.example.json
    if ($LASTEXITCODE -ne 0) { throw 'Staging failed.' }
    if (Test-Path -LiteralPath 'AGENTS.md') { git add -- AGENTS.md; if ($LASTEXITCODE -ne 0) { throw 'Staging AGENTS.md failed.' } }
    foreach ($taskFile in @(git diff --cached --diff-filter=ACMR --name-only)) {
        if ($taskFile -notmatch $taskAllowed) { throw "Unexpected staged file: $taskFile" }
        $taskBlob = git show (':' + $taskFile) 2>$null
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect staged file: $taskFile" }
        $taskText = $taskBlob -join "`n"
        if ($taskText -match 'github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|[A-Za-z]:\\+Users\\+[^\s"\\]+') {
            throw "Potential credential or personal path in $taskFile. Review before publishing."
        }
    }
    git diff --cached --check
    if ($LASTEXITCODE -ne 0) { throw 'Diff check failed.' }
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        git commit -m $Message
        if ($LASTEXITCODE -ne 0) { throw 'Commit failed.' }
    } elseif ($LASTEXITCODE -ne 0) { throw 'Cannot inspect staged changes.' }
    git push
    if ($LASTEXITCODE -ne 0) { throw 'Push failed. The local commit is preserved; resolve remote changes and retry without force-push.' }
    Write-Output 'Published. Check the GitHub Actions run for this commit.'
} finally { Pop-Location }
