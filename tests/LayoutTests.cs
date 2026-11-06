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
            PreferenceTests.Run();
            var s = Dual();
            s.Occupants.Add(R(0,0,2560,1400));
            var p = WindowSupport.ChooseLayout(s, new IntPtr(1));
            Check(p.Target.Handle.ToInt64() == 2 && p.Maximize, "occupied main -> empty secondary maximized");
            s.Occupants.Add(R(-1900,10,500,500));
            p = WindowSupport.ChooseLayout(s,new IntPtr(1));
            Check(p.Target.Handle.ToInt64() == 2 && p.FreeBounds.HasValue, "occupied primary -> free rectangle on secondary");
            p = WindowSupport.ChooseLayout(s,new IntPtr(2));
            Check(p.Target.Handle.ToInt64() == 2 && p.FreeBounds.HasValue, "active screen free area preferred");
            s.Occupants.Add(R(-1920,0,1920,1040));
            p=WindowSupport.ChooseLayout(s,new IntPtr(1));
            Check(p.Target.Primary && p.WantsSplit && !p.FreeBounds.HasValue, "no empty area -> split candidate on active screen");
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
            FreeAreaTests();
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

    private static void FreeAreaTests()
    {
        WindowSupport.Rect free,left,right;
        var obstacles=new System.Collections.Generic.List<WindowSupport.Rect> { R(0,0,800,900) };
        Check(WindowSupport.TryFindFreeBounds(R(0,0,1920,1080),obstacles,out free)
            && free.Left >= 800 && !WindowSupport.Overlaps(free,obstacles[0]), "finds a non-overlapping blank rectangle");
        Check(!WindowSupport.TryFindFreeBounds(R(0,0,700,400),new System.Collections.Generic.List<WindowSupport.Rect>(),out free), "tiny blank space is unusable");
        obstacles.Add(R(800,0,1120,1080));
        Check(!WindowSupport.TryFindFreeBounds(R(0,0,1920,1080),obstacles,out free), "thin leftover strip is not enough");
        Check(WindowSupport.TrySplitBounds(R(-2560,0,2561,1392),out left,out right)
            && left.Left == -2560 && left.Right == right.Left && right.Right == 1
            && !WindowSupport.Overlaps(left,right), "odd-width negative-origin split covers monitor without overlap");
        Check(!WindowSupport.TrySplitBounds(R(0,0,1080,1920),out left,out right), "portrait screen refuses unusably narrow split");
        var unreliable=Dual(); unreliable.Reliable=false;
        Check(!WindowSupport.ChooseLayout(unreliable,new IntPtr(1)).WantsSplit
            && !WindowSupport.ChooseLayout(unreliable,new IntPtr(1)).FreeBounds.HasValue, "incomplete scan never moves existing windows");
        // Compare the edge-search algorithm with a small exhaustive oracle at one-pixel resolution.
        var random=new Random(71);
        for (int trial=0;trial<20;trial++)
        {
            var work=R(-20,-15,740,425);
            var blocks=new System.Collections.Generic.List<WindowSupport.Rect>();
            for(int i=0;i<3;i++) blocks.Add(R(random.Next(-30,730),random.Next(-25,420),random.Next(1,40),random.Next(1,40)));
            bool brute=false;
            for(int y=work.Top;y<=work.Bottom-405;y++)
                for(int x=work.Left;x<=work.Right-720;x++)
                {
                    var candidate=R(x,y,720,405);
                    if(!blocks.Exists(b=>WindowSupport.Overlaps(candidate,b))) brute=true;
                }
            bool found=WindowSupport.TryFindFreeBounds(work,blocks,out free);
            Check(found == brute && (!found || !blocks.Exists(b=>WindowSupport.Overlaps(free,b))), "free-space search agrees with exhaustive oracle " + trial);
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
                    SplitSmoke(form,d,result);
                }
                form.Close();
            }
            File.AppendAllText(result, "PASS native layout\r\n");
            return 0;
        }
        catch (Exception e) { File.AppendAllText(result, "FAIL " + e + "\r\n"); return 1; }
    }

    private static void SplitSmoke(Form original, WindowSupport.Display display,string result)
    {
        WindowSupport.Rect left,right;
        if(!WindowSupport.TrySplitBounds(display.Work,out left,out right)) return;
        using(var gpt=new Form { Text="PanePilot split test (temporary)", StartPosition=FormStartPosition.Manual })
        {
            IntPtr second=gpt.Handle;
            var saved=WindowSupport.SaveWindow(original.Handle);
            if(!WindowSupport.TrySplitPair(saved,second,display,false)) throw new Exception("Normal split failed");
            WindowSupport.Rect actual;
            if(!WindowSupport.GetWindowRect(original.Handle,out actual) || !WindowSupport.SameRect(actual,left)) throw new Exception("Original not in left column");
            if(!WindowSupport.GetWindowRect(second,out actual) || !WindowSupport.SameRect(actual,right)) throw new Exception("GPT not in right column");
            WindowSupport.UndoLastSplit();
            if(WindowSupport.HasSplitUndo || !WindowSupport.GetWindowRect(original.Handle,out actual)
                || !WindowSupport.SameRect(actual,saved.Outer)) throw new Exception("Normal undo failed");

            WindowSupport.ShowWindow(original.Handle,3); Application.DoEvents();
            saved=WindowSupport.SaveWindow(original.Handle);
            if(!WindowSupport.TrySplitPair(saved,second,display,false)) throw new Exception("Maximized split failed");
            WindowSupport.UndoLastSplit();
            Application.DoEvents();
            if(WindowSupport.HasSplitUndo || WindowSupport.SaveWindow(original.Handle).Placement.Show != 3) throw new Exception("Maximized undo failed");
            WindowSupport.ShowWindow(original.Handle,4); Application.DoEvents();

            saved=WindowSupport.SaveWindow(original.Handle);
            var shifted=saved.Outer; shifted.Left+=20; shifted.Right+=20;
            WindowSupport.PlaceNormal(original.Handle,shifted);
            if(WindowSupport.TrySplitPair(saved,second,display,false)) throw new Exception("Stale foreground was moved");
            WindowSupport.RestoreSaved(saved);

            gpt.MinimumSize=new System.Drawing.Size(display.Work.Right-display.Work.Left,500);
            saved=WindowSupport.SaveWindow(original.Handle);
            if(WindowSupport.TrySplitPair(saved,second,display,false)) throw new Exception("Minimum-width failure accepted");
            if(!WindowSupport.GetWindowRect(original.Handle,out actual) || !WindowSupport.SameRect(actual,saved.Outer)) throw new Exception("Failed split moved original");
            gpt.MinimumSize=System.Drawing.Size.Empty;

            saved=WindowSupport.SaveWindow(original.Handle);
            if(!WindowSupport.TrySplitPair(saved,second,display,false)) throw new Exception("Closed-partner setup failed");
            gpt.Close(); Application.DoEvents();
            WindowSupport.UndoLastSplit();
            if(WindowSupport.HasSplitUndo || !WindowSupport.GetWindowRect(original.Handle,out actual)
                || !WindowSupport.SameRect(actual,saved.Outer)) throw new Exception("Closed-partner undo failed");
            File.AppendAllText(result,"PASS split + normal/maximized undo + stale target + minimum-width rollback + closed partner monitor=" + display.Handle + "\r\n");
        }
    }
}
