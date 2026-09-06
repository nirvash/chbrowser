---
name: chbrowser-release-deploy
description: Build, publish, and locally deploy the ChBrowser release artifact. Use only when a release or deployment is explicitly requested; do not push, create releases, or stop the daily app implicitly.
---

# ChBrowser release and deployment

Use the repository's release target and preserve the user's authority boundaries. `LOCAL.md` is machine-specific and may be read for the deployment path, but must not be committed.

## Build

- For Debug verification, use the normal `src/ChBrowser/bin/Debug/` output; do not use a separated `BaseOutputPath`.
- Publish with:

```pwsh
dotnet publish src/ChBrowser/ChBrowser.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

- Treat `src/ChBrowser/bin/Release/net8.0-windows/win-x64/publish/ChBrowser.exe` as the expected artifact. Check the command exit code and report warnings/errors explicitly.

## Local deployment

1. Resolve the deployment directory from `LOCAL.md` and verify the exact source and destination paths.
2. Confirm the daily ChBrowser process is not running. If a stop is necessary, identify the executable path and PID first; stop only the verified Debug PID. Never kill by process name, and never stop the Release/publish daily executable under the Debug exception.
3. Copy the published `ChBrowser.exe` to the deployment directory.
4. Copy `themes/default/` to `<deployment>\data\themes\default\`. The repository `themes/` directory is versioned distribution content; the app does not read it directly.
5. Compare source and deployed SHA-256 values and report the exact paths and hashes.

Deployment is not runtime or UI validation. If the user requests that validation, perform it separately and report its evidence.
