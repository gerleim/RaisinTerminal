# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git Commits

Do not append `Co-Authored-By` trailers to commit messages.

## Shell Commands

**Never `cd` to the working directory before running commands.** The working directory is already set to the repo root — just run commands directly (e.g. `dotnet build RaisinTerminal.slnx`, not `cd /path/to/repo && dotnet build ...`).

## Build Commands

```bash
# Build
dotnet build RaisinTerminal.slnx

# Run
dotnet run --project RaisinTerminal/RaisinTerminal.csproj

# Test (xUnit)
dotnet test RaisinTerminal.Tests/RaisinTerminal.Tests.csproj

# Run a single test
dotnet test RaisinTerminal.Tests/RaisinTerminal.Tests.csproj --filter "FullyQualifiedName~TestMethodName"
```

## NuGet mode (for CI / public builds)

The project uses conditional references: by default it uses `ProjectReference` to sibling Raisin libraries (local dev). Pass `-p:UseProjectReferences=false` to switch to NuGet packages instead — this is how the public repo builds without the sibling folders.

```bash
# Build using NuGet packages instead of project references
dotnet build RaisinTerminal.slnx -p:UseProjectReferences=false
```

When bumping a library version, update the `PackageReference` version in the `Condition="'$(UseProjectReferences)' == 'false'"` ItemGroups across: `RaisinTerminal.csproj`, `RaisinTerminal.Core.csproj`, `LibrariesRaisin/Raisin.Core.csproj`, `LibrariesRaisin/Raisin.WPF.Base.csproj`.

When packing the libraries for NuGet, use `-p:UseProjectReferences=false` so the .nupkg declares NuGet dependencies instead of project paths:

```bash
dotnet pack LibrariesRaisin/Raisin.Core/Raisin.Core.csproj -p:UseProjectReferences=false -c Release
dotnet pack LibrariesRaisin/Raisin.WPF.Base/Raisin.WPF.Base.csproj -p:UseProjectReferences=false -c Release
```

Raisin.EventSystem has no Raisin dependencies, so it can be packed without the flag.

## Rebuild & Restart (when running inside RaisinTerminal)

When the user asks you to rebuild, publish, or restart the app, follow this two-step process:

**Step 1 — Build and test first (inside the terminal):**

```bash
dotnet build RaisinTerminal.slnx && dotnet test RaisinTerminal.Tests/RaisinTerminal.Tests.csproj
```

If the build or tests fail, **stop and fix the errors**. Do not proceed to Step 2.

**Step 2 — Only after a clean build and passing tests, run rebuild.bat:**

```bash
start "" "rebuild.bat"
```

This launches a detached process that:
1. Queries the running app via named pipe to check for busy terminal sessions
2. If sessions are busy, prompts the user for confirmation before proceeding
3. Kills the running RaisinTerminal process (which also kills your session)
4. Publishes a self-contained release to `%LOCALAPPDATA%\RaisinTerminal`
5. Starts the newly built app

**Important:** After running `rebuild.bat` your terminal session will be terminated. This is expected — the app will come back up automatically.

## Architecture

