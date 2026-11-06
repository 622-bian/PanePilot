using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Desktop geometry uses physical pixels in the virtual desktop coordinate system.
// Does not read window titles/content. Split changes only the captured foreground and new PWA.
internal static partial class WindowSupport
{
    internal sealed class Display
    {
        internal IntPtr Handle;
        internal Rect Bounds, Work;
        internal bool Primary;
    }

    internal sealed class DesktopSnapshot
    {
        internal readonly List<Display> Displays = new List<Display>();
        internal readonly List<Rect> Occupants = new List<Rect>();
        internal bool Reliable = true;
    }

    internal sealed class LayoutRequest
    {
        internal IntPtr PreferredMonitor;
        internal SavedWindow Foreground;
        internal DesktopSnapshot Snapshot;
        internal long SampledAt;
    }

    internal sealed class LayoutPlan
    {
        internal Display Target;
        internal bool Maximize;
        internal int DisplayCount, EmptyCount;
        internal Rect? FreeBounds;
        internal bool WantsSplit;
        internal bool RememberedMaximize;
    }

    private static readonly object layoutGate = new object();

    internal static bool Overlaps(Rect a, Rect b)
    {
        return Math.Max(a.Left, b.Left) < Math.Min(a.Right, b.Right)
            && Math.Max(a.Top, b.Top) < Math.Min(a.Bottom, b.Bottom);
    }

    internal static bool CountsAsOccupant(bool visible, bool minimized, bool cloaked,
        bool preload, long extendedStyle, string className)
    {
        if (!visible || minimized || cloaked || preload) return false;
        // Tool palettes, notifications, desktop and taskbars are not normal app windows.
        if ((extendedStyle & 0x80L) != 0 && (extendedStyle & 0x40000L) == 0) return false;
        return className != "Progman" && className != "WorkerW"
            && className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd";
    }

    internal static LayoutPlan ChooseLayout(DesktopSnapshot snapshot, IntPtr preferred, bool preferMaximized=false)
    {
        List<Display> empty = snapshot.Displays.FindAll(display => snapshot.Reliable
            && !snapshot.Occupants.Exists(rect => Overlaps(rect, display.Work)));
        List<Display> choices = empty.Count > 0 ? empty : snapshot.Displays;
        Display target = choices.Find(display => display.Handle == preferred)
            ?? choices.Find(display => display.Primary)
            ?? (choices.Count == 0 ? null : choices[0]);
        LayoutPlan plan = new LayoutPlan { Target = target, Maximize = target!=null && (empty.Count > 0 || preferMaximized),
            RememberedMaximize=preferMaximized,
            DisplayCount = snapshot.Displays.Count, EmptyCount = empty.Count };
        if (empty.Count == 0 && snapshot.Reliable && !preferMaximized)
        {
            List<Display> ordered = new List<Display>(snapshot.Displays);
            ordered.Sort((a, b) => DisplayPriority(a, preferred).CompareTo(DisplayPriority(b, preferred)));
            foreach (Display display in ordered)
            {
                Rect free;
                if (!TryFindFreeBounds(display.Work, snapshot.Occupants, out free)) continue;
                plan.Target = display; plan.FreeBounds = free;
                return plan;
            }
            plan.WantsSplit = target != null;
        }
        return plan;
    }

    private static int DisplayPriority(Display display, IntPtr preferred)
    {
        return display.Handle == preferred ? 0 : display.Primary ? 1 : 2;
    }

