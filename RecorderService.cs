using System.Diagnostics;

namespace BoltMacro;

public sealed class RecorderService
{
    private readonly TimelineService _timeline;

    private IntPtr _kbdHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    private NativeMethods.HookProc? _kbdProc;
    private NativeMethods.HookProc? _mouseProc;

    private readonly Stopwatch _sw = new();

    private readonly List<RawEvent> _raw = new();
    private readonly object _rawLock = new();

    public bool IsRecording { get; private set; }

    public RecorderService(TimelineService timeline)
    {
        _timeline = timeline;
    }

    public bool Start()
    {
        if (IsRecording) return true;

        lock (_rawLock) _raw.Clear();
        _sw.Restart();

        _kbdProc = KeyboardHook;
        _mouseProc = MouseHook;

        using var p = Process.GetCurrentProcess();
        var hMod = NativeMethods.GetModuleHandle(p.MainModule?.ModuleName);

        _kbdHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        if (_kbdHook == IntPtr.Zero)
        {
            IsRecording = false;
            _timeline.Add(new TimelineEvent
            {
                Source = TimelineSource.Recording,
                Severity = TimelineSeverity.Error,
                Message = "Failed to install keyboard hook. Try running as admin or check security settings."
            });
            return false;
        }

        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);

        if (_mouseHook == IntPtr.Zero)
        {
            if (_kbdHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_kbdHook);
            _kbdHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;

            IsRecording = false;
            _timeline.Add(new TimelineEvent
            {
                Source = TimelineSource.Recording,
                Severity = TimelineSeverity.Error,
                Message = "Failed to install hooks. Try running as admin or check security settings."
            });
            return false;
        }

        IsRecording = true;
        _timeline.Add(new TimelineEvent
        {
            Source = TimelineSource.Recording,
            Severity = TimelineSeverity.Info,
            Message = "Recording started."
        });
        return true;
    }

    public List<MacroStep> StopAndBuildSteps()
    {
        if (!IsRecording) return new List<MacroStep>();

        IsRecording = false;
        try
        {
            if (_kbdHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_kbdHook);
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        }
        finally
        {
            _kbdHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }

        List<RawEvent> snapshot;
        lock (_rawLock) snapshot = _raw.ToList();

        snapshot.Sort((a, b) => a.T.CompareTo(b.T));

        var steps = new List<MacroStep>();
        long prevT = 0;

        foreach (var e in snapshot)
        {
            int delay = (int)Math.Clamp(e.T - prevT, 0, int.MaxValue);
            prevT = e.T;

            MacroStep? step = e.Kind switch
            {
                "KD" => new KeyStep { VirtualKey = e.A, IsKeyDown = true },
                "KU" => new KeyStep { VirtualKey = e.A, IsKeyDown = false },
                "MM" => new MouseMoveStep { X = e.A, Y = e.B },
                "LC" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Left, Clicks = 1 },
                "LD" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Left, Clicks = 2 },
                "RC" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Right, Clicks = 1 },
                "RD" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Right, Clicks = 2 },
                "MC" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Middle, Clicks = 1 },
                "MD" => new MouseClickStep { X = e.A, Y = e.B, Button = MouseButton.Middle, Clicks = 2 },
                _ => null
            };

            if (step is null) continue;
            step.DelayMsBefore = delay;
            steps.Add(step);
        }

        _timeline.Add(new TimelineEvent
        {
            Source = TimelineSource.Recording,
            Severity = TimelineSeverity.Info,
            Message = $"Recording stopped. Steps: {steps.Count}."
        });

        return steps;
    }

    private bool IsOurProcessForeground()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && IsRecording && !IsOurProcessForeground())
            {
                int msg = wParam.ToInt32();
                if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    lock (_rawLock) _raw.Add(new RawEvent(_sw.ElapsedMilliseconds, "KD", (int)data.vkCode, 0, 0));
                }
                else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    lock (_rawLock) _raw.Add(new RawEvent(_sw.ElapsedMilliseconds, "KU", (int)data.vkCode, 0, 0));
                }
            }
        }
        catch
        {
            // Never throw from a hook.
        }

        return NativeMethods.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && IsRecording && !IsOurProcessForeground())
            {
                int msg = wParam.ToInt32();
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                if (msg == NativeMethods.WM_MOUSEMOVE)
                {
                    lock (_rawLock)
                    {
                        var lastMove = _raw.LastOrDefault(x => x.Kind == "MM");
                        if (lastMove is null || Math.Abs(lastMove.A - data.pt.x) + Math.Abs(lastMove.B - data.pt.y) >= 3)
                            _raw.Add(new RawEvent(_sw.ElapsedMilliseconds, "MM", data.pt.x, data.pt.y, 0));
                    }
                }
                else
                {
                    (string kind, int btn)? click = msg switch
                    {
                        NativeMethods.WM_LBUTTONDOWN => ("LC", (int)MouseButton.Left),
                        NativeMethods.WM_LBUTTONDBLCLK => ("LD", (int)MouseButton.Left),
                        NativeMethods.WM_RBUTTONDOWN => ("RC", (int)MouseButton.Right),
                        NativeMethods.WM_RBUTTONDBLCLK => ("RD", (int)MouseButton.Right),
                        NativeMethods.WM_MBUTTONDOWN => ("MC", (int)MouseButton.Middle),
                        NativeMethods.WM_MBUTTONDBLCLK => ("MD", (int)MouseButton.Middle),
                        _ => null
                    };

                    if (click is not null)
                    {
                        lock (_rawLock) _raw.Add(new RawEvent(_sw.ElapsedMilliseconds, click.Value.kind, data.pt.x, data.pt.y, click.Value.btn));
                    }
                }
            }
        }
        catch
        {
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private sealed record RawEvent(long T, string Kind, int A, int B, int C);
}
