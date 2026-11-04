using System;
using System.IO;
using System.Windows.Forms;

internal static class LayoutTests
{
    private static int checks;
    internal static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }
    internal static WindowSupport.Rect R(int x, int y, int w, int h)
    {
        return new WindowSupport.Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }
    private static WindowSupport.Display D(int id, int x, int y, int w, int h, bool primary)
    {
        return new WindowSupport.Display { Handle = new IntPtr(id), Bounds = R(x,y,w,h),
            Work = R(x,y,w,h-40), Primary = primary };
    }
    private static WindowSupport.DesktopSnapshot Dual()
    {
        var s = new WindowSupport.DesktopSnapshot();
        s.Displays.Add(D(1, 0, 0, 2560, 1440, true));
        s.Displays.Add(D(2, -1920, 0, 1920, 1080, false));
        return s;
    }
    [STAThread]
    private static int Main()
    {
        try
        {
            SettingsTests();
            var s = Dual();
            s.Occupants.Add(R(0,0,2560,1400));
            var p = WindowSupport.ChooseLayout(s, new IntPtr(1));
            Check(p.Target.Handle.ToInt64() == 2 && p.Maximize, "occupied main -> empty secondary maximized");
            s.Occupants.Add(R(-1900,10,500,500));
            p = WindowSupport.ChooseLayout(s,new IntPtr(1));
            Check(p.Target.Handle.ToInt64() == 1 && !p.Maximize, "both busy -> active screen normal");
            p = WindowSupport.ChooseLayout(s,new IntPtr(2));
            Check(p.Target.Handle.ToInt64() == 2 && !p.Maximize, "active screen may be secondary");
            s.Occupants.Clear();
            Check(WindowSupport.ChooseLayout(s,new IntPtr(2)).Target.Handle.ToInt64() == 2, "both empty -> preferred screen");
            Check(WindowSupport.ChooseLayout(s,new IntPtr(99)).Target.Primary, "missing preferred -> primary");
            s.Occupants.Add(R(-100,100,200,300));
            Check(WindowSupport.ChooseLayout(s,new IntPtr(1)).EmptyCount == 0, "straddling window occupies both");
            s.Occupants.Clear(); s.Occupants.Add(R(0,100,500,300));
            Check(WindowSupport.ChooseLayout(s,new IntPtr(1)).EmptyCount == 1, "touching edge is not overlap");
            s.Displays.RemoveAt(1);
            p = WindowSupport.ChooseLayout(s,new IntPtr(2));
            Check(p.Target.Primary && !p.Maximize, "unplug secondary -> remaining busy screen");
            s.Occupants.Clear();
            Check(WindowSupport.ChooseLayout(s,IntPtr.Zero).Maximize, "single empty screen maximizes");
            s.Reliable = false;
            Check(!WindowSupport.ChooseLayout(s,IntPtr.Zero).Maximize, "incomplete scan never declares empty");
            s.Displays.Clear();
            Check(WindowSupport.ChooseLayout(s,IntPtr.Zero).Target == null, "no displays -> safe fallback");
            foreach (var work in new[] { R(0,0,2560,1392), R(-1920,0,1920,1040), R(0,-1080,1920,1040), R(0,0,640,360) })
            {
                var r = WindowSupport.PreferredBounds(work);
                Check((r.Right-r.Left)*9 == (r.Bottom-r.Top)*16 && r.Right-r.Left <= 720
                    && r.Left >= work.Left && r.Right <= work.Right && r.Top >= work.Top && r.Bottom <= work.Bottom,
                    "16:9 fits work area including negative origins");
            }
            Check(WindowSupport.CountsAsOccupant(true,false,false,false,0,"normal"), "normal app counts");
            Check(!WindowSupport.CountsAsOccupant(false,false,false,false,0,"normal"), "hidden ignored");
            Check(!WindowSupport.CountsAsOccupant(true,true,false,false,0,"normal"), "minimized ignored");
            Check(!WindowSupport.CountsAsOccupant(true,false,true,false,0,"normal"), "other desktop/cloaked ignored");
            Check(!WindowSupport.CountsAsOccupant(true,false,false,true,0,"normal"), "preload ignored");
            Check(!WindowSupport.CountsAsOccupant(true,false,false,false,0x80,"normal"), "tool window ignored");
            Check(WindowSupport.CountsAsOccupant(true,false,false,false,0x40080,"normal"), "explicit app window counts");
            foreach (string shell in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                Check(!WindowSupport.CountsAsOccupant(true,false,false,false,0,shell), "shell ignored: " + shell);
            Console.WriteLine("PASS total=" + checks);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void SettingsTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "panepilot-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string edge = Path.Combine(folder, "msedge_proxy.exe");
        string config = Path.Combine(folder, "config.json");
        try
        {
            File.WriteAllText(edge, "test fixture; never executed");
            var settings = new AppSettings { EdgePath = edge, ProfileDirectory = "Default",
                PwaAppId = new string('a',32), WindowAppId = "chatgpt.com-example!App" };
            settings.Validate();
            Check(settings.LaunchArguments("panepilot-test-1").Contains("https://chatgpt.com/?panepilot-test-1=1"), "launch URL preserves ownership marker");
            using (var stream = File.Create(config))
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, settings);
            AppSettings.Load(config);
            Check(AppSettings.Current.WindowAppId == settings.WindowAppId, "config round trip preserves PWA identity");
            settings.ProfileDirectory = "Default\" --other";
            Check(Rejected(delegate { settings.Validate(); }), "profile cannot inject command arguments");
            settings.ProfileDirectory = "Default";
            settings.PwaAppId = "wrong";
            Check(Rejected(delegate { settings.Validate(); }), "invalid PWA ID rejected");
            settings.PwaAppId = new string('a',32);
            settings.WindowAppId = "another-app";
            Check(Rejected(delegate { settings.Validate(); }), "non-ChatGPT window identity rejected");
            settings.WindowAppId = "chatgpt.com-example!App";
            settings.EdgePath = Path.Combine(folder,"missing.exe");
            Check(Rejected(delegate { settings.Validate(); }), "missing browser rejected");
            Check(Rejected(delegate { settings.LaunchArguments("x\" y"); }), "marker delimiters rejected");
            File.WriteAllText(config,"{}");
            Check(Rejected(delegate { AppSettings.Load(config); }), "incomplete config rejected");
        }
        finally
        {
            File.Delete(config); File.Delete(edge); Directory.Delete(folder);
        }
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (System.Runtime.Serialization.SerializationException) { return true; }
        catch (InvalidDataException) { return true; }
        catch (ArgumentException) { return true; }
    }
}

