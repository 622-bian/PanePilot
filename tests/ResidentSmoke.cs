using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// Explicitly opt-in integration test. Covers each screen with its own temporary blank form,
// invokes the resident's public IPC, and leaves the created GPT window open after undo.
internal static class ResidentSmoke
{
    [STAThread]
    private static int Main()
    {
        string root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
        string result=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"resident-smoke-result.txt");
        var forms=new List<Form>();
        bool restoreMaximum=true, modeChanged=false;
        WindowSupport.EnableDpiAwareness();
        try
        {
            AppSettings.Load(Path.Combine(root,"config.json"));
            var desktop=WindowSupport.CaptureDesktop(IntPtr.Zero);
            File.WriteAllText(result,"Starting resident split/undo integration test\r\n");
            string statusPath=Path.Combine(root,"preload-status.json");
            string before=File.ReadAllText(statusPath);
            restoreMaximum=!before.Contains("\"mode\":\"automatic\"");
            using(var automatic=EventWaitHandle.OpenExisting(@"Local\PanePilot_AutomaticMode")) automatic.Set();
            modeChanged=true;
            for(int i=0;i<50;i++) {
                Pump(100);
                if(File.ReadAllText(statusPath).Contains("\"mode\":\"automatic\"")) break;
                if(i==49) throw new Exception("Automatic mode was not selected");
            }
            long previous=Delivered(before);
            using(var take=EventWaitHandle.OpenExisting(@"Local\PanePilot_Take"))
            using(var undo=EventWaitHandle.OpenExisting(@"Local\PanePilot_Undo"))
            {
                foreach(var display in desktop.Displays)
                {
                    var form=new Form { Text="PanePilot resident test (temporary)", StartPosition=FormStartPosition.Manual };
                    forms.Add(form);
                    if(!WindowSupport.PositionNewWindow(form.Handle,new WindowSupport.LayoutPlan {Target=display,Maximize=true}))
                        throw new Exception("Cannot cover test monitor");
                }
                if(forms.Count == 0) throw new Exception("No test monitor");
                Form original=forms[0];
                original.Text="PanePilot integration control";
                bool start=false, cancelled=false;
                var startButton=new Button { Text="Run split and undo test", Dock=DockStyle.Fill };
                startButton.Click+=delegate { start=true; };
                original.Controls.Add(startButton);
                original.AcceptButton=startButton;
                original.FormClosed+=delegate { cancelled=true; };
                original.Show();
                // An explicit click gives the test a genuine foreground activation, without
                // trying to bypass Windows focus-stealing protection.
                for(int i=0;i<1200 && !start && !cancelled;i++) Pump(100);
                if(!start || cancelled) throw new Exception("Test was not started");
                WindowSupport.SavedWindow saved=WindowSupport.SaveWindow(original.Handle);
                if(WindowSupport.GetForegroundWindow() != original.Handle) throw new Exception("Test foreground unavailable; no launch requested");
                take.Set();
                IntPtr created=IntPtr.Zero;
                for(int i=0;i<100;i++)
                {
                    Pump(100);
                    long delivered=Delivered(File.ReadAllText(statusPath));
                    if(delivered != 0 && delivered != previous) { created=new IntPtr(delivered); break; }
                }
                if(created == IntPtr.Zero) throw new Exception("No preloaded delivery; prepare a spare before this test");
                WindowSupport.Rect left,right,current;
                if(!WindowSupport.TrySplitBounds(saved.Work,out left,out right)) throw new Exception("Screen too narrow for test");
                // lastDelivered is published when ownership transfers, before layout finishes.
                // Keep servicing the test form's messages while the resident resizes it cross-process.
                bool split=false;
                for(int i=0;i<50;i++)
                {
                    Pump(100);
                    bool originalPlaced=WindowSupport.GetWindowRect(original.Handle,out current) && WindowSupport.SameRect(left,current);
                    bool gptPlaced=WindowSupport.GetWindowRect(created,out current) && WindowSupport.SameRect(right,current);
                    if(originalPlaced && gptPlaced) { split=true; break; }
                }
                if(!split) throw new Exception("Resident did not complete both split columns");
                File.AppendAllText(result,"PASS real GPT + captured foreground split\r\n");
                undo.Set();
                bool restored=false;
                for(int i=0;i<40;i++)
                {
                    Pump(100);
                    var originalAfter=WindowSupport.SaveWindow(original.Handle);
                    var gptAfter=WindowSupport.SaveWindow(created);
                    if(originalAfter.Placement.Show == saved.Placement.Show
                        && WindowSupport.SameRect(originalAfter.Placement.Normal,saved.Placement.Normal)
                        && !WindowSupport.SameRect(gptAfter.Outer,right)) { restored=true; break; }
                }
                if(!restored) throw new Exception("Resident undo did not restore windows");
                if(!File.ReadAllText(statusPath).Contains("\"mode\":\"automatic\""))
                    throw new Exception("Programmatic split/undo changed the user's mode");
                File.AppendAllText(result,"PASS IPC undo restored original maximized state and kept GPT open\r\n");
            }
            return 0;
        }
        catch(Exception e) { File.AppendAllText(result,"FAIL " + e + "\r\n"); return 1; }
        finally {
            foreach(var form in forms) { form.Close(); form.Dispose(); }
            if(modeChanged) using(var restoreMode=EventWaitHandle.OpenExisting(restoreMaximum ? @"Local\PanePilot_MaximizeMode" : @"Local\PanePilot_AutomaticMode")) restoreMode.Set();
        }
    }
    private static long Delivered(string status)
    {
        var match=Regex.Match(status,"\"lastDelivered\":([0-9]+)");
        return match.Success ? Int64.Parse(match.Groups[1].Value) : 0;
    }
    private static void Pump(int milliseconds)
    {
        DateTime end=DateTime.UtcNow.AddMilliseconds(milliseconds);
        while(DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