**WPF terminal emulator** (.NET 8, C#) using MVVM with AvalonDock for multi-pane layout.

### Three projects

- **RaisinTerminal** — WPF UI app (Views, ViewModels, Services, Controls)
- **RaisinTerminal.Core** — Terminal emulation engine (no UI dependency)
- **RaisinTerminal.Tests** — xUnit tests for Core

### Terminal pipeline

```
Keyboard → InputEncoder → ConPtySession (Win32 pseudoconsole) → child process
child process → output bytes → AnsiParser (state machine) → TerminalEmulator → TerminalBuffer → TerminalCanvas (WPF render)
```

- **ConPtySession** wraps Windows ConPTY via P/Invoke, launches child processes
- **AnsiParser** is a stateful parser (Ground/Escape/CSI/OSC states) handling VT100/ANSI sequences and UTF-8
- **TerminalEmulator** interprets parsed commands (cursor movement, SGR colors, erase ops, alternate screen buffer, etc.)
- **TerminalBuffer** maintains the character grid (CellData per cell) with scrollback (max 10,000 lines)
- **TerminalCanvas** is a custom WPF FrameworkElement that renders the grid using FormattedText with brush caching
- **InputEncoder** converts WPF key events to VT escape sequences

### UI layer

- **TerminalView** captures keyboard/mouse input, drives TerminalCanvas rendering
- **TerminalSessionViewModel** owns one ConPtySession + TerminalEmulator, reads output on a background thread, coalesces render requests at DispatcherPriority.Input. Also tracks Claude Code session state (name, status, `/clear` rename handling)
- **MainViewModel** manages the collection of terminal sessions, generates unique Claude session names via ProjectNameHelper
- **ProjectsPanelViewModel** — sidebar that organizes terminal sessions into project groups by working directory (longest prefix match), manages per-project attachments (clipboard image paste, drag-and-drop), and detects Claude Code session status (idle/working/waiting) by scanning the TUI screen state
- **LayoutService** persists window placement, AvalonDock layout XML, and per-session state (working dir, last command, alternate screen) to `%AppData%/RaisinTerminal/`
- **SettingsService** persists app settings (display options, attachment auto-cleanup) to `%AppData%/RaisinTerminal/settings.json`
- **TerminalSettingsRegistry** declarative registry of all user-facing settings with metadata (category, editor type, bounds) for the Options dialog
- **CommandHistoryService** singleton manages cross-session command history (max 500, persisted to file)
- **ProjectNameHelper** (in Core) generates abbreviated project names from CamelCase (e.g. `RaisinTerminal` → `RT`) and unique Claude session names (`RT 1`, `RT 2`)

### Key dependencies

- **Raisin.WPF.Base** — shared base library via project reference (ViewModelBase, layout helpers, window placement)
- **Raisin.EventSystem** — event bus
- **AvalonDock** — docking framework with VS2013 dark theme
- **System.IO.Pipelines** — efficient I/O for console output

### Rendering notes

RaisinTerminal is a full terminal emulator, not a proxy — it owns the entire rendering pipeline rather than delegating to a host terminal. This means any visual feature (character rendering, cursor shapes, selection highlights, etc.) must be implemented explicitly. For example, Unicode block drawing characters (U+2580–U+259F) are rendered as geometric primitives in `TerminalCanvas.TryDrawBlockChar` because WPF's FormattedText leaves gaps between adjacent glyphs. Cell dimensions are rounded to whole pixels with `SnapsToDevicePixels`/`UseLayoutRounding` to prevent sub-pixel seams. Consecutive empty lines can be compressed into a smaller visual space to reduce scrollback clutter.

### Debugging terminal rendering issues

When investigating visual glitches, garbled text, or unexpected content in the scrollback, use the per-session transcript logs at `%AppData%/RaisinTerminal/sessions/`:

- **`{ContentId}.txt`** — text-only transcript (printed characters and newlines, no escape sequences). Use this to verify what text was actually written and in what order.
- **`{ContentId}.raw`** — raw byte stream including all ANSI/VT escape sequences. Use this to trace cursor movements, screen clears, scroll region changes, and other control sequences that affect layout.

Both files are append-only with timestamped markers for session start/restore and `/clear` events. Enable the **Session file logging** setting in Options → Debug to start recording. To find the ContentId for a session, check the LayoutService persisted state or the filenames in the sessions directory.

### Notable behaviors

- Single-instance enforcement via named Mutex
- Session persistence on exit: working directory, last command, alternate screen state are saved and restored on next launch
- If a TUI app was running in alternate screen mode, the last command is replayed on restore
- Claude Code awareness: detects Claude's TUI screen state (idle prompt `❯`, spinner glyphs, approval/tool prompts) and reports session status in the projects panel. Handles session naming and automatic `/clear` rename restoration
- Click-to-reposition cursor at shell prompt
- Clipboard image paste and drag-and-drop file paths into terminal
- Projects panel groups sessions by working directory, with per-project attachments and icon auto-detection
