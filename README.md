# DesktopTool 2.8.43 reconstructed source

> Community reconstruction for interoperability, security research and continued
> maintenance. This repository is not affiliated with or endorsed by the original
> DesktopTool author.

This tree was reconstructed from the locally installed .NET 8/WPF binaries and PDB.
It is not the author's original repository: comments, local variable names and original
XAML formatting cannot be recovered exactly.

## Layout

- `src/` - buildable decompiled C# project and embedded WPF BAML/resources
- `xaml-recovered/` - editable XAML recovered from BAML
- `audit/` - extracted uninstaller source and static-analysis artifacts
- `original-metadata/` - copied assembly metadata used during reconstruction
- `third_party/Everything/` - signed voidtools file-search dependency and license
- `dist/DesktopTool-Reconstructed/` - self-contained Windows x64 build

## Build

Run in PowerShell:

```powershell
& '.\build.ps1'
```

The script uses .NET SDK 8, publishes a self-contained Windows x64 application and
creates `portable.txt`. Runtime data therefore stays under the build's `Data/`
directory and does not share settings/history with the installed private build.

## Deliberate safety changes

- reconstructed data directory and mutex are separate from the installed application;
- automatic startup-registry repair and recovery-task repair are disabled on launch;
- automatic Explorer-window closing diagnostics are disabled on launch;
- startup entries created manually from Settings use reconstructed-only names;
- ClosedXML is restored from NuGet rather than loaded from the private installation.

The decompiler damaged some non-XAML Chinese string literals. The recovered XAML is
available for UI cleanup, but a later refactor should replace embedded BAML with normal
WPF `Page` items and correct remaining text literals.

## Recent reconstruction fixes

- Fixed startup resource loading and static data-path initialization.
- Added a visible **更改名称** action to the collection-zone options menu.
- Kept collection-zone data isolated in portable `Data/` storage.
- Preserved portable data automatically when running `build.ps1` again.
