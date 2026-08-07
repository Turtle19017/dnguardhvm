# Changelog

## 1.0.0

- Split the workflow into independent .NET 8 console applications.
- Added automatic workspace creation beside the protected target.
- Added timestamped capture sessions and `current.json` discovery.
- Added C# corpus index generation with union token maps and hint reports.
- Embedded the v0.8.6 rebuild/validator engine in a shared library.
- Added drag-and-drop target handling to every user-facing executable.
- Added colored per-method console logs and persistent log files.
- Removed runtime PowerShell, BAT, Bash and Python dependencies.

## 1.0.1 — Capture drag-and-drop hotfix

- Keep the console open after drag-and-drop unless `--no-pause` is used.
- Do not require a dump during capture; read MVID from the protected target when no dump exists.
- Disable ForceJit auto-exit by default. Use `--auto-exit` explicitly to restore it.
- Auto-discover capture payloads recursively under `C:\tool_dng`, `C:\Tools`, and the user's Downloads folder.
- Write ForceJit log/checkpoint/done files inside the timestamped capture session.
- Tail `forcejit.log` in real time with colored console output.
- Record whether the sweep completed in `capture\current.json`.
- Preserve partial captures and report exact launcher/sweep/method status instead of closing silently.
