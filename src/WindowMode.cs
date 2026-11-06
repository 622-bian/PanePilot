using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

// A user's preference is separate from the geometry of any particular window.
internal sealed class WindowModeStore
{
    [DataContract] private sealed class Data { [DataMember(IsRequired=true)] public bool PreferMaximized; }
    private readonly object gate=new object();
    private readonly string path;
    private bool maximized=true;
    private bool persisted;
    private DateTime nextSave;
    internal bool PreferMaximized { get { lock(gate) return maximized; } }
    internal WindowModeStore(string path)
    {
        this.path=path;
        if(!File.Exists(path)) return;
        try {
            using(var input=File.OpenRead(path)) {
                var data=(Data)new DataContractJsonSerializer(typeof(Data)).ReadObject(input);
                if(data==null) throw new SerializationException("Missing window mode");
                maximized=data.PreferMaximized;
            }
            persisted=true;
        } catch(Exception error) {
            if(!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is SerializationException)) throw;
            Program.Log("window-mode-load-error " + error.GetType().Name);
        }
    }
    internal void Set(bool value,string reason)
    {
        lock(gate)
        {
            if(maximized==value && persisted) return;
            maximized=value;
            persisted=false;
            nextSave=DateTime.UtcNow.AddSeconds(5);
            string temp=path+".tmp";
            try {
                using(var output=File.Create(temp))
                    new DataContractJsonSerializer(typeof(Data)).WriteObject(output,new Data { PreferMaximized=value });
                if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                persisted=true;
                Program.Log("window-mode=" + (value ? "maximized" : "automatic") + " reason=" + reason);
            } catch(IOException) { Program.Log("window-mode-save-error IOException"); }
              catch(UnauthorizedAccessException) { Program.Log("window-mode-save-error UnauthorizedAccessException"); }
        }
    }
    internal void FlushPending(DateTime now)
    {
        lock(gate) if(!persisted && now>=nextSave) Set(maximized,"persist-pending");
    }
}

// Pure transition policy. Hiding/minimizing/focusing an older window is not a size preference.
internal sealed class WindowModeObservation
{
    private bool? lastMaximized;
    private bool wasMinimized;
    internal void Baseline(bool minimized,bool maximized)
    { wasMinimized=minimized; if(!minimized) lastMaximized=maximized; }
    internal void Minimizing() { wasMinimized=true; }
    internal bool? Observe(bool visible,bool minimized,bool maximized,bool active)
    {
        if(!visible) return null;
        if(minimized) { wasMinimized=true; return null; }
        if(wasMinimized) { Baseline(false,maximized); return null; }
        if(!active) return null;
        bool? previous=lastMaximized;
        lastMaximized=maximized;
        return previous.HasValue && previous.Value!=maximized ? (bool?)maximized : null;
    }
}

internal sealed class WindowModeTracker : IDisposable
{
    private sealed class Entry { internal uint Pid; internal IntPtr Token; internal WindowModeObservation State=new WindowModeObservation(); }
    private const string Identity="PanePilot.ModeIdentity";
    private readonly object gate=new object();
    private readonly Dictionary<IntPtr,Entry> entries=new Dictionary<IntPtr,Entry>();
    private readonly Dictionary<IntPtr,int> suppressed=new Dictionary<IntPtr,int>();
    private readonly List<IntPtr> hooks=new List<IntPtr>();
    private readonly WindowModeStore store;
    private readonly WinEventProc callback;
    private IntPtr foreground;
    private bool disposed;
    private long nextToken=DateTime.UtcNow.Ticks;
    internal static WindowModeTracker Active;

