using System;
using System.IO;

internal static class PreferenceTests
{
    internal static void Run()
    {
        var observe=new WindowModeObservation();
        observe.Baseline(false,true);
        Check(observe.Observe(true,true,false,false)==null,"minimizing never changes maximized preference");
        Check(observe.Observe(false,false,false,false)==null,"closing/hiding never changes preference");
        Check(observe.Observe(true,false,true,true)==null,"restoring from minimized does not change preference");
        Check(observe.Observe(true,false,false,true)==false,"explicit normal restore chooses automatic layout");
        Check(observe.Observe(true,false,true,true)==true,"explicit maximize chooses maximized layout");
        observe.Baseline(false,false);
        Check(observe.Observe(true,false,false,true)==null,"programmatic layout establishes baseline without changing preference");
        Check(observe.Observe(true,false,false,true)==null,"focusing an older normal window does not reset preference");
        observe.Baseline(false,true); observe.Minimizing();
        Check(observe.Observe(true,false,false,true)==null,"minimize transition cannot look like manual restore");

        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"panepilot-preference-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"window-mode.local.json");
        try {
            var store=new WindowModeStore(path);
            Check(store.PreferMaximized,"fresh installation defaults to maximized");
            store.Set(false,"test");
            Check(!new WindowModeStore(path).PreferMaximized,"automatic choice survives restart");
            store.Set(true,"test");
            Check(new WindowModeStore(path).PreferMaximized,"maximized choice survives restart");
            File.WriteAllText(path,"invalid");
            Check(new WindowModeStore(path).PreferMaximized,"corrupt preference cannot silently force small windows");
            File.WriteAllText(path,"null");
            Check(new WindowModeStore(path).PreferMaximized,"null preference defaults safely without stopping the helper");
            File.Delete(path);
            Directory.CreateDirectory(path);
            store=new WindowModeStore(path);
            store.Set(false,"test-save-failure");
            Directory.Delete(path);
            store.FlushPending(DateTime.UtcNow.AddSeconds(6));
            Check(!new WindowModeStore(path).PreferMaximized,"temporary save failure retries without losing latest choice");
        } finally { File.Delete(path); File.Delete(path+".tmp"); Directory.Delete(directory); }

        var desktop=new WindowSupport.DesktopSnapshot();
        var main=new WindowSupport.Display { Handle=new IntPtr(1), Primary=true, Work=LayoutTests.R(0,0,1920,1040) };
        var second=new WindowSupport.Display { Handle=new IntPtr(2), Work=LayoutTests.R(-1920,0,1920,1040) };
        desktop.Displays.Add(main); desktop.Displays.Add(second);
        desktop.Occupants.Add(main.Work);
        var plan=WindowSupport.ChooseLayout(desktop,main.Handle,true);
        Check(plan.Maximize && plan.Target==second,"maximized preference still prioritizes an empty monitor");
        desktop.Occupants.Add(LayoutTests.R(-1920,0,100,100));
        plan=WindowSupport.ChooseLayout(desktop,main.Handle,true);
        Check(plan.Maximize && plan.Target==main && !plan.FreeBounds.HasValue && !plan.WantsSplit,"busy monitors do not downgrade maximized preference");
        desktop.Occupants.Add(second.Work);
        plan=WindowSupport.ChooseLayout(desktop,second.Handle,true);
        Check(plan.Maximize && plan.Target==second && !plan.WantsSplit,"remembered mode skips split even when all monitors are full");
        Check(WindowSupport.ChooseLayout(desktop,second.Handle,false).WantsSplit,"automatic mode retains the existing split policy");
        desktop.Displays.Remove(second);
        Check(WindowSupport.ChooseLayout(desktop,second.Handle,true).Target==main,"unplugged monitor preserves maximum mode on remaining screen");

        var retry=new PreloadRecovery(); DateTime now=new DateTime(2026,1,1);
        Check(retry.CanAttempt(now,false,false),"healthy preload can run");
        retry.Fail(now);
        Check(!retry.CanAttempt(now,false,false),"preload failure has a cooldown");
        Check(retry.CanAttempt(now.AddSeconds(5),false,false),"safe fault retries after cooldown");
        Check(!retry.CanAttempt(now.AddMinutes(1),false,true),"uncertain live window prevents duplicate preload");
        Check(!retry.CanAttempt(now.AddMinutes(1),true,false),"manual pause cannot auto resume");
        retry.Fail(now); retry.Fail(now);
        Check(!retry.CanAttempt(now.AddDays(1),false,false),"repeated faults stop after two retries");
        retry.Reset();
        Check(retry.CanAttempt(now,false,false),"explicit resume or success resets retry budget");
    }
    private static void Check(bool condition,string text) { LayoutTests.Check(condition,text); }
}
