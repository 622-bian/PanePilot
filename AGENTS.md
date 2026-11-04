# PanePilot project workflow

- Make small changes, inspect Git status/diff, and keep personal configuration, browser data and runtime logs out of Git.
- For completed project changes, the owner has authorized automatic local commit and push to the configured GitHub remote. Use `scripts/Publish.ps1 -Message "..."` after relevant desktop verification, then inspect the GitHub Actions result. Do not force-push, reset unrelated changes, or publish files outside the reviewed allowlist.
- This is a completion-time publishing workflow, not a background file watcher. Incomplete edits must not be automatically committed.
- Build and unit tests are not proof of Win32 behavior. Changes involving window state, DPI, foreground focus or undo require relevant native tests and a clear account of what was not verified.
- Never read or log chat text. Only resize the captured foreground window when the user invokes the split feature; verify its identity and state before changing it. Keep undo and failure rollback observable in logs.
