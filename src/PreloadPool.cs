using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Owns at most one marked, unused window. Delivered windows leave ownership permanently.
internal sealed class PreloadPool : IDisposable
{
    private sealed class Slot
    {
        internal IntPtr Handle;
        internal uint ProcessId;
        internal bool Ready;
        internal DateTime Created;
    }
    private readonly object gate = new object();
    private readonly IntPtr cookie = new IntPtr(new Random().Next(1, Int32.MaxValue));
    private readonly string statusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preload-status.json");
    private Slot spare;
    private Slot provisional;
    private bool directLaunchDuringPrepare;
    private bool pending, disposed, paused;
    private DateTime nextAttempt = DateTime.UtcNow.AddSeconds(1);
    private DateTime nextStatus;
    private long lastDelivered;
    private bool scanPending;
    private bool maintenancePending;
    private DateTime nextMaintenance;
    private readonly PreloadRecovery recovery=new PreloadRecovery();
    private List<WindowSupport.Info> unresolved=new List<WindowSupport.Info>();
    private bool unknownFailure;

    internal string StatusText
    {
        get { lock (gate) return paused ? "预加载已手动暂停" : recovery.Exhausted ? "预加载故障，请手动恢复"
            : unknownFailure || unresolved.Count>0 ? "等待未确认窗口关闭" : recovery.Failures>0 && !pending ? "预加载等待重试"
            : spare != null && spare.Ready ? "备用窗口已就绪" : pending || spare != null ? "正在预加载" : "等待 PWA 窗口"; }
    }

    internal bool NeedsResume { get { lock(gate) return paused || recovery.Failures>0; } }

    internal PreloadPool()
    {
        // A previous crash must not leave an unreachable hidden Edge window.
        foreach (WindowSupport.Info info in WindowSupport.Windows())
            if (info.AppId == WindowSupport.AppId && WindowSupport.GetProp(info.Handle, WindowSupport.OwnerProperty) != IntPtr.Zero)
            {
                WindowSupport.RemoveProp(info.Handle, WindowSupport.OwnerProperty);
                WindowSupport.RestorePlacement(info.Handle, true);
                WindowSupport.ShowWindowAsync(info.Handle, 4);
                Program.Log("recovered-orphan hwnd=" + info.Handle);
            }
    }

