using System.Windows.Forms;

namespace BoltMacro;

public sealed class PlaybackService
{
    private readonly TimelineService _timeline;

    public PlaybackService(TimelineService timeline)
    {
        _timeline = timeline;
    }

    public async Task<(bool ok, RunStatus status, string? error)> RunAsync(
        IReadOnlyList<MacroStep> steps,
        CancellationToken ct)
    {
        var pressed = new HashSet<ushort>();

        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                ct.ThrowIfCancellationRequested();

                int delayBefore = step.DelayMsBefore;
                if (i == 0 && delayBefore > 10_000)
                {
                    _timeline.Add(new TimelineEvent
                    {
                        Source = TimelineSource.Playback,
                        Severity = TimelineSeverity.Warn,
                        Message = $"Clamped initial lead-in delay from {delayBefore}ms to 1500ms."
                    });
                    delayBefore = 1_500;
                }

                if (delayBefore > 0)
                    await Task.Delay(delayBefore, ct);

                switch (step)
                {
                    case DelayStep d:
                        if (d.Ms > 0) await Task.Delay(d.Ms, ct);
                        break;

                    case KeyStep k:
                        SendKey((ushort)k.VirtualKey, k.IsKeyDown);
                        if (k.IsKeyDown) pressed.Add((ushort)k.VirtualKey);
                        else pressed.Remove((ushort)k.VirtualKey);
                        break;

                    case MouseMoveStep mm:
                        MoveMouseAbsolute(mm.X, mm.Y);
                        break;

                    case MouseClickStep mc:
                        MoveMouseAbsolute(mc.X, mc.Y);
                        Click(mc.Button, mc.Clicks);
                        break;
                }
            }

            return (true, RunStatus.Success, null);
        }
        catch (OperationCanceledException)
        {
            return (false, RunStatus.Aborted, "Aborted by user (panic stop)." );
        }
        catch (Exception ex)
        {
            return (false, RunStatus.Failed, ex.Message);
        }
        finally
        {
            foreach (var vk in pressed.ToArray())
            {
                try { SendKey(vk, down: false); } catch { }
            }
        }
    }

    private static void SendKey(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void MoveMouseAbsolute(int screenX, int screenY)
    {
        var vs = SystemInformation.VirtualScreen;
        int w = vs.Width;
        int h = vs.Height;
        if (w <= 1 || h <= 1) return;

        int x = Math.Clamp(screenX, vs.Left, vs.Right - 1);
        int y = Math.Clamp(screenY, vs.Top, vs.Bottom - 1);

        int dx = (int)Math.Round((x - vs.Left) * 65535.0 / (w - 1));
        int dy = (int)Math.Round((y - vs.Top) * 65535.0 / (h - 1));

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = 0,
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void Click(MouseButton btn, int clicks)
    {
        clicks = Math.Max(1, clicks);

        (uint down, uint up) = btn switch
        {
            MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP)
        };

        for (int i = 0; i < clicks; i++)
        {
            SendMouse(down);
            SendMouse(up);
            Thread.Sleep(10);
        }
    }

    private static void SendMouse(uint flags)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