// Optional integration test: only moves its own blank form, never another app's window.
internal static class NativeLayoutSmoke
{
    [STAThread]
    private static int Main()
    {
        string result = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-layout-result.txt");
        WindowSupport.EnableDpiAwareness();
        try
        {
            var s = WindowSupport.CaptureDesktop(IntPtr.Zero);
            File.WriteAllText(result, "displays=" + s.Displays.Count + " reliable=" + s.Reliable + "\r\n");
            if (s.Displays.Count == 0 || !s.Reliable) throw new Exception("No reliable desktop snapshot");
            using (Form form = new Form { Text = "PanePilot layout test (temporary)", StartPosition = FormStartPosition.Manual })
            {
                IntPtr handle = form.Handle;
                foreach (var d in s.Displays)
                {
                    var plan = new WindowSupport.LayoutPlan { Target = d, Maximize = true };
                    if (!WindowSupport.PositionNewWindow(handle, plan)) throw new Exception("Maximize verification failed");
                    Application.DoEvents();
                    if (form.WindowState != FormWindowState.Maximized) throw new Exception("Not maximized");
                    plan.Maximize = false;
                    if (!WindowSupport.PositionNewWindow(handle, plan)) throw new Exception("Restore/size verification failed");
                    Application.DoEvents();
                    var expected = WindowSupport.PreferredBounds(d.Work);
                    WindowSupport.Rect actual;
                    if (!WindowSupport.GetWindowRect(handle, out actual) || actual.Left != expected.Left
                        || actual.Top != expected.Top || actual.Right != expected.Right || actual.Bottom != expected.Bottom)
                        throw new Exception("Normal bounds differ");
                    File.AppendAllText(result, "PASS monitor=" + d.Handle + " work=" + d.Work.Left + "," + d.Work.Top
                        + "," + d.Work.Right + "," + d.Work.Bottom + " maximize + restore16x9\r\n");
                }
                form.Close();
            }
            File.AppendAllText(result, "PASS native layout\r\n");
            return 0;
        }
        catch (Exception e) { File.AppendAllText(result, "FAIL " + e + "\r\n"); return 1; }
    }
}
