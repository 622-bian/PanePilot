using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private const string InstanceName = @"Local\PanePilot";
    internal static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
    internal static WindowModeStore WindowMode;

    internal static void Log(string message)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Launch(WindowSupport.LayoutRequest layout = null)
    {
        // A one-shot process cannot retain undo state; only resident requests may split.
        if (layout == null) layout = WindowSupport.CaptureLayoutRequest(false);
        layout.Snapshot=null; // Cold launches wait for Edge; refresh again at delivery.
        Stopwatch watch = Stopwatch.StartNew();
        var existing = new System.Collections.Generic.HashSet<IntPtr>();
        foreach (var info in WindowSupport.Windows()) existing.Add(info.Handle);
        string marker = "panepilot-open-" + Guid.NewGuid().ToString("N");
        LaunchPreloaded(marker, false);
        Log("launch-dispatched ms=" + watch.ElapsedMilliseconds);
        // Resident fallback requests retain their captured foreground and undo capability.
        while (watch.ElapsedMilliseconds < 15000)
        {
            foreach (var info in WindowSupport.Windows())
            {
                if (existing.Contains(info.Handle) || info.AppId != WindowSupport.AppId) continue;
                try
                {
                    bool editor;
                    if (WindowSupport.MatchesMarker(info.Handle, marker, out editor))
                    {
                        Log("normal-window-sized pid="+Process.GetCurrentProcess().Id+" hwnd="+info.Handle+" success=" + WindowSupport.ApplyRequestedLayout(info.Handle, layout));
                        if(WindowModeTracker.Active!=null) WindowModeTracker.Active.Track(info.Handle);
                        return;
                    }
                }
                catch (Exception) { }
            }
            Thread.Sleep(150);
        }
        Log("normal-window-size-unverified");
    }

    internal static void LaunchPreloaded(string marker, bool background = true)
    {
        AppSettings settings = AppSettings.Current;
        using (Process process = Process.Start(new ProcessStartInfo(settings.EdgePath, settings.LaunchArguments(marker))
            { UseShellExecute = false, WindowStyle = background ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal })) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        WindowSupport.EnableDpiAwareness();
        try
        {
            if (args.Length == 1 && args[0] == "--check-config") { LoadSettings(); return 0; }
            if(args.Length==1 && (args[0]=="--maximize-mode" || args[0]=="--automatic-mode"))
            {
                bool maximize=args[0]=="--maximize-mode";
                EventWaitHandle signal;
                if(EventWaitHandle.TryOpenExisting(InstanceName+(maximize ? "_MaximizeMode" : "_AutomaticMode"),out signal))
                    using(signal) signal.Set();
                else { LoadSettings(); WindowMode.Set(maximize,"command"); }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--once") { LoadSettings(); Launch(); return 0; }
            if (args.Length == 1 && args[0] == "--take")
            {
                EventWaitHandle signal;
                if (EventWaitHandle.TryOpenExisting(InstanceName + "_Take", out signal))
                    using (signal) { signal.Set(); }
                else { LoadSettings(); Launch(); }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--undo")
            {
                EventWaitHandle signal;
                if (EventWaitHandle.TryOpenExisting(InstanceName + "_Undo", out signal))
                    using (signal) { signal.Set(); }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--stop")
            {
                EventWaitHandle signal;
                if (EventWaitHandle.TryOpenExisting(InstanceName + "_Stop", out signal))
                    using (signal) { signal.Set(); }
                return 0;
            }
            bool created;
            using (Mutex instance = new Mutex(true, InstanceName, out created))
            {
                if (!created) return 0;
                try
                {
                    LoadSettings();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_Stop"))
                    using (EventWaitHandle take = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_Take"))
                    using (EventWaitHandle undo = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_Undo"))
                    using (EventWaitHandle maximize = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_MaximizeMode"))
                    using (EventWaitHandle automatic = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_AutomaticMode"))
                    using (HotkeyContext context = new HotkeyContext(stop, take, undo, maximize, automatic))
                        Application.Run(context);
                }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception error)
        {
            Log("error " + error.Message);
            if (!(args.Length == 1 && args[0] == "--check-config"))
                MessageBox.Show("PanePilot 未能启动：\n" + error.Message + "\n请先运行 scripts/Configure.ps1 配置 Edge 网页应用。", "PanePilot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static void LoadSettings()
    {
        AppSettings.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
        WindowMode=new WindowModeStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"window-mode.local.json"));
    }
}

internal sealed class HotkeyContext : ApplicationContext
{
    private readonly HotkeyWindow window;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private bool disposed;
    private readonly PreloadPool pool;
    private readonly WindowModeTracker modeTracker;

    internal HotkeyContext(EventWaitHandle stop, EventWaitHandle take, EventWaitHandle undo, EventWaitHandle maximize, EventWaitHandle automatic)
    {
        window = new HotkeyWindow();
        pool = new PreloadPool();
        modeTracker=new WindowModeTracker(Program.WindowMode);
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ChatGPT 新窗口 · Ctrl+Alt+G", Visible = true };
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("新建 ChatGPT 窗口", null, delegate { LaunchSafely(); });
        var maximizeMode=new ToolStripMenuItem("新窗口：保持最大化");
        var automaticMode=new ToolStripMenuItem("新窗口：自动布局");
        maximizeMode.Click+=delegate { SetMode(true); };
        automaticMode.Click+=delegate { SetMode(false); };
        menu.Items.Add(maximizeMode); menu.Items.Add(automaticMode);
        ToolStripMenuItem undoItem = new ToolStripMenuItem("撤销上次分屏 · Ctrl+Alt+Shift+G");
        undoItem.Click += delegate { UndoSafely(); };
        menu.Items.Add(undoItem);
        ToolStripMenuItem pause = new ToolStripMenuItem("暂停预加载");
        pause.Click += delegate { pool.TogglePause(); };
        menu.Items.Add(pause);
        menu.Items.Add("退出快捷键程序", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { LaunchSafely(); };
        window.Pressed += delegate { LaunchSafely(); };
        window.UndoPressed += delegate { UndoSafely(); };
        timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += delegate
        {
            if (stop.WaitOne(0)) { ExitThread(); return; }
            if (maximize.WaitOne(0)) SetMode(true);
            if (automatic.WaitOne(0)) SetMode(false);
            if (take.WaitOne(0)) LaunchSafely();
            if (undo.WaitOne(0)) UndoSafely();
            modeTracker.Tick();
            Program.WindowMode.FlushPending(DateTime.UtcNow);
            maximizeMode.Checked=Program.WindowMode.PreferMaximized;
            automaticMode.Checked=!maximizeMode.Checked;
            undoItem.Enabled = WindowSupport.HasSplitUndo;
            pool.Tick();
            pause.Text=pool.NeedsResume ? "恢复预加载" : "暂停预加载";
            tray.Text = "ChatGPT · " + (maximizeMode.Checked ? "最大化 · " : "自动布局 · ") + pool.StatusText;
        };
        timer.Start();
        Program.Log("ready pid=" + Process.GetCurrentProcess().Id + " hotkey=Ctrl+Alt+G undoHotkey=" + window.UndoAvailable);
        if (!window.UndoAvailable)
            tray.ShowBalloonTip(5000,"PanePilot","撤销快捷键被占用，可使用托盘的撤销入口。",ToolTipIcon.Warning);
    }

    private void LaunchSafely()
    {
        try { modeTracker.Tick(); pool.Take(); }
        catch (Exception error)
        {
            Program.Log("launch-error " + error.Message);
            tray.ShowBalloonTip(5000, "ChatGPT 启动失败", error.Message, ToolTipIcon.Warning);
        }
    }

    private void SetMode(bool maximize)
    {
        modeTracker.Tick();
        Program.WindowMode.Set(maximize,"explicit-choice");
    }

    private void UndoSafely()
    {
        try { tray.ShowBalloonTip(2500,"PanePilot",WindowSupport.UndoLastSplit(),ToolTipIcon.Info); }
        catch (Exception error) { Program.Log("undo-error " + error.GetType().Name); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            timer.Stop();
            timer.Dispose();
            pool.Dispose();
            modeTracker.Dispose();
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            window.Dispose();
            Program.Log("stopped");
        }
        base.Dispose(disposing);
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    internal event EventHandler Pressed;
    internal event EventHandler UndoPressed;
    internal bool UndoAvailable { get; private set; }

    internal HotkeyWindow()
    {
        CreateHandle(new CreateParams { Caption = "ChatGPTFastHotkey", Parent = new IntPtr(-3) });
        // Ctrl + Alt + G. MOD_NOREPEAT prevents held keys opening many windows.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (RegisterHotKey(Handle, 1, 0x4003, 0x47)) break;
            int error = Marshal.GetLastWin32Error();
            if (error != 1409 || attempt == 29)
            {
                DestroyHandle();
                throw new Win32Exception(error, "无法注册 Ctrl+Alt+G，可能仍被其他程序占用。");
            }
            Thread.Sleep(500);
        }
        UndoAvailable=RegisterHotKey(Handle,2,0x4007,0x47);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == 1)
        {
            EventHandler handler = Pressed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == 2)
        {
            EventHandler handler=UndoPressed;
            if (handler != null) handler(this,EventArgs.Empty);
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, 1);
        if (UndoAvailable) UnregisterHotKey(Handle,2);
        DestroyHandle();
    }
}
