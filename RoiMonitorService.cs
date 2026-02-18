using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace BoltMacro;

public sealed class RoiMonitorService
{
    private readonly TimelineService _timeline;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _watchers = new();
    private readonly Dictionary<string, RuleRuntimeState> _runtimeStates = new();
    private readonly Queue<IncidentRecord> _incidents = new();
    private const int IncidentCapacity = 40;

    public RoiMonitorService(TimelineService timeline)
    {
        _timeline = timeline;
    }

    public void StartOrReplace(RuleModel rule, Action<string, double> onTriggered, Action<TimelineEvent> log, CancellationToken externalCt, Action<string, double, string>? onMetric = null)
    {
        if (rule.Trigger.ContextRoi is null && rule.Trigger.Roi is null)
            throw new InvalidOperationException("Rule has no ROI configured.");

        Stop(rule.Id);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        RuleRuntimeState state;
        lock (_lock)
        {
            _watchers[rule.Id] = cts;
            state = new RuleRuntimeState
            {
                IsArmed = true,
                CorrelationId = Guid.NewGuid().ToString("N")[..8]
            };
            _runtimeStates[rule.Id] = state;
        }

        var ct = cts.Token;
        Task.Run(async () =>
        {
            Bitmap? baselineButton = null;
            Bitmap? baselineContext = null;
            Rectangle? currentContextRect = null;
            Rectangle? lastStableContextRect = null;
            int stableTicks = 0;
            bool weakContextLogged = false;
            bool invalidSampleLogged = false;
            var hitMetrics = new Queue<double>();
            DateTime lastGlobalSearchUtc = DateTime.MinValue;

            try
            {
                int hz = Math.Clamp(rule.Trigger.SamplingHz, 1, 30);
                int intervalMs = Math.Max(20, 1000 / hz);
                int aboveMs = 0;
                int hits = 0;
                long cooldownUntil = 0;

                if (rule.Trigger.ContextRoi is not null && rule.Trigger.ButtonRoiRelative is not null)
                {
                    currentContextRect = ToRect(rule.Trigger.ContextRoi);
                    baselineContext = CaptureRectExact(currentContextRect.Value, rule.Trigger.Scope);
                    baselineButton = CaptureRectExact(ToButtonRect(currentContextRect.Value, rule.Trigger.ButtonRoiRelative), rule.Trigger.Scope);
                }
                else if (rule.Trigger.Roi is not null)
                {
                    currentContextRect = ToRect(rule.Trigger.Roi);
                    baselineButton = CaptureRectExact(currentContextRect.Value, rule.Trigger.Scope);
                }
                else
                {
                    throw new InvalidOperationException("No ROI configured.");
                }

                var roi = rule.Trigger.Roi ?? rule.Trigger.ContextRoi;
                string roiTxt = roi is null ? "?, ?, ?, ?" : $"{roi.X},{roi.Y},{roi.W},{roi.H}";
                log(new TimelineEvent
                {
                    Source = TimelineSource.Trigger,
                    Message = $"[Trigger] MonitorStart ruleId={rule.Id} corr={state.CorrelationId} roi={roiTxt} hz={rule.Trigger.SamplingHz} thresh={rule.Trigger.Threshold:0.###} debounce={rule.Trigger.DebounceMs} hitsReq={rule.Trigger.ConsecutiveHitsRequired} cooldown={rule.Trigger.CooldownMs}"
                });
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (!ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"[Trigger] Tick ruleId={rule.Id} corr={state.CorrelationId} metric=pending hit={hits}/{rule.Trigger.ConsecutiveHitsRequired} time={sw.ElapsedMilliseconds}" });

                    Rectangle buttonRect;
                    Rectangle? resolvedContextRect = currentContextRect;
                    double contextQuality = 1.0;
                    double selectedScale = 1.0;
                    bool reacquiring = false;
                    string searchMode = "legacy";

                    if (rule.Trigger.ContextRoi is not null && rule.Trigger.ButtonRoiRelative is not null)
                    {
                        if (baselineContext is null) throw new InvalidOperationException("Context baseline missing.");

                        Rectangle? maskRect = BuildMaskRect(baselineContext.Size, rule.Trigger.ButtonRoiRelative);
                        var match = ResolveContextRect(
                            baselineContext,
                            rule.Trigger,
                            currentContextRect ?? ToRect(rule.Trigger.ContextRoi),
                            maskRect,
                            lastGlobalSearchUtc,
                            log,
                            rule.Id,
                            out searchMode);

                        contextQuality = match.quality;
                        selectedScale = match.scale;
                        resolvedContextRect = match.rect;
                        state.LastContextRect = resolvedContextRect;
                        state.LastContextQuality = contextQuality;
                        if (match.usedGlobal) lastGlobalSearchUtc = DateTime.UtcNow;

                        if (contextQuality < Math.Clamp(rule.Trigger.ContextMatchMinScore, 0.1, 0.99))
                        {
                            if (!weakContextLogged)
                            {
                                log(new TimelineEvent
                                {
                                    Source = TimelineSource.Trigger,
                                    Severity = TimelineSeverity.Info,
                                    Message = $"[Trigger] context lock weak (score={contextQuality:0.000}) ruleId={rule.Id} mode={searchMode}"
                                });
                                weakContextLogged = true;
                            }

                            aboveMs = 0;
                            hits = 0;
                            hitMetrics.Clear();
                            state.IsLocked = false;
                            state.StableLockTicks = 0;
                            state.CandidateActive = false;
                            state.CandidateTicks = 0;
                            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                            continue;
                        }

                        weakContextLogged = false;
                        currentContextRect = resolvedContextRect;
                        buttonRect = ToButtonRect(currentContextRect.Value, rule.Trigger.ButtonRoiRelative);

                        if (lastStableContextRect is null)
                        {
                            stableTicks = 1;
                            lastStableContextRect = currentContextRect;
                        }
                        else
                        {
                            double iou = IoU(lastStableContextRect.Value, currentContextRect.Value);
                            double centerDistance = CenterDistance(lastStableContextRect.Value, currentContextRect.Value);
                            bool jump = iou < Math.Clamp(rule.Trigger.ContextJumpMinIou, 0.0, 1.0)
                                || centerDistance > Math.Max(0, rule.Trigger.ContextJumpMaxPixels);

                            if (jump)
                            {
                                stableTicks = 0;
                                lastStableContextRect = currentContextRect;
                                reacquiring = true;
                                state.IsLocked = false;
                                state.StableLockTicks = 0;
                            }
                            else
                            {
                                stableTicks++;
                                lastStableContextRect = currentContextRect;
                                state.StableLockTicks = stableTicks;
                            }
                        }

                        if (stableTicks < Math.Max(1, rule.Trigger.StableTicksRequired))
                        {
                            aboveMs = 0;
                            hits = 0;
                            hitMetrics.Clear();
                            state.IsLocked = false;
                            state.CandidateActive = false;
                            state.CandidateTicks = 0;
                            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                            continue;
                        }
                        state.IsLocked = true;
                    }
                    else
                    {
                        if (currentContextRect is null) throw new InvalidOperationException("ROI missing.");
                        buttonRect = currentContextRect.Value;
                    }

                    if (!TryCaptureRectExact(buttonRect, rule.Trigger.Scope, out var current))
                    {
                        if (!invalidSampleLogged)
                        {
                            invalidSampleLogged = true;
                            log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"[Trigger] invalid sample skipped ruleId={rule.Id} rect={buttonRect.X},{buttonRect.Y},{buttonRect.Width},{buttonRect.Height}" });
                        }

                        aboveMs = 0;
                        hits = 0;
                        hitMetrics.Clear();
                        state.CandidateActive = false;
                        state.CandidateTicks = 0;
                        await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    invalidSampleLogged = false;
                    using (current)
                    {
                        var metricOpt = DiffMetric(current, baselineButton!);
                        if (!metricOpt.HasValue)
                        {
                            onMetric?.Invoke(rule.Id, 0.0, "Metric unavailable");
                            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                            continue;
                        }

                        double metric = metricOpt.Value;

                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs < cooldownUntil)
                        {
                            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (metric >= rule.Trigger.Threshold)
                        {
                            state.CandidateActive = true;
                            state.CandidateTicks += 1;
                            if (state.CandidateTicks == 1)
                                state.CandidateFirstSeenUtc = DateTime.UtcNow;
                            aboveMs += intervalMs;
                            if (aboveMs >= rule.Trigger.DebounceMs)
                            {
                                hits++;
                                aboveMs = 0;
                                hitMetrics.Enqueue(metric);
                                while (hitMetrics.Count > Math.Max(1, rule.Trigger.ConsecutiveHitsRequired))
                                    hitMetrics.Dequeue();

                                bool writeIncident = rule.Trigger.TriggerDebugDetails || rule.Trigger.CollectIncidentOnFire;
                                string incident = writeIncident
                                    ? BuildIncidentBlock(rule, metric, hitMetrics, contextQuality, selectedScale, searchMode, resolvedContextRect, buttonRect, "candidate")
                                    : string.Empty;
                                string detail = writeIncident ? " " + incident : string.Empty;

                                log(new TimelineEvent
                                {
                                    Source = TimelineSource.Trigger,
                                    Severity = TimelineSeverity.Info,
                                    Message = $"[Trigger] Trigger candidate ruleId={rule.Id} corr={state.CorrelationId} metric={metric:0.000} hit={hits}/{rule.Trigger.ConsecutiveHitsRequired}{detail}"
                                });

                                if (hits >= Math.Max(1, rule.Trigger.ConsecutiveHitsRequired))
                                {
                                    if (!state.IsLocked || contextQuality < Math.Clamp(rule.Trigger.ContextMatchMinScore, 0.1, 0.99))
                                    {
                                        if (writeIncident)
                                        {
                                            log(new TimelineEvent
                                            {
                                                Source = TimelineSource.Trigger,
                                                Severity = TimelineSeverity.Info,
                                                Message = $"[Trigger] Trigger dropped ruleId={rule.Id} corr={state.CorrelationId} reason=LOCK_LOW {BuildIncidentBlock(rule, metric, hitMetrics, contextQuality, selectedScale, searchMode, resolvedContextRect, buttonRect, "dropped")}"
                                            });
                                        }
                                        hits = 0;
                                        hitMetrics.Clear();
                                        state.CandidateActive = false;
                                        state.CandidateTicks = 0;
                                        await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                                        continue;
                                    }

                                    var variance = MetricRange(hitMetrics);
                                    if (variance > Math.Max(0.0, rule.Trigger.MetricVarianceMax))
                                    {
                                        if (writeIncident)
                                        {
                                            log(new TimelineEvent
                                            {
                                                Source = TimelineSource.Trigger,
                                                Severity = TimelineSeverity.Info,
                                                Message = $"[Trigger] Trigger dropped ruleId={rule.Id} corr={state.CorrelationId} reason=variance variance={variance:0.000} {BuildIncidentBlock(rule, metric, hitMetrics, contextQuality, selectedScale, searchMode, resolvedContextRect, buttonRect, "dropped")}"
                                            });
                                        }

                                        hits = 0;
                                        hitMetrics.Clear();
                                        state.CandidateActive = false;
                                        state.CandidateTicks = 0;
                                        await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                                        continue;
                                    }

                                    cooldownUntil = nowMs + Math.Max(0, rule.Trigger.CooldownMs);
                                    hits = 0;
                                    hitMetrics.Clear();
                                    state.CandidateActive = false;
                                    state.CandidateTicks = 0;
                                    state.LastFireUtc = DateTime.UtcNow;

                                    baselineButton.Dispose();
                                    baselineButton = (Bitmap)current.Clone();

                                    if (!ct.IsCancellationRequested)
                                    {
                                        string firedDetail = writeIncident
                                            ? " " + BuildIncidentBlock(rule, metric, Enumerable.Empty<double>(), contextQuality, selectedScale, searchMode, resolvedContextRect, buttonRect, "fired")
                                            : string.Empty;
                                        log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"[Trigger] Fired ruleId={rule.Id} corr={state.CorrelationId} metric={metric:0.000}{firedDetail}" });
                                        AddIncident(new IncidentRecord
                                        {
                                            UtcTime = DateTime.UtcNow,
                                            CorrelationId = state.CorrelationId,
                                            RuleId = rule.Id,
                                            RuleName = rule.Name,
                                            MacroId = rule.MacroId,
                                            MacroName = rule.MacroId,
                                            ContextQuality = contextQuality,
                                            ContextRect = resolvedContextRect,
                                            ButtonRect = buttonRect,
                                            Decision = "FIRED",
                                            Reason = searchMode
                                        });
                                        onTriggered(rule.Id, metric);
                                    }
                                }
                            }
                        }
                        else
                        {
                            aboveMs = 0;
                            hits = 0;
                            hitMetrics.Clear();
                            state.CandidateActive = false;
                            state.CandidateTicks = 0;
                        }
                    }

                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"[Trigger] MonitorStop ruleId={rule.Id}" });
            }
            catch (Exception ex)
            {
                log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Error, Message = $"[Error] MonitorException ruleId={rule.Id} ex={ex.Message}" });
            }
            finally
            {
                baselineButton?.Dispose();
                baselineContext?.Dispose();
                lock (_lock)
                {
                    if (_watchers.TryGetValue(rule.Id, out var active) && active == cts)
                        _watchers.Remove(rule.Id);
                    _runtimeStates.Remove(rule.Id);
                }
                cts.Dispose();
            }
        }, ct);
    }

    public void Stop(string ruleId)
    {
        CancellationTokenSource? c = null;
        lock (_lock)
        {
            if (_watchers.TryGetValue(ruleId, out c))
                _watchers.Remove(ruleId);
            _runtimeStates.Remove(ruleId);
        }

        if (c is null) return;
        try { c.Cancel(); } catch { }
        c.Dispose();
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"Monitoring stopped for rule '{ruleId}'." });
    }

    public void StopAll()
    {
        List<CancellationTokenSource> all;
        lock (_lock)
        {
            all = _watchers.Values.ToList();
            _watchers.Clear();
            _runtimeStates.Clear();
        }

        foreach (var c in all)
        {
            try { c.Cancel(); } catch { }
            c.Dispose();
        }

        _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = "All monitoring stopped." });
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _watchers.ContainsKey(ruleId);
    }

    private static (Rectangle rect, double quality, double scale, bool usedGlobal) ResolveContextRect(
        Bitmap contextTemplate,
        RoiTrigger trigger,
        Rectangle previous,
        Rectangle? maskRect,
        DateTime lastGlobalSearchUtc,
        Action<TimelineEvent> log,
        string ruleId,
        out string searchMode)
    {
        searchMode = "local";

        var windowBounds = TryResolveWindowBounds(trigger, out var resolvedHwnd, out var resolveReason);
        if (trigger.UseWindowFilter && windowBounds is null)
        {
            log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Info, Message = $"[Trigger] window binding paused ruleId={ruleId} reason={resolveReason}" });
            return (previous, 0, 1.0, false);
        }

        Rectangle? local = BuildLocalSearchArea(previous, trigger.LocalSearchPaddingPx, windowBounds);
        if (local is not null && TryFindContextRect(contextTemplate, trigger, local.Value, maskRect, out var localMatch))
            return (localMatch.rect, localMatch.quality, localMatch.scale, false);

        Rectangle? windowArea = windowBounds;
        if (windowArea is not null)
        {
            searchMode = "window";
            if (TryFindContextRect(contextTemplate, trigger, windowArea.Value, maskRect, out var windowMatch))
                return (windowMatch.rect, windowMatch.quality, windowMatch.scale, false);
        }

        bool globalAllowed = !trigger.UseWindowFilter || trigger.AllowGlobalSearchFallback;
        if (!globalAllowed) return (previous, 0, 1.0, false);

        int everyN = Math.Max(5, trigger.GlobalReacquireEveryNSeconds);
        if (DateTime.UtcNow - lastGlobalSearchUtc < TimeSpan.FromSeconds(everyN))
            return (previous, 0, 1.0, false);

        searchMode = "global";
        var globalArea = trigger.Scope == TrackingScope.PrimaryMonitor
            ? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen
            : SystemInformation.VirtualScreen;

        if (TryFindContextRect(contextTemplate, trigger, globalArea, maskRect, out var globalMatch))
        {
            if (trigger.TriggerDebugDetails)
            {
                log(new TimelineEvent
                {
                    Source = TimelineSource.Trigger,
                    Severity = TimelineSeverity.Info,
                    Message = $"[Trigger] global reacquire ruleId={ruleId} hwnd={resolvedHwnd} quality={globalMatch.quality:0.000}"
                });
            }

            return (globalMatch.rect, globalMatch.quality, globalMatch.scale, true);
        }

        return (previous, 0, 1.0, true);
    }

    private static Rectangle? BuildLocalSearchArea(Rectangle previous, int padding, Rectangle? clipTo)
    {
        padding = Math.Max(20, padding);
        var local = Rectangle.FromLTRB(previous.Left - padding, previous.Top - padding, previous.Right + padding, previous.Bottom + padding);

        Rectangle bounds = clipTo ?? SystemInformation.VirtualScreen;
        local = Rectangle.Intersect(local, bounds);
        return local.Width > 8 && local.Height > 8 ? local : null;
    }

    private static Rectangle? TryResolveWindowBounds(RoiTrigger trigger, out IntPtr resolvedHwnd, out string reason)
    {
        resolvedHwnd = IntPtr.Zero;
        reason = "window-filter-disabled";

        if (!trigger.UseWindowFilter) return null;

        var windows = EnumerateCandidateWindows(trigger).ToList();
        if (windows.Count == 0)
        {
            reason = "no-window-match";
            return null;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var selected = windows.FirstOrDefault(w => w.Hwnd == foreground);
        if (selected.Hwnd == IntPtr.Zero)
            selected = windows.OrderByDescending(w => w.Bounds.Width * w.Bounds.Height).First();

        resolvedHwnd = selected.Hwnd;
        reason = "ok";
        return selected.Bounds;
    }

    private static IEnumerable<WindowCandidate> EnumerateCandidateWindows(RoiTrigger trigger)
    {
        var list = new List<WindowCandidate>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.IsIconic(hWnd)) return true;
            if (!NativeMethods.GetWindowRect(hWnd, out var rect)) return true;

            var bounds = RectToRectangle(rect);
            if (bounds.Width <= 10 || bounds.Height <= 10) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = string.Empty;
            try
            {
                processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                return true;
            }

            string title = GetWindowText(hWnd);
            string className = GetClassName(hWnd);

            bool processOk = string.IsNullOrWhiteSpace(trigger.WindowProcessName)
                || processName.Contains(trigger.WindowProcessName, StringComparison.OrdinalIgnoreCase);
            bool titleOk = string.IsNullOrWhiteSpace(trigger.WindowTitleContains)
                || title.Contains(trigger.WindowTitleContains, StringComparison.OrdinalIgnoreCase);
            bool classOk = string.IsNullOrWhiteSpace(trigger.WindowClassName)
                || className.Contains(trigger.WindowClassName, StringComparison.OrdinalIgnoreCase);

            bool matched = trigger.WindowMatchMode switch
            {
                WindowMatchMode.ProcessOnly => processOk,
                WindowMatchMode.ProcessAndTitle => processOk && titleOk,
                WindowMatchMode.ProcessAndClass => processOk && classOk,
                WindowMatchMode.StrictAll => processOk && titleOk && classOk,
                _ => processOk
            };

            if (matched)
                list.Add(new WindowCandidate(hWnd, bounds));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static Rectangle RectToRectangle(NativeMethods.RECT rect)
        => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static bool TryFindContextRect(Bitmap contextTemplate, RoiTrigger trigger, Rectangle searchArea, Rectangle? maskRect, out (Rectangle rect, double quality, double scale) result)
    {
        result = default;

        using var screenshot = CaptureRectExact(searchArea, trigger.Scope);

        IEnumerable<double> scales = trigger.UseMultiScaleMatching
            ? trigger.MatchScales.Where(s => s > 0.3 && s < 2.5).Distinct().OrderBy(s => s)
            : new[] { 1.0 };

        Rectangle? bestRect = null;
        double bestScore = double.MaxValue;
        double bestScale = 1.0;

        foreach (double scale in scales)
        {
            using var template = ScaleBitmap(contextTemplate, scale);
            if (template.Width <= 8 || template.Height <= 8) continue;
            if (template.Width >= screenshot.Width || template.Height >= screenshot.Height) continue;

            Rectangle? scaledMask = null;
            if (maskRect is not null)
                scaledMask = ScaleMask(maskRect.Value, template.Size, contextTemplate.Size);

            var candidate = MatchTemplateByPrimaryThenFallback(screenshot, template, searchArea, scaledMask, trigger);
            if (candidate.score < bestScore)
            {
                bestScore = candidate.score;
                bestRect = candidate.rect;
                bestScale = scale;
            }
        }

        if (bestRect is null) return false;
        result = (bestRect.Value, 1.0 - Math.Clamp(bestScore, 0.0, 1.0), bestScale);
        return true;
    }

    private static Rectangle? BuildMaskRect(Size contextSize, RelativeRoiRect rel)
    {
        if (contextSize.Width <= 0 || contextSize.Height <= 0) return null;

        Rectangle button = ToButtonRect(new Rectangle(0, 0, contextSize.Width, contextSize.Height), rel);
        var contextRect = new Rectangle(0, 0, contextSize.Width, contextSize.Height);
        var clipped = Rectangle.Intersect(button, contextRect);
        return clipped.Width <= 0 || clipped.Height <= 0 ? null : clipped;
    }

    private static Rectangle ScaleMask(Rectangle mask, Size scaledTemplate, Size sourceTemplate)
    {
        double sx = sourceTemplate.Width > 0 ? scaledTemplate.Width / (double)sourceTemplate.Width : 1.0;
        double sy = sourceTemplate.Height > 0 ? scaledTemplate.Height / (double)sourceTemplate.Height : 1.0;

        var scaled = new Rectangle(
            (int)Math.Round(mask.X * sx),
            (int)Math.Round(mask.Y * sy),
            Math.Max(1, (int)Math.Round(mask.Width * sx)),
            Math.Max(1, (int)Math.Round(mask.Height * sy)));

        return Rectangle.Intersect(scaled, new Rectangle(0, 0, scaledTemplate.Width, scaledTemplate.Height));
    }

    private static (Rectangle rect, double score) MatchTemplateByPrimaryThenFallback(Bitmap screen, Bitmap tpl, Rectangle screenBounds, Rectangle? maskRect, RoiTrigger trigger)
    {
        var primary = MatchTemplateBySad(screen, tpl, screenBounds, maskRect);
        double quality = 1.0 - Math.Clamp(primary.score, 0.0, 1.0);

        bool fallbackNeeded = trigger.UseOrbFallback && quality < Math.Clamp(trigger.ContextMatchMinScore, 0.1, 0.99);
        bool roiTooSmall = tpl.Width < 30 || tpl.Height < 30;

        if (!fallbackNeeded || roiTooSmall)
            return primary;

        var fallback = MatchTemplateByEdgeDiff(screen, tpl, screenBounds, maskRect);
        return fallback.score < primary.score ? fallback : primary;
    }

    private static (Rectangle rect, double score) MatchTemplateBySad(Bitmap screen, Bitmap tpl, Rectangle screenBounds, Rectangle? maskRect)
    {
        int step = 8;
        Rectangle best = new(screenBounds.X, screenBounds.Y, tpl.Width, tpl.Height);
        double bestScore = double.MaxValue;

        for (int y = 0; y <= screen.Height - tpl.Height; y += step)
        {
            for (int x = 0; x <= screen.Width - tpl.Width; x += step)
            {
                var score = FastSad(screen, tpl, x, y, maskRect);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Rectangle(screenBounds.X + x, screenBounds.Y + y, tpl.Width, tpl.Height);
                }
            }
        }

        return (best, bestScore);
    }

    private static (Rectangle rect, double score) MatchTemplateByEdgeDiff(Bitmap screen, Bitmap tpl, Rectangle screenBounds, Rectangle? maskRect)
    {
        int step = 8;
        Rectangle best = new(screenBounds.X, screenBounds.Y, tpl.Width, tpl.Height);
        double bestScore = double.MaxValue;

        using var tplEdge = BuildEdgeMagnitude(tpl);

        for (int y = 0; y <= screen.Height - tpl.Height; y += step)
        {
            for (int x = 0; x <= screen.Width - tpl.Width; x += step)
            {
                using var src = Crop24bpp(screen, new Rectangle(x, y, tpl.Width, tpl.Height));
                using var srcEdge = BuildEdgeMagnitude(src);
                var score = DiffMetric(srcEdge, tplEdge, maskRect);
                if (score.HasValue && score.Value < bestScore)
                {
                    bestScore = score.Value;
                    best = new Rectangle(screenBounds.X + x, screenBounds.Y + y, tpl.Width, tpl.Height);
                }
            }
        }

        return (best, bestScore);
    }

    private static Bitmap Crop24bpp(Bitmap source, Rectangle rect)
    {
        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        return bmp;
    }

    private static Bitmap BuildEdgeMagnitude(Bitmap src)
    {
        var edge = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);

        var r = new Rectangle(0, 0, src.Width, src.Height);
        var bdS = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bdE = edge.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* ps = (byte*)bdS.Scan0;
                byte* pe = (byte*)bdE.Scan0;

                for (int y = 1; y < src.Height - 1; y++)
                {
                    byte* rowPrev = ps + (y - 1) * bdS.Stride;
                    byte* row = ps + y * bdS.Stride;
                    byte* rowNext = ps + (y + 1) * bdS.Stride;
                    byte* rowOut = pe + y * bdE.Stride;

                    for (int x = 1; x < src.Width - 1; x++)
                    {
                        int idx = x * 3;
                        int gx = (row[idx + 3] + rowPrev[idx + 3] + rowNext[idx + 3]) - (row[idx - 3] + rowPrev[idx - 3] + rowNext[idx - 3]);
                        int gy = (rowNext[idx - 3] + rowNext[idx] + rowNext[idx + 3]) - (rowPrev[idx - 3] + rowPrev[idx] + rowPrev[idx + 3]);
                        int mag = Math.Clamp((Math.Abs(gx) + Math.Abs(gy)) / 3, 0, 255);

                        rowOut[idx] = (byte)mag;
                        rowOut[idx + 1] = (byte)mag;
                        rowOut[idx + 2] = (byte)mag;
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(bdS);
            edge.UnlockBits(bdE);
        }

        return edge;
    }

    private static double FastSad(Bitmap source, Bitmap tpl, int x0, int y0, Rectangle? maskRect)
    {
        var rSrc = new Rectangle(0, 0, source.Width, source.Height);
        var rTpl = new Rectangle(0, 0, tpl.Width, tpl.Height);
        var bdS = source.LockBits(rSrc, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bdT = tpl.LockBits(rTpl, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* ps = (byte*)bdS.Scan0;
                byte* pt = (byte*)bdT.Scan0;
                long sum = 0;
                long count = 0;
                int sample = 8;

                for (int y = 0; y < tpl.Height; y += sample)
                {
                    byte* rowS = ps + (y0 + y) * bdS.Stride;
                    byte* rowT = pt + y * bdT.Stride;
                    for (int x = 0; x < tpl.Width; x += sample)
                    {
                        if (maskRect is not null && maskRect.Value.Contains(x, y))
                            continue;

                        int iS = (x0 + x) * 3;
                        int iT = x * 3;
                        sum += Math.Abs(rowS[iS] - rowT[iT]);
                        sum += Math.Abs(rowS[iS + 1] - rowT[iT + 1]);
                        sum += Math.Abs(rowS[iS + 2] - rowT[iT + 2]);
                        count += 3;
                    }
                }

                if (count == 0) return double.MaxValue;
                return sum / (count * 255.0);
            }
        }
        finally
        {
            source.UnlockBits(bdS);
            tpl.UnlockBits(bdT);
        }
    }

    private static Bitmap ScaleBitmap(Bitmap src, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));

        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }

    private static Rectangle ToRect(RoiRect r) => new(r.X, r.Y, r.W, r.H);

    private static Rectangle ToButtonRect(Rectangle contextRect, RelativeRoiRect rel)
    {
        if (rel.OffsetXFrac.HasValue && rel.OffsetYFrac.HasValue && rel.WFrac.HasValue && rel.HFrac.HasValue)
        {
            int x = contextRect.X + (int)Math.Round(contextRect.Width * rel.OffsetXFrac.Value);
            int y = contextRect.Y + (int)Math.Round(contextRect.Height * rel.OffsetYFrac.Value);
            int w = Math.Max(1, (int)Math.Round(contextRect.Width * rel.WFrac.Value));
            int h = Math.Max(1, (int)Math.Round(contextRect.Height * rel.HFrac.Value));
            return new Rectangle(x, y, w, h);
        }

        return new Rectangle(contextRect.X + rel.OffsetX, contextRect.Y + rel.OffsetY, rel.W, rel.H);
    }

    private static bool TryCaptureRectExact(Rectangle requested, TrackingScope scope, out Bitmap bitmap)
    {
        bitmap = null!;
        var limits = scope == TrackingScope.PrimaryMonitor
            ? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen
            : SystemInformation.VirtualScreen;

        var clipped = Rectangle.Intersect(requested, limits);
        if (clipped != requested || clipped.Width <= 0 || clipped.Height <= 0)
            return false;

        bitmap = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(clipped.X, clipped.Y, 0, 0, clipped.Size, CopyPixelOperation.SourceCopy);
        return true;
    }

    private static Bitmap CaptureRectExact(Rectangle requested, TrackingScope scope)
    {
        if (!TryCaptureRectExact(requested, scope, out var bmp))
            throw new InvalidOperationException("Capture rectangle is clipped/outside visible area.");
        return bmp;
    }

    private static double? DiffMetric(Bitmap a, Bitmap b, Rectangle? maskRect = null)
    {
        if (a.Width != b.Width || a.Height != b.Height) return null;

        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var bdA = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bdB = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* pA = (byte*)bdA.Scan0;
                byte* pB = (byte*)bdB.Scan0;
                long sum = 0;
                long count = 0;
                int step = 6;

                for (int y = 0; y < a.Height; y += step)
                {
                    byte* rowA = pA + y * bdA.Stride;
                    byte* rowB = pB + y * bdB.Stride;

                    for (int x = 0; x < a.Width; x += step)
                    {
                        if (maskRect is not null && maskRect.Value.Contains(x, y))
                            continue;

                        int i = x * 3;
                        sum += Math.Abs(rowA[i] - rowB[i]);
                        sum += Math.Abs(rowA[i + 1] - rowB[i + 1]);
                        sum += Math.Abs(rowA[i + 2] - rowB[i + 2]);
                        count += 3;
                    }
                }

                if (count == 0) return 0.0;
                return Math.Clamp(sum / (count * 255.0), 0.0, 1.0);
            }
        }
        finally
        {
            a.UnlockBits(bdA);
            b.UnlockBits(bdB);
        }
    }

    private static double IoU(Rectangle a, Rectangle b)
    {
        var inter = Rectangle.Intersect(a, b);
        if (inter.Width <= 0 || inter.Height <= 0) return 0;

        double i = inter.Width * inter.Height;
        double u = a.Width * a.Height + b.Width * b.Height - i;
        return u <= 0 ? 0 : i / u;
    }

    private static double CenterDistance(Rectangle a, Rectangle b)
    {
        double ax = a.X + a.Width / 2.0;
        double ay = a.Y + a.Height / 2.0;
        double bx = b.X + b.Width / 2.0;
        double by = b.Y + b.Height / 2.0;
        double dx = ax - bx;
        double dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double MetricRange(IEnumerable<double> values)
    {
        var arr = values.ToArray();
        if (arr.Length <= 1) return 0;
        return arr.Max() - arr.Min();
    }

    private static string BuildIncidentBlock(RuleModel rule, double metric, IEnumerable<double> metrics, double contextQuality, double scale, string searchMode, Rectangle? contextRect, Rectangle buttonRect, string phase)
    {
        var macro = rule.MacroId ?? "(none)";
        string trend = BuildMetricTrend(metrics);
        string ctx = contextRect is null ? "none" : $"{contextRect.Value.X},{contextRect.Value.Y},{contextRect.Value.Width},{contextRect.Value.Height}";
        string btn = $"{buttonRect.X},{buttonRect.Y},{buttonRect.Width},{buttonRect.Height}";

        return $"incident phase={phase} ruleId={rule.Id} ruleName={SafeKv(rule.Name)} macroId={macro} macroName={SafeKv(macro)} q={contextQuality:0.000} scale={scale:0.###} search={searchMode} metric={metric:0.000} trend={trend} ctxRect={ctx} btnRect={btn}";
    }

    private static string BuildMetricTrend(IEnumerable<double> metrics)
    {
        var arr = metrics.ToArray();
        if (arr.Length == 0) return "none";
        return $"[{string.Join(",", arr.Select(x => x.ToString("0.000")))}]";
    }

    private static string SafeKv(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "_";
        return value.Replace(' ', '_').Replace('|', '_').Replace('\n', '_').Replace('\r', '_');
    }

    private void AddIncident(IncidentRecord incident)
    {
        lock (_lock)
        {
            _incidents.Enqueue(incident);
            while (_incidents.Count > IncidentCapacity)
                _incidents.Dequeue();
        }
    }

    private sealed class RuleRuntimeState
    {
        public bool IsArmed { get; set; }
        public bool IsLocked { get; set; }
        public Rectangle? LastContextRect { get; set; }
        public double LastContextQuality { get; set; }
        public int StableLockTicks { get; set; }
        public bool CandidateActive { get; set; }
        public int CandidateTicks { get; set; }
        public DateTime CandidateFirstSeenUtc { get; set; }
        public DateTime LastFireUtc { get; set; }
        public bool WaitingForReturnToBaseline { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }

    private sealed class IncidentRecord
    {
        public DateTime UtcTime { get; init; }
        public string CorrelationId { get; init; } = string.Empty;
        public string RuleId { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public string? MacroId { get; init; }
        public string? MacroName { get; init; }
        public Rectangle? ContextRect { get; init; }
        public Rectangle ButtonRect { get; init; }
        public double ContextQuality { get; init; }
        public string Decision { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
    }

    private readonly record struct WindowCandidate(IntPtr Hwnd, Rectangle Bounds);
}