    internal static bool TryFindFreeBounds(Rect work, List<Rect> occupants, out Rect result)
    {
        // A free fixed-size rectangle can slide left/up until a screen or obstacle edge stops it.
        // Candidate edges avoid a per-pixel search and keep negative monitor origins meaningful.
        result = new Rect();
        const int width = 720, height = 405;
        var xs = new SortedSet<int> { work.Left };
        var ys = new SortedSet<int> { work.Top };
        foreach (Rect rect in occupants)
        {
            if (!Overlaps(rect, work)) continue;
            xs.Add(rect.Right); ys.Add(rect.Bottom);
        }
        foreach (int y in ys)
            foreach (int x in xs)
            {
                Rect candidate = new Rect { Left=x, Top=y, Right=x+width, Bottom=y+height };
                if (candidate.Left < work.Left || candidate.Top < work.Top
                    || candidate.Right > work.Right || candidate.Bottom > work.Bottom) continue;
                if (occupants.Exists(rect => Overlaps(rect, candidate))) continue;
                result = candidate;
                return true;
            }
        return false;
    }

    internal static DesktopSnapshot CaptureDesktop(IntPtr excluded)
    {
        DesktopSnapshot snapshot = new DesktopSnapshot();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            delegate(IntPtr handle, IntPtr dc, ref Rect rect, IntPtr data)
            {
                MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(handle, ref info)) { snapshot.Reliable = false; return true; }
                if (info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top) return true;
                // Mirrored outputs share a desktop region, not independent placement space.
                if (snapshot.Displays.Exists(d => d.Bounds.Left == info.Bounds.Left
                    && d.Bounds.Top == info.Bounds.Top && d.Bounds.Right == info.Bounds.Right
                    && d.Bounds.Bottom == info.Bounds.Bottom)) return true;
                snapshot.Displays.Add(new Display { Handle = handle, Bounds = info.Bounds,
                    Work = info.Work, Primary = (info.Flags & 1) != 0 });
                return true;
            }, IntPtr.Zero)) snapshot.Reliable = false;

        if (!EnumWindows(delegate(IntPtr handle, IntPtr data)
        {
            if (handle == excluded || !IsWindowVisible(handle) || IsIconic(handle)) return true;
            uint cloaked;
            bool hiddenDesktop = DwmGetWindowAttribute(handle, 14, out cloaked, 4) == 0 && cloaked != 0;
            StringBuilder name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            if (!CountsAsOccupant(true, false, hiddenDesktop,
                GetProp(handle, OwnerProperty) != IntPtr.Zero || IsParked(handle),
                GetWindowLongPtr(handle, -20).ToInt64(), name.ToString())) return true;
            Rect bounds;
            // DWM frame bounds exclude invisible resize borders spilling onto the next screen.
            if (DwmGetWindowAttribute(handle, 9, out bounds, Marshal.SizeOf(typeof(Rect))) != 0
                && !GetWindowRect(handle, out bounds))
            {
                if (IsWindow(handle)) snapshot.Reliable = false;
                return true;
            }
            if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top) snapshot.Occupants.Add(bounds);
            return true;
        }, IntPtr.Zero)) snapshot.Reliable = false;
        return snapshot;
    }

    internal static LayoutRequest CaptureLayoutRequest(bool allowSplit = true)
    {
        Stopwatch watch=Stopwatch.StartNew();
        IntPtr foreground = GetForegroundWindow();
        LayoutRequest request = new LayoutRequest {
            PreferredMonitor = MonitorFromWindow(foreground, 2),
            Foreground = allowSplit && !(Program.WindowMode!=null && Program.WindowMode.PreferMaximized)
                && IsSplitCandidate(foreground) ? SaveWindow(foreground) : null };
        try
        {
            request.Snapshot=CaptureDesktop(IntPtr.Zero);
            request.SampledAt=Stopwatch.GetTimestamp();
            Program.Log("layout-request displays="+request.Snapshot.Displays.Count+" scanMs="+watch.ElapsedMilliseconds
                +" occupants="+String.Join(",",request.Snapshot.Displays.ConvertAll(d=>request.Snapshot.Occupants.FindAll(r=>Overlaps(r,d.Work)).Count.ToString()).ToArray()));
        }
        catch (Exception error) { Program.Log("layout-scan-error " + error.GetType().Name); }
        return request;
    }

    private static void LogLayout(string phase, LayoutPlan plan, bool reliable)
    {
        Program.Log("layout-" + phase + " displays=" + plan.DisplayCount + " empty=" + plan.EmptyCount
            + " reliable=" + reliable + " mode=" + (plan.RememberedMaximize ? "remembered-maximize" : plan.Maximize ? "empty-maximize" : plan.FreeBounds.HasValue ? "free-space" : plan.WantsSplit ? "split-candidate" : "normal-16x9")
            + " monitor=" + (plan.Target == null ? IntPtr.Zero : plan.Target.Handle));
    }

    internal static bool ApplyRequestedLayout(IntPtr handle, LayoutRequest request)
    {
        if (!IsWindow(handle) || ReadAppId(handle) != AppId) return false;
        using(WindowModeTracker.IgnoreChanges(handle))
        lock (layoutGate)
        {
            Stopwatch watch=Stopwatch.StartNew();
            bool preferMaximized=Program.WindowMode!=null && Program.WindowMode.PreferMaximized;
            try
            {
                // Reuse only this request's fresh snapshot, never a previous hotkey's state.
                bool fresh=request.Snapshot!=null && (Stopwatch.GetTimestamp()-request.SampledAt)*1000.0/Stopwatch.Frequency<100;
                DesktopSnapshot snapshot = fresh ? request.Snapshot : CaptureDesktop(handle);
                request.Snapshot=null;
                LayoutPlan plan = ChooseLayout(snapshot, request.PreferredMonitor,preferMaximized);
                LogLayout("delivery", plan, snapshot.Reliable);
                if (plan.WantsSplit && request.Foreground != null
                    && TrySplitPair(request.Foreground, handle, plan.Target, true)) return true;
                if (plan.Target != null && PositionNewWindow(handle, plan)) return true;
            }
            catch (Exception error) { Program.Log("layout-apply-error " + error.GetType().Name); }
            finally { Program.Log("layout-duration ms="+watch.ElapsedMilliseconds); }
            if(preferMaximized)
            {
                // Do not silently discard the user's preference on a monitor/DPI race.
                Program.Log("layout-fallback maximize-current-monitor");
                ShowWindow(handle,3);
                return IsZoomed(handle);
            }
            Program.Log("layout-fallback original-16x9");
            return ApplyPreferredBounds(handle);
        }
    }

    // Only called for a verified newly-created PWA (or the test harness's own form).
    internal static bool PositionNewWindow(IntPtr handle, LayoutPlan plan)
    {
        Rect bounds = plan.FreeBounds ?? PreferredBounds(plan.Target.Work);
        if(plan.Maximize && IsZoomed(handle) && MonitorFromWindow(handle,2)==plan.Target.Handle)
        {
            // A prepared maximized window on the right screen needs no restore/resize flash.
            ShowWindow(handle,3);
            bool maximized=IsZoomed(handle);
            Program.Log("layout-applied success="+maximized+" maximized=True fast-show=True");
            return maximized;
        }
        if (IsIconic(handle) || IsZoomed(handle)) ShowWindow(handle, 4);
        // Establish normal/restore bounds on the target BEFORE maximizing there.
        if (!SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014)) return false;
        ShowWindow(handle, plan.Maximize ? 3 : 4);
        // Edge may rescale a previously hidden window while handling its first show/DPI change.
        // Apply the normal size again after that transition, in physical pixels.
        if (!plan.Maximize && !SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014)) return false;
        Rect actual = new Rect();
        bool success = MonitorFromWindow(handle, 2) == plan.Target.Handle
            && IsZoomed(handle) == plan.Maximize && GetWindowRect(handle, out actual);
        if (!plan.Maximize) success = success && actual.Left == bounds.Left && actual.Top == bounds.Top
            && actual.Right == bounds.Right && actual.Bottom == bounds.Bottom;
        Program.Log("layout-applied success=" + success + " maximized=" + IsZoomed(handle)
            + " width=" + (actual.Right - actual.Left) + " height=" + (actual.Bottom - actual.Top));
        return success;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out Rect value, int size);
}
