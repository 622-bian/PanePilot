using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static partial class WindowSupport
{
    private const string SplitIdentity = "PanePilot.SplitIdentity";
    private static long nextIdentity = DateTime.UtcNow.Ticks;
    private static SplitUndo lastSplit;

    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Placement
    {
        internal int Length, Flags, Show;
        internal Point Min, Max;
        internal Rect Normal;
    }
    internal sealed class SavedWindow
    {
        internal IntPtr Handle, Identity, Monitor;
        internal uint Process;
        internal Placement Placement;
        internal Rect Outer, MonitorBounds, Work;
    }
    private sealed class SplitUndo { internal SavedWindow Original, Gpt; }

    internal static bool HasSplitUndo { get { lock (layoutGate) return lastSplit != null; } }

    internal static bool SameRect(Rect a, Rect b)
    {
        return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    }

    internal static bool TrySplitBounds(Rect work, out Rect left, out Rect right)
    {
        int mid = work.Left + (work.Right - work.Left) / 2;
        left = new Rect { Left=work.Left, Top=work.Top, Right=mid, Bottom=work.Bottom };
        right = new Rect { Left=mid, Top=work.Top, Right=work.Right, Bottom=work.Bottom };
        // Do not create unusably narrow columns on portrait/small screens.
        return mid - work.Left >= 720 && work.Right - mid >= 720 && work.Bottom - work.Top >= 405;
    }

    internal static bool IsSplitCandidate(IntPtr handle)
    {
        if (!IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle)
            || GetWindow(handle, 4) != IntPtr.Zero || IsParked(handle)) return false;
        long style = GetWindowLongPtr(handle, -16).ToInt64();
        long extended = GetWindowLongPtr(handle, -20).ToInt64();
        uint cloaked;
        return (style & 0x00040000L) != 0 && (style & 0x00C00000L) == 0x00C00000L
            && (extended & 0x80L) == 0
            && DwmGetWindowAttribute(handle, 14, out cloaked, 4) == 0 && cloaked == 0;
    }

    internal static SavedWindow SaveWindow(IntPtr handle)
    {
        if (!IsWindow(handle)) return null;
        var saved = new SavedWindow { Handle=handle, Monitor=MonitorFromWindow(handle,2),
            Placement = new Placement { Length = Marshal.SizeOf(typeof(Placement)) } };
        MonitorInfo monitor = new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetWindowPlacement(handle, ref saved.Placement) || !GetWindowRect(handle, out saved.Outer)
            || !GetMonitorInfo(saved.Monitor, ref monitor)
            || GetWindowThreadProcessId(handle, out saved.Process) == 0) return null;
        saved.MonitorBounds=monitor.Bounds; saved.Work=monitor.Work;
        saved.Identity=GetProp(handle,SplitIdentity);
        if (saved.Identity == IntPtr.Zero)
        {
            saved.Identity=new IntPtr(Interlocked.Increment(ref nextIdentity));
            if (!SetProp(handle,SplitIdentity,saved.Identity)) return null;
        }
        return saved;
    }

    private static bool SameWindow(SavedWindow saved)
    {
        uint pid;
        return saved != null && IsWindow(saved.Handle)
            && GetWindowThreadProcessId(saved.Handle,out pid) != 0 && pid == saved.Process
            && GetProp(saved.Handle,SplitIdentity) == saved.Identity;
    }

    private static bool SameDisplay(SavedWindow saved)
    {
        MonitorInfo current=new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)) };
        return GetMonitorInfo(saved.Monitor,ref current) && SameRect(current.Bounds,saved.MonitorBounds)
            && SameRect(current.Work,saved.Work);
    }

    internal static bool UnchangedSinceRequest(SavedWindow saved)
    {
        Rect current;
        return SameWindow(saved) && SameDisplay(saved) && IsSplitCandidate(saved.Handle)
            && MonitorFromWindow(saved.Handle,2) == saved.Monitor
            && GetWindowRect(saved.Handle,out current) && SameRect(current,saved.Outer)
            && IsZoomed(saved.Handle) == (saved.Placement.Show == 3);
    }

    internal static bool PlaceNormal(IntPtr handle, Rect bounds)
    {
        if (IsIconic(handle) || IsZoomed(handle)) ShowWindow(handle,4);
        if (!SetWindowPos(handle,IntPtr.Zero,bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top,0x0014)) return false;
        ShowWindow(handle,4);
        // Edge can apply its DPI/minimum-track constraints during first show.
        if (!SetWindowPos(handle,IntPtr.Zero,bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top,0x0014)) return false;
        Rect actual;
        return GetWindowRect(handle,out actual) && SameRect(actual,bounds) && !IsZoomed(handle);
    }

    internal static bool TrySplitPair(SavedWindow original, IntPtr gpt, Display display, bool checkForeground)
    {
        lock (layoutGate)
        {
            Rect left,right;
            IntPtr foreground=GetForegroundWindow();
            if (original == null || display == null || original.Handle == gpt || original.Monitor != display.Handle
                || !UnchangedSinceRequest(original) || !TrySplitBounds(display.Work,out left,out right)
                || (checkForeground && foreground != original.Handle && foreground != gpt))
            { Program.Log("split-skipped reason=target-changed-or-ineligible"); return false; }

            // Undo keeps the new chat visible as an ordinary window; it never closes a chat.
            PositionNewWindow(gpt,new LayoutPlan { Target=display });
            SavedWindow gptBefore=SaveWindow(gpt);
            if (gptBefore == null) return false;
            bool moved=false;
            try
            {
                // Verify the new window fits before changing an existing user window.
                if (!PlaceNormal(gpt,right)) return false;
                if (!UnchangedSinceRequest(original)) return false;
                moved=true;
                if (!PlaceNormal(original.Handle,left)) return false;
                Rect gptActual;
                if (!GetWindowRect(gpt,out gptActual) || !SameRect(gptActual,right)) return false;
                lastSplit=new SplitUndo { Original=original, Gpt=gptBefore };
                Program.Log("split-applied success=True undo=True");
                return true;
            }
            finally
            {
                // A complete successful transaction is the only path that replaces the undo record.
                if (lastSplit == null || lastSplit.Gpt != gptBefore)
                {
                    bool restoredOriginal=!moved || RestoreSaved(original);
                    bool restoredGpt=RestoreSaved(gptBefore);
                    if (!restoredOriginal || !restoredGpt)
                        lastSplit=new SplitUndo { Original=original, Gpt=gptBefore };
                    Program.Log("split-rollback original=" + restoredOriginal + " gpt=" + restoredGpt);
                }
            }
        }
    }

    internal static bool RestoreSaved(SavedWindow saved)
    {
        if (!SameWindow(saved)) return true; // Closed windows are not recreated; reused handles are never touched.
        if (!SameDisplay(saved)) return false;
        // WINDOWPLACEMENT uses workspace coordinates. Pass it back unchanged, not to SetWindowPos.
        Placement placement=saved.Placement;
        if (!SetWindowPlacement(saved.Handle,ref placement)) return false;
        Placement actual=new Placement { Length=Marshal.SizeOf(typeof(Placement)) };
        return GetWindowPlacement(saved.Handle,ref actual) && actual.Show == saved.Placement.Show
            && SameRect(actual.Normal,saved.Placement.Normal);
    }

    internal static string UndoLastSplit()
    {
        lock (layoutGate)
        {
            if (lastSplit == null) return "没有可撤销的分屏";
            bool original=RestoreSaved(lastSplit.Original);
            bool gpt=RestoreSaved(lastSplit.Gpt);
            Program.Log("split-undo original=" + original + " gpt=" + gpt);
            if (!original || !gpt) return "部分窗口未能恢复；请检查显示器布局或窗口状态后重试";
            if (SameWindow(lastSplit.Original)) SetForegroundWindow(lastSplit.Original.Handle);
            lastSplit=null;
            return "已撤销最近一次分屏";
        }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle,uint command);
}
