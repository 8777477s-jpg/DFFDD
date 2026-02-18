# ✦ Bolt Macro (MVP, patched)

This is a minimal, end-to-end **C# WinForms** project that implements the first MVP slice of ✦:

- Macro record (global keyboard/mouse hooks) → save to SQLite
- Macro playback (SendInput) with **key-release safety** in `finally`
- ROI selector overlay (drag rectangle)
- ROI change trigger (diff metric) with threshold + debounce + hits + cooldown
- Rule loop: trigger → run macro → resume monitoring
- Single "input lease" arbitration (prevents multiple things driving input)
- Panic stop: **Ctrl+Shift+Esc** (aborts run, stops monitor, releases lease, disarms rules)
- SQLite WAL + retry for lock contention

> This is Phase 1 MVP. Text-element watch (UI Automation) + OCR-in-region are intentionally not included yet.

## Build & run

1) Install **.NET 8 SDK** on Windows.
2) From the folder that contains `BoltMacro.csproj`:

```powershell
cd BoltMacro

dotnet restore

dotnet build -c Release

dotnet run -c Release
```

The database is created at:

- `%AppData%\BoltMacro\bolt.db`

## Usage

### Record a macro
1) Click **Record**
2) Do your actions in any app
3) Click **Stop**
4) Choose **Save** and enter a macro name (or **Discard**)

### Play a macro
1) Select it in the left list
2) Click **Play**

### Create an ROI rule
1) Click **New Rule**
2) Select the rule in the middle list
3) Choose a **Macro** in the editor (right)
4) Click **Select ROI**
   - Drag a rectangle, then press **Enter** to confirm (Esc cancels)
5) Tune trigger settings (Hz, threshold, debounce, hits, cooldown)
6) Click **Save**
7) Click **Arm**

### Panic stop
- Press **Ctrl+Shift+Esc** at any time

## What was "patched" (the reliability fixes)

- **No junk files**: the ROI trigger does not write screenshots or temp images.
- **Thread-safety**: recorder uses a lock around the raw-event list.
- **Hook safety**: exceptions inside hooks are swallowed (hooks must never throw).
- **Input safety**: any pressed keys are released in `finally` after playback.
- **DB stability**: WAL + busy_timeout + retry loop for lock errors.
- **No resume of armed rules on restart**: rules are disarmed on startup (Phase 1 safety).
- **Single armed rule policy (Phase 1)**: arming one rule disarms others.

## Next phases

- Work modes: ☯-style quick automation mode, Text Element Watch Mode, OCR-in-region watch.
- Stable binding: UIA re-acquire, DPI correctness, multi-monitor overlays, window-relative ROI.
- Verification & recovery engine, scheduler with priorities/fairness.
