# DesktopTool 2.8.43 static security audit

Audit target: the locally installed `DesktopTool` 2.8.43 files.

## Summary

No clear backdoor or data-exfiltration implementation was found in the managed main
assembly or the managed uninstaller. Microsoft Defender reported no threats for the
installed directory. The main application and uninstallers are unsigned, so publisher
identity and supply-chain integrity cannot be established from their signatures.

## Network behavior

- No `HttpClient`, `WebClient`, `WebRequest`, socket, TCP, UDP or DNS client usage was
  found in the managed main source.
- The only concrete external URL found is `https://icaogu.cn/desktop/index/index.html`,
  opened in the user's default browser from an About/Donate action.
- No dynamic assembly loading or downloaded-code execution was found.

## Sensitive/invasive behavior found

- moves, hides, restores and deletes desktop files as part of desktop organization;
- changes Explorer hidden/super-hidden settings and later restores them;
- supports auto-start through a Startup shortcut and HKCU Run value;
- supports a logon recovery scheduled task and a recovery batch file;
- can close Explorer windows that point at the desktop during recovery diagnostics;
- launches Explorer, PowerShell fallback scripts, `schtasks.exe`, and Everything;
- launches bundled Everything elevated and can silently run its installer path;
- copies selected file paths/files to the clipboard;
- uses global hotkeys and mouse-state polling.

These behaviors match advertised desktop-management/search/recovery features, but bugs
in move/recovery logic could still cause data loss. Testing should use portable mode and
disposable files until the file-state machinery is refactored and covered by tests.

## Potentially alarming APIs that were not used

`VirtualAllocEx`, `WriteProcessMemory`, `ReadProcessMemory` and low-level keyboard-hook
declarations exist in `NativeMethods`, but no call sites were found in the application.
No `CreateRemoteThread` usage was found.

## Bundled native component

The retained `Everything64.dll` and `everything1.5.exe` are Authenticode-signed by
`voidtools PTY LTD`. Their SHA-256 values are recorded in `THIRD-PARTY-NOTICES.md`.

## Limits

This is static analysis plus an antivirus scan, not proof of absence. Native runtime,
installer/bootstrapper and Windows API behavior would require sandboxed dynamic tracing
for stronger assurance. The reconstructed build deliberately avoids sharing application
data with the installed build and disables silent persistence repair on startup.
