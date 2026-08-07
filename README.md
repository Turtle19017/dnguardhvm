# DNGuard Console Suite 1.0.1

A set of separate .NET 8 console applications for the existing DNGuard HVM
capture/rebuild workflow. Each executable accepts the protected target as its
first argument, so the target can be dragged and dropped onto the tool.

## Included tools

| Tool | Purpose |
|---|---|
| `DNGuard.Capture.exe` | Starts the target under the existing JIT shim and ForceJit startup hook. Creates a timestamped corpus session. |
| `DNGuard.Index.exe` | Consolidates the latest capture corpus into a persistent `by-token` index. C# only; no Python. |
| `DNGuard.Rebuild.exe` | Runs the v0.8.6 reconstruction engine using the detected dump and index. |
| `DNGuard.Validate.exe` | Re-runs post-write semantic validation against the latest rebuilt DLL. |
| `DNGuard.Status.exe` | Shows detected dump, current capture, index statistics, rebuilt DLL and validation metrics. |

`DNGuard.Common.dll` and `DNGuard.Engine.dll` are build-time/runtime dependencies
that are bundled into each published single-file executable.


## Capture 1.0.1 behavior

`DNGuard.Capture.exe` no longer requires the dump file. If no dump is detected,
it reads the MVID from the protected target and continues. Drag-and-drop runs pause
before closing so preflight and launcher errors remain visible.

ForceJit auto-exit is disabled by default. Keep the target open until the live log
shows `SWEEP COMPLETE`, then close the target manually. To enable the old behavior:

```text
DNGuard.Capture.exe target.exe --auto-exit
```

Capture payloads are also searched recursively in `C:\tool_dng`, `C:\Tools`, and
the current user's Downloads directory.

## Default workflow

Drag the same target EXE onto these tools in order:

```text
DNGuard.Capture.exe
DNGuard.Index.exe
DNGuard.Rebuild.exe
DNGuard.Validate.exe
DNGuard.Status.exe
```

All tools auto-detect the shared workspace. No PowerShell, BAT, Bash or Python
file is used at runtime.

## Dump detection

The rebuild engine needs the manually dumped metadata module. Detection order:

1. Explicit `--dump <path>`.
2. `<target-dir>\_dnguard\<target>\dump\<same-file>`.
3. `<target-dir>\Dumps\<same-file>`.
4. `<target-dir>\Dumps\<same-name>.dll` for an EXE target.
5. `<target-name>.dumped.exe` or `<target-name>.dump.exe` beside the target.

This suite intentionally does not automate dumping, matching the current manual
dump workflow.

## Build

Requirements:

- Windows 10/11 x64.
- .NET 8 SDK, or Visual Studio 2022 with .NET desktop/build tools.

Open `DNGuard.Tools.sln`, select Release, then publish the five console projects.
Command-line examples:

```powershell
dotnet publish .\src\DNGuard.Capture\DNGuard.Capture.csproj -c Release
dotnet publish .\src\DNGuard.Index\DNGuard.Index.csproj -c Release
dotnet publish .\src\DNGuard.Rebuild\DNGuard.Rebuild.csproj -c Release
dotnet publish .\src\DNGuard.Validate\DNGuard.Validate.csproj -c Release
dotnet publish .\src\DNGuard.Status\DNGuard.Status.csproj -c Release
```

Each single-file EXE is created under:

```text
src\<project>\bin\Release\net8.0\win-x64\publish\
```

## Capture payloads

Copy the existing files beside `DNGuard.Capture.exe`:

```text
DNGuardJitLauncher.exe
DNGuardJitShim.dll
ForceJit.dll
```

See `native-payloads\README.md` for all supported detection locations. Only the
native shim remains native because CLR JIT interception cannot be implemented as
pure managed C#.

## Tool examples

```powershell
DNGuard.Capture.exe "D:\LordsBot-Release\LordsMobileBot.exe"
DNGuard.Index.exe "D:\LordsBot-Release\LordsMobileBot.exe"
DNGuard.Rebuild.exe "D:\LordsBot-Release\LordsMobileBot.exe"
DNGuard.Validate.exe "D:\LordsBot-Release\LordsMobileBot.exe"
DNGuard.Status.exe "D:\LordsBot-Release\LordsMobileBot.exe"
```

Useful overrides:

```powershell
DNGuard.Capture.exe target.exe --warmup 45000 --passes 3
DNGuard.Index.exe target.exe --corpus "D:\custom-capture"
DNGuard.Rebuild.exe target.exe --dump "D:\Dumps\target.exe" --eh flatten
DNGuard.Validate.exe --assembly "D:\custom\target.rebuilt.dll"
DNGuard.Status.exe target.exe --json
```

## Console logging

- Capture output is streamed from the launcher and colored by severity/event.
- Index emits one colored line per indexed method unless `--quiet` is used.
- Rebuild emits one line per rebuilt method with token, IL size, patched token
  count, fixed references and remaining unmapped operands.
- Complete logs are retained in the workspace `logs\` directory.

## Current engine

The suite embeds the DNGuardRebuilder v0.8.6 direct-producer-closure source and
`dnlib.dll`. Rebuild still applies the same conservative field consensus,
generic operand closure, array provenance and semantic validator used by the
existing v0.8.6 command-line build.