    internal void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            if (spare != null && !Owned(spare)) spare = null;
            if (spare != null && DateTime.UtcNow - spare.Created > TimeSpan.FromSeconds(1) && WindowSupport.IsWindowVisible(spare.Handle) && !WindowSupport.IsParked(spare.Handle))
            {
                // If Edge/user exposed the spare independently, it is now theirs.
                WindowSupport.RemoveProp(spare.Handle, WindowSupport.OwnerProperty);
                WindowSupport.RestorePlacement(spare.Handle, true);
                Program.Log("spare-released-externally hwnd=" + spare.Handle);
                spare = null;
                nextAttempt = DateTime.UtcNow.AddSeconds(2);
            }
            if (DateTime.UtcNow >= nextStatus) { WriteStatus(); nextStatus = DateTime.UtcNow.AddSeconds(1); }
            if (spare != null && !pending && !maintenancePending && DateTime.UtcNow >= nextMaintenance)
            {
                maintenancePending = true;
                Task.Run(delegate
                {
                    try
                    {
                        bool hasUserWindow = WindowSupport.Windows().Exists(w => w.AppId == WindowSupport.AppId &&
                            WindowSupport.IsWindowVisible(w.Handle) && WindowSupport.GetProp(w.Handle, WindowSupport.OwnerProperty) == IntPtr.Zero);
                        lock (gate) { if (!disposed && !hasUserWindow) CloseSpare(); }
                    }
                    catch (Exception error) { Program.Log("maintenance-error " + error.GetType().Name); }
                    finally { lock (gate) { maintenancePending = false; nextMaintenance = DateTime.UtcNow.AddSeconds(5); } }
                });
            }
            if (paused || recovery.Exhausted || spare != null || pending || scanPending || DateTime.UtcNow < nextAttempt) return;
            scanPending = true;
        }
        Task.Run(delegate
        {
            List<WindowSupport.Info> before=null;
            bool attempted=false;
            try
            {
                List<WindowSupport.Info> windows = WindowSupport.Windows();
                bool hasUserWindow = windows.Exists(w => w.AppId == WindowSupport.AppId && WindowSupport.IsWindowVisible(w.Handle));
                lock (gate)
                {
                    unresolved.RemoveAll(old=>!windows.Exists(now=>now.Handle==old.Handle && now.ProcessId==old.ProcessId));
                    if(!recovery.CanAttempt(DateTime.UtcNow,paused,unknownFailure || unresolved.Count>0)) return;
                    if (disposed || paused || !hasUserWindow || spare != null || pending) return;
                    pending = true;
                    directLaunchDuringPrepare = false;
                }
                before=windows; attempted=true;
                Prepare(windows);
            }
            catch (Exception error) {
                Program.Log("preload-error " + error.GetType().Name);
                if(attempted) RegisterFailure(before,error.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    ReleaseProvisional();
                    pending = false;
                    scanPending = false;
                    nextAttempt = DateTime.UtcNow.AddSeconds(5);
                    WriteStatus();
                }
            }
        });
    }

    private void Prepare(List<WindowSupport.Info> before)
    {
        HashSet<IntPtr> existing = new HashSet<IntPtr>();
        foreach (WindowSupport.Info info in before) existing.Add(info.Handle);
        string marker = "panepilot-preload-" + Guid.NewGuid().ToString("N");
        IntPtr previousForeground = WindowSupport.GetForegroundWindow();
        Stopwatch watch = Stopwatch.StartNew();
        Program.LaunchPreloaded(marker);
        Program.Log("preload-start");
        Slot mine = null;
        while (watch.ElapsedMilliseconds < 30000)
        {
            lock (gate) { if (disposed || paused) return; }
            foreach (WindowSupport.Info info in WindowSupport.Windows())
            {
                if (existing.Contains(info.Handle) || info.AppId != WindowSupport.AppId) continue;
                lock (gate)
                {
                    if (disposed || paused) return;
                    // Briefly suppress only a newly created matching PWA while proving its URL.
                    // Never close an unverified candidate; restore it on timeout/cancellation.
                    if (provisional == null && !directLaunchDuringPrepare)
                    {
                        if (WindowSupport.SetProp(info.Handle, WindowSupport.OwnerProperty, cookie))
                        {
                            provisional = new Slot { Handle = info.Handle, ProcessId = info.ProcessId, Created = DateTime.UtcNow };
                            WindowSupport.Park(info.Handle);
                            if (WindowSupport.GetForegroundWindow() == info.Handle && WindowSupport.IsWindow(previousForeground))
                                WindowSupport.SetForegroundWindow(previousForeground);
                            Program.Log("preload-hidden-candidate hwnd=" + info.Handle + " ms=" + watch.ElapsedMilliseconds);
                        }
                    }
                }
                bool hasEditor;
                bool markerMatches;
                try { markerMatches = WindowSupport.MatchesMarker(info.Handle, marker, out hasEditor); }
                catch (Exception) { continue; }
                if (!markerMatches) continue;
                lock (gate)
                {
                    if (disposed || paused) return;
                    if (!WindowSupport.SetProp(info.Handle, WindowSupport.OwnerProperty, cookie))
                        throw new InvalidOperationException("Cannot mark preload window.");
                    mine = new Slot { Handle = info.Handle, ProcessId = info.ProcessId, Created = DateTime.UtcNow, Ready = hasEditor };
                    if (provisional != null && provisional.Handle != mine.Handle) ReleaseProvisional();
                    provisional = null;
                    // A window becomes a retained spare only after its unique URL is verified.
                    if (!WindowSupport.IsParked(info.Handle)) WindowSupport.Park(info.Handle);
                    if (WindowSupport.GetForegroundWindow() == info.Handle && WindowSupport.IsWindow(previousForeground))
                        WindowSupport.SetForegroundWindow(previousForeground);
                    spare = mine;
                    recovery.Reset(); unresolved.Clear(); unknownFailure=false;
                    // Delay visibility checks until Edge has processed SW_HIDE.
                    nextStatus = DateTime.UtcNow.AddSeconds(1);
                    Program.Log("preload-captured hwnd=" + info.Handle + " ms=" + watch.ElapsedMilliseconds);
                    WriteStatus();
                }
                break;
            }
            if (mine != null) break;
            Thread.Sleep(50);
        }
        if (mine == null)
        {
            RegisterFailure(before,"window-marker-not-found");
            return;
        }
        while (watch.ElapsedMilliseconds < 60000)
        {
            lock (gate)
            {
                if (disposed || spare != mine || !Owned(mine)) return;
                if (mine.Ready)
                {
                    WindowSupport.ShowWindow(mine.Handle, 0);
                    WindowSupport.RestorePlacement(mine.Handle, false);
                    recovery.Reset(); unresolved.Clear(); unknownFailure=false;
                    Program.Log("preload-ready hwnd=" + mine.Handle + " ms=" + watch.ElapsedMilliseconds);
                    WriteStatus();
                    return;
                }
            }
            bool ready = false;
            try { ready = WindowSupport.HasEditor(mine.Handle); }
            catch (Exception) { }
            lock (gate) { if (spare == mine && !disposed) mine.Ready = ready; }
            Thread.Sleep(500);
        }
        Program.Log("preload-waiting-page hwnd=" + mine.Handle);
    }

    internal void Take()
    {
        Stopwatch watch = Stopwatch.StartNew();
        WindowSupport.LayoutRequest layout = WindowSupport.CaptureLayoutRequest();
        Slot taken;
        lock (gate)
        {
            taken = spare != null && Owned(spare) ? spare : null;
            if (taken == null && pending)
            {
                directLaunchDuringPrepare = true;
                // An unverified candidate may be a new manual window: expose it before fall back.
                ReleaseProvisional();
            }
            spare = null;
            nextAttempt = DateTime.UtcNow.AddSeconds(1);
            if (taken != null)
            {
                WindowSupport.RemoveProp(taken.Handle, WindowSupport.OwnerProperty);
                lastDelivered = taken.Handle.ToInt64();
            }
            WriteStatus();
        }
        if (taken == null)
        {
            Task.Run(delegate { try { Program.Launch(layout); } catch (Exception error) { Program.Log("normal-launch-error " + error.Message); } });
            Program.Log("preload-miss normal-launch");
            return;
        }
        WindowSupport.RestorePlacement(taken.Handle, true);
        bool sized = WindowSupport.ApplyRequestedLayout(taken.Handle, layout);
        if (!WindowSupport.IsWindowVisible(taken.Handle)) WindowSupport.ShowWindow(taken.Handle, 4);
        bool foreground = WindowSupport.SetForegroundWindow(taken.Handle);
        if(WindowModeTracker.Active!=null) WindowModeTracker.Active.Track(taken.Handle);
        WindowSupport.Rect deliveredBounds;
        WindowSupport.GetWindowRect(taken.Handle, out deliveredBounds);
        Program.Log("preload-delivered hwnd=" + taken.Handle + " editorReady=" + taken.Ready + " requestMs=" + watch.ElapsedMilliseconds + " foreground=" + foreground + " sized=" + sized + " width=" + (deliveredBounds.Right-deliveredBounds.Left) + " height=" + (deliveredBounds.Bottom-deliveredBounds.Top));
    }

    internal bool TogglePause()
    {
        lock (gate)
        {
            // The same tray action explicitly resumes a fault pause, without toggling into manual pause.
            if(!paused && recovery.Failures>0) { recovery.Reset(); unresolved.Clear(); unknownFailure=false; }
            else paused = !paused;
            if (paused) { CloseSpare(); ReleaseProvisional(); }
            nextAttempt = DateTime.UtcNow;
            WriteStatus();
            return paused;
        }
    }

    private void RegisterFailure(List<WindowSupport.Info> before,string reason)
    {
        List<WindowSupport.Info> candidates=new List<WindowSupport.Info>();
        bool unknown=false;
        try {
            candidates=WindowSupport.Windows().FindAll(w=>w.AppId==WindowSupport.AppId
                && (before==null || !before.Exists(old=>old.Handle==w.Handle && old.ProcessId==w.ProcessId)));
        } catch(Exception) { unknown=true; }
        lock(gate) {
            if(disposed || paused) return;
            unresolved=candidates; unknownFailure=unknown; recovery.Fail(DateTime.UtcNow);
            Program.Log("preload-fault reason="+reason+" failures="+recovery.Failures
                +" unresolved="+unresolved.Count+" scanUnknown="+unknown+" exhausted="+recovery.Exhausted);
        }
    }

    private bool Owned(Slot slot)
    {
        uint pid;
        return WindowSupport.IsWindow(slot.Handle) && WindowSupport.GetWindowThreadProcessId(slot.Handle, out pid) != 0
            && pid == slot.ProcessId && WindowSupport.GetProp(slot.Handle, WindowSupport.OwnerProperty) == cookie;
    }

    private void CloseSpare()
    {
        if (spare != null && Owned(spare))
        {
            WindowSupport.RemoveProp(spare.Handle, WindowSupport.OwnerProperty);
            if (!WindowSupport.IsWindowVisible(spare.Handle) || WindowSupport.IsParked(spare.Handle))
            {
                WindowSupport.ShowWindow(spare.Handle, 0);
                WindowSupport.RestorePlacement(spare.Handle, false);
                WindowSupport.PostMessage(spare.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            Program.Log("preload-released hwnd=" + spare.Handle);
        }
        spare = null;
    }

    private void ReleaseProvisional()
    {
        if (provisional != null && Owned(provisional))
        {
            WindowSupport.RemoveProp(provisional.Handle, WindowSupport.OwnerProperty);
            WindowSupport.RestorePlacement(provisional.Handle, true);
            WindowSupport.ShowWindowAsync(provisional.Handle, 4);
        }
        provisional = null;
    }

    private void WriteStatus()
    {
        try
        {
            string state = disposed ? "stopped" : paused ? "paused" : recovery.Exhausted ? "fault-paused"
                : spare != null ? (spare.Ready ? "ready" : "loading") : pending ? "preparing"
                : unknownFailure || unresolved.Count>0 ? "waiting-unresolved" : recovery.Failures>0 ? "retry-wait" : "waiting";
            File.WriteAllText(statusPath, "{\"state\":\"" + state + "\",\"pid\":" + Process.GetCurrentProcess().Id +
                ",\"spareHandle\":" + (spare == null ? 0 : spare.Handle.ToInt64()) + ",\"lastDelivered\":" + lastDelivered
                + ",\"mode\":\"" + (Program.WindowMode==null || Program.WindowMode.PreferMaximized ? "maximized" : "automatic") + "\"}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; CloseSpare(); ReleaseProvisional(); WriteStatus(); }
    }
}
