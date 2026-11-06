using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// Opt-in desktop test; only minimizes/restores/closes GPT windows created by its own requests.
internal static class PreferenceSmoke
{
    private static string root,result;
    [STAThread]
    private static int Main()
    {
        WindowSupport.EnableDpiAwareness();
        root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
        result=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preference-smoke-result.txt");
        File.WriteAllText(result,"Starting maximize/minimize/close preference test\r\n");
        try
        {
            AppSettings.Load(Path.Combine(root,"config.json"));
            using(var control=new Form { Text="PanePilot preference test", Width=540, Height=220, StartPosition=FormStartPosition.CenterScreen })
            {
                bool start=false,cancelled=false;
                var button=new Button {Text="Run maximize / minimize / close test",Dock=DockStyle.Fill};
                button.Click+=delegate { start=true; };
                control.FormClosed+=delegate { cancelled=true; };
                control.Controls.Add(button); control.AcceptButton=button; control.Show();
                Wait(delegate { return start || cancelled; },120000,"test start");
                if(cancelled) throw new Exception("Cancelled");
                Signal("MaximizeMode"); WaitMode("maximized");

                IntPtr first=TakeMaximized();
                WindowSupport.ShowWindow(first,6);
                Wait(delegate { return WindowSupport.IsIconic(first); },3000,"minimize test GPT");
                Pump(250); WaitMode("maximized");
                IntPtr second=TakeMaximized();
                if(!WindowSupport.IsIconic(first)) throw new Exception("Old minimized window was restored unexpectedly");
                Pass("maximize -> minimize -> next window is maximized");

                WindowSupport.PostMessage(second,0x0010,IntPtr.Zero,IntPtr.Zero);
                Wait(delegate { return !WindowSupport.IsWindow(second); },5000,"close test GPT");
                WaitMode("maximized");
                IntPtr third=TakeMaximized();
                Pass("maximize -> close -> next window is maximized");

                if(!WindowSupport.SetForegroundWindow(third)) throw new Exception("Cannot activate test GPT for user-state simulation");
                Pump(150);
                WindowSupport.ShowWindow(third,9);
                WaitMode("automatic");
                Pass("manual restore changes preference to automatic");
                WindowSupport.ShowWindow(third,3);
                WaitMode("maximized");
                if(!new WindowModeStore(Path.Combine(root,"window-mode.local.json")).PreferMaximized)
                    throw new Exception("Maximized choice was not persisted");
                Pass("manual maximize restores and persists maximized preference");

                using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,"PanePilot.exe"),"--once") { UseShellExecute=false, WindowStyle=ProcessWindowStyle.Hidden }))
                {
                    Wait(delegate { return process.HasExited; },25000,"cold launch");
                    if(process.ExitCode!=0) throw new Exception("Cold launch process failed");
                    var matches=Regex.Matches(File.ReadAllText(Path.Combine(root,"launcher.log")),
                        "normal-window-sized pid="+process.Id+" hwnd=([0-9]+) success=True");
                    if(matches.Count!=1) throw new Exception("Cold-launch result not uniquely verified");
                    IntPtr cold=new IntPtr(Int64.Parse(matches[0].Groups[1].Value));
                    if(!WindowSupport.IsWindow(cold) || !WindowSupport.IsZoomed(cold)) throw new Exception("Cold launch lost maximized preference");
                }
                Pass("separate one-shot process reloads saved maximum mode");
                control.Close();
            }
            Pass("preference integration"); return 0;
        }
        catch(Exception error) { File.AppendAllText(result,"FAIL "+error+"\r\n"); return 1; }
        finally { try { Signal("MaximizeMode"); } catch(WaitHandleCannotBeOpenedException) {} }
    }
    private static IntPtr TakeMaximized()
    {
        Wait(delegate { return ReadStatus().Contains("\"state\":\"ready\""); },45000,"ready spare");
        long previous=LastDelivered();
        Signal("Take");
        IntPtr handle=IntPtr.Zero;
        Wait(delegate {
            long current=LastDelivered();
            if(current==0 || current==previous) return false;
            handle=new IntPtr(current);
            return WindowSupport.IsWindow(handle) && WindowSupport.IsWindowVisible(handle)
                && WindowSupport.ReadAppId(handle)==WindowSupport.AppId && WindowSupport.IsZoomed(handle)
                && DeliveryLogged(handle);
        },10000,"maximized delivery");
        return handle;
    }
    private static long LastDelivered()
    {
        var match=Regex.Match(ReadStatus(),"\"lastDelivered\":([0-9]+)");
        return match.Success ? Int64.Parse(match.Groups[1].Value) : 0;
    }
    private static bool DeliveryLogged(IntPtr handle)
    {
        try { return File.ReadAllText(Path.Combine(root,"launcher.log")).Contains("preload-delivered hwnd="+handle+" "); }
        catch(IOException) { return false; }
    }
    private static string ReadStatus() { try { return File.ReadAllText(Path.Combine(root,"preload-status.json")); } catch(IOException) { return ""; } }
    private static void WaitMode(string mode)
    { Wait(delegate { return ReadStatus().Contains("\"mode\":\""+mode+"\""); },5000,"mode="+mode); }
    private static void Signal(string action)
    { using(var signal=EventWaitHandle.OpenExisting(@"Local\PanePilot_"+action)) signal.Set(); }
    private static void Wait(Func<bool> ready,int milliseconds,string label)
    {
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<milliseconds) { Pump(50); if(ready()) return; }
        throw new Exception("Timed out: "+label);
    }
    private static void Pump(int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Pass(string label) { File.AppendAllText(result,"PASS "+label+"\r\n"); }
}
