# DNGuard.Capture 1.0.1 hotfix

The original 1.0 build returned immediately on every preflight failure. That made
drag-and-drop look like the tool never started. It also required a manually dumped
module only to read the MVID and enabled `DG_FORCEJIT_AUTOEXIT=1` by default.

Version 1.0.1 changes the capture behavior:

1. The console waits for Enter before closing unless `--no-pause` is supplied.
2. A dump is optional for capture. The protected target's metadata supplies MVID.
3. Auto-exit is disabled by default. Close the target after `SWEEP COMPLETE`.
4. Existing launcher/shim/ForceJit payloads are auto-detected in common old tool folders.
5. ForceJit output is tailed live and stored in the session folder.

Recommended drag-and-drop behavior:

```text
Drop target.exe on DNGuard.Capture.exe
→ target launches
→ wait for SWEEP COMPLETE
→ close target manually
→ capture console prints the session summary
→ press Enter to close capture console
```

Explicit auto-exit mode remains available:

```text
DNGuard.Capture.exe target.exe --auto-exit
```