    internal WindowModeTracker(WindowModeStore store)
    {
        this.store=store; callback=OnEvent; Active=this;
        foreach(uint eventId in new uint[] {3,0x16,0x17,0x8001,0x800B})
        {
            IntPtr hook=SetWinEventHook(eventId,eventId,IntPtr.Zero,callback,0,0,0);
            if(hook!=IntPtr.Zero) hooks.Add(hook); else Program.Log("window-mode-hook-unavailable event="+eventId);
        }
        foreach(var info in WindowSupport.Windows())
            if(info.AppId==WindowSupport.AppId) Track(info.Handle);
        Tick();
    }
    internal void Track(IntPtr handle)
    {
        lock(gate)
        {
            if(disposed || !WindowSupport.IsWindowVisible(handle) || WindowSupport.IsParked(handle)
                || WindowSupport.GetProp(handle,WindowSupport.OwnerProperty)!=IntPtr.Zero) return;
            Entry existing;
            if(entries.TryGetValue(handle,out existing) && Valid(handle,existing)) return;
            var entry=new Entry { Token=new IntPtr(++nextToken) };
            if(WindowSupport.GetWindowThreadProcessId(handle,out entry.Pid)==0
                || !WindowSupport.SetProp(handle,Identity,entry.Token)) return;
            entry.State.Baseline(WindowSupport.IsIconic(handle),WindowSupport.IsZoomed(handle));
            entries[handle]=entry;
        }
    }
    private bool Valid(IntPtr handle,Entry entry)
    {
        uint pid;
        return WindowSupport.IsWindow(handle) && WindowSupport.GetWindowThreadProcessId(handle,out pid)!=0
            && pid==entry.Pid && WindowSupport.GetProp(handle,Identity)==entry.Token;
    }
    internal void Tick()
    {
        IntPtr current=WindowSupport.GetForegroundWindow();
        if(current!=foreground)
        {
            foreground=current;
            StringBuilder name=new StringBuilder(256);
            GetClassName(current,name,name.Capacity);
            if(name.ToString()=="Chrome_WidgetWin_1" && WindowSupport.ReadAppId(current)==WindowSupport.AppId) Track(current);
        }
        Sample(current);
    }
    private void Sample(IntPtr handle)
    {
        lock(gate)
        {
            Entry entry;
            if(disposed || !entries.TryGetValue(handle,out entry)) return;
            if(!Valid(handle,entry)) { entries.Remove(handle); return; }
            if(suppressed.ContainsKey(handle) || WindowSupport.IsParked(handle)
                || WindowSupport.GetProp(handle,WindowSupport.OwnerProperty)!=IntPtr.Zero) return;
            bool? choice=entry.State.Observe(WindowSupport.IsWindowVisible(handle),WindowSupport.IsIconic(handle),
                WindowSupport.IsZoomed(handle),WindowSupport.GetForegroundWindow()==handle);
            if(choice.HasValue) store.Set(choice.Value,"user-window-state");
        }
    }
    private void OnEvent(IntPtr hook,uint kind,IntPtr handle,int obj,int child,uint thread,uint time)
    {
        try {
            if(handle==IntPtr.Zero || obj!=0 || child!=0 || disposed) return;
            if(kind==3) { Tick(); return; }
            lock(gate)
            {
                if(kind==0x8001) { entries.Remove(handle); return; }
                Entry entry;
                if(kind==0x16 && entries.TryGetValue(handle,out entry)) { entry.State.Minimizing(); return; }
            }
            Sample(handle);
        } catch(Exception error) { Program.Log("window-mode-observe-error "+error.GetType().Name); }
    }
    internal static IDisposable IgnoreChanges(IntPtr handle)
    {
        var owner=Active;
        if(owner!=null) lock(owner.gate) {
            int count; owner.suppressed.TryGetValue(handle,out count); owner.suppressed[handle]=count+1;
        }
        return new Scope(owner,handle);
    }
    private sealed class Scope : IDisposable
    {
        private WindowModeTracker owner; private readonly IntPtr handle;
        internal Scope(WindowModeTracker owner,IntPtr handle) { this.owner=owner; this.handle=handle; }
        public void Dispose()
        {
            if(owner==null) return;
            lock(owner.gate) {
                Entry entry;
                if(owner.entries.TryGetValue(handle,out entry) && owner.Valid(handle,entry))
                    entry.State.Baseline(WindowSupport.IsIconic(handle),WindowSupport.IsZoomed(handle));
                int count;
                if(owner.suppressed.TryGetValue(handle,out count)) {
                    if(count<=1) owner.suppressed.Remove(handle); else owner.suppressed[handle]=count-1;
                }
            }
            owner=null;
        }
    }
    public void Dispose()
    {
        disposed=true;
        foreach(IntPtr hook in hooks) UnhookWinEvent(hook);
        lock(gate) foreach(var item in entries)
            if(Valid(item.Key,item.Value)) WindowSupport.RemoveProp(item.Key,Identity);
        if(Active==this) Active=null;
        GC.KeepAlive(callback);
    }
    private delegate void WinEventProc(IntPtr hook,uint kind,IntPtr handle,int obj,int child,uint thread,uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventProc proc,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle,StringBuilder name,int size);
}
