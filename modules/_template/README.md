# Hello template (C#, .NET 8)

Copy this folder to start a new module. C# on .NET 8, self-contained, is the default language for new DeskKit modules.

Build: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`. It publishes one self-contained `bin\Hello.exe` with the version from `module.json`. It needs the .NET SDK 8 or newer and, the first time, network access for the win-x64 runtime pack.

Run alone: `bin\Hello.exe [name]`. It prints a greeting and exits 0. Wrong arguments exit 2.

- Settings: `%APPDATA%\DeskKit\template\settings.json`, key `greeting` (default `Hello`). Written with defaults on first run.
- `src/Hello/SingleInstance.cs`: the single-instance code for tray and window modules (contract 7.2).

Steps: `docs/module-contract.md`, section 14.
