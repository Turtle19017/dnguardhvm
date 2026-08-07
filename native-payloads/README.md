# Native/runtime capture payloads

`DNGuard.Capture.exe` is a .NET 8 console front-end, but runtime JIT interception
still requires the existing capture payloads:

- `DNGuardJitLauncher.exe`
- `DNGuardJitShim.dll`
- `ForceJit.dll`

Place these files in one of the following locations:

1. Beside `DNGuard.Capture.exe`.
2. In `payloads\` beside `DNGuard.Capture.exe`.
3. In `<target-dir>\DNGuardTools\`.
4. In `<target-dir>\_dnguard\<target>\tools\`.
5. In the directory named by the `DNGUARD_TOOLS` environment variable.

The native shim cannot be replaced by managed C# because it must intercept the
native CLR JIT boundary. The launcher/orchestration, indexing, rebuilding,
validation and workspace detection are managed .NET console applications.
