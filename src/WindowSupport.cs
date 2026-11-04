using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static partial class WindowSupport
{
    internal static string AppId { get { return AppSettings.Current.WindowAppId; } }
    internal const string OwnerProperty = "PanePilot.PreloadedWindow";
    internal sealed class Info
    {
        internal IntPtr Handle;
        internal uint ProcessId;
        internal string AppId;
    }
    internal static List<Info> Windows()
    {
        List<Info> result = new List<Info>();
        EnumWindows(delegate(IntPtr handle, IntPtr data)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString() != "Chrome_WidgetWin_1") return true;
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    if (process.ProcessName != "msedge") return true;
                result.Add(new Info { Handle = handle, ProcessId = processId, AppId = ReadAppId(handle) });
            }
            catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static string ReadAppId(IntPtr handle)
    {
        IPropertyStore store;
        Guid iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(handle, ref iid, out store) != 0) return "";
        try
        {
            PropertyKey key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            PropVariant value;
            if (store.GetValue(ref key, out value) != 0) return "";
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // Reads only document URL and the existence of an editable control, never chat text.
    // Caller must restrict this to windows created after its own launch request.
    internal static bool MatchesMarker(IntPtr handle, string marker, out bool hasEditor)
    {
        hasEditor = false;
        AutomationElement root = AutomationElement.FromHandle(handle);
        if (root == null) return false;
        AutomationElementCollection docs = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement doc in docs)
        {
            object raw = doc.GetCurrentPropertyValue(ValuePattern.ValueProperty, true);
            string url = raw as string;
            if (url == null || !url.StartsWith("https://chatgpt.com/", StringComparison.OrdinalIgnoreCase) || !url.Contains(marker)) continue;
            hasEditor = doc.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)) != null;
            return true;
        }
        return false;
    }

    internal static bool HasEditor(IntPtr handle)
    {
        AutomationElement root = AutomationElement.FromHandle(handle);
        if (root == null) return false;
        AutomationElement doc = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        return doc != null && doc.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true))) != null;
    }

    internal static bool IsParked(IntPtr handle)
    {
        return GetProp(handle, OwnerProperty + ".Parked") != IntPtr.Zero;
    }

    internal static void Park(IntPtr handle)
    {
        // Transparency does not change geometry. Never save iconic/minimized rectangles.
        IntPtr style = GetWindowLongPtr(handle, -20);
        SetProp(handle, OwnerProperty + ".Style", style);
        uint color, flags;
        byte alpha;
        if (GetLayeredWindowAttributes(handle, out color, out alpha, out flags))
        {
            SetProp(handle, OwnerProperty + ".Alpha", new IntPtr(alpha));
            SetProp(handle, OwnerProperty + ".Flags", new IntPtr(flags));
            SetProp(handle, OwnerProperty + ".Color", new IntPtr(color));
        }
        // Keep an on-screen viewport for Chromium accessibility and page initialization.
        // Click-through + no-activate prevents this nearly transparent window taking input.
        SetWindowLongPtr(handle, -20, new IntPtr(style.ToInt64() | 0x00080000L | 0x00000020L | 0x08000000L));
        if (!SetLayeredWindowAttributes(handle, 0, 1, 2))
        {
            SetWindowLongPtr(handle, -20, style);
            throw new InvalidOperationException("Cannot prepare transparent preload window.");
        }
        SetProp(handle, OwnerProperty + ".Parked", new IntPtr(1));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0033);
        ChangeTaskbar(handle, false);
    }

    internal static void RestorePlacement(IntPtr handle, bool addToTaskbar)
    {
        if (IsParked(handle))
        {
            IntPtr style = GetProp(handle, OwnerProperty + ".Style");
            uint flags = unchecked((uint)GetProp(handle, OwnerProperty + ".Flags").ToInt64());
            if ((style.ToInt64() & 0x00080000L) != 0 && flags != 0)
                SetLayeredWindowAttributes(handle, unchecked((uint)GetProp(handle, OwnerProperty + ".Color").ToInt64()),
                    (byte)GetProp(handle, OwnerProperty + ".Alpha").ToInt32(), flags);
            SetWindowLongPtr(handle, -20, style);
        }
        // Remove legacy geometry properties without applying potentially corrupt dimensions.
        foreach (string suffix in new[] { ".X", ".Y", ".W", ".H", ".Style", ".Alpha", ".Flags", ".Color", ".Parked" }) RemoveProp(handle, OwnerProperty + suffix);
        if (addToTaskbar) ChangeTaskbar(handle, true);
    }

    internal static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
    }

    internal static Rect PreferredBounds(Rect work)
    {
        // Exact 16:9 outer bounds, at most 720x405 physical pixels.
        int units = Math.Min(45, Math.Min((work.Right - work.Left - 32) / 16, (work.Bottom - work.Top - 32) / 9));
        if (units < 1) throw new ArgumentException("Monitor work area is too small.");
        int width = units * 16, height = units * 9;
        int x = work.Left + (work.Right - work.Left - width) / 2;
        int y = work.Top + (work.Bottom - work.Top - height) / 2;
        return new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    internal static bool ApplyPreferredBounds(IntPtr handle)
    {
        if (!IsWindow(handle) || ReadAppId(handle) != AppId) return false;
        MonitorInfo monitor = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return false;
        Rect bounds = PreferredBounds(monitor.Work);
        if (IsIconic(handle) || IsZoomed(handle)) ShowWindow(handle, 4);
        return SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014);
    }

    private static void ChangeTaskbar(IntPtr handle, bool add)
    {
        object value = null;
        try
        {
            value = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11D0-958A-006097C9A090")));
            ITaskbarList taskbar = (ITaskbarList)value;
            taskbar.HrInit();
            if (add) taskbar.AddTab(handle); else taskbar.DeleteTab(handle);
        }
        catch (COMException) { }
        finally { if (value != null) Marshal.ReleaseComObject(value); }
    }

    internal delegate bool EnumProc(IntPtr handle, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal Rect Bounds; internal Rect Work; internal uint Flags; }
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr handle, int command);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr handle, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetProp(IntPtr handle, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr handle, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr handle, string name);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid Format; internal uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("56FDF342-FD6D-11D0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr handle);
        void DeleteTab(IntPtr handle);
        void ActivateTab(IntPtr handle);
        void SetActiveAlt(IntPtr handle);
    }
}
