using Microsoft.Win32;
using NvGraphics.Abstractions;
using NvGraphics.Models;
using NvGraphics.Services;

var errors = new List<string>(); int tested=0;
void Assert(bool value, string name) { tested++; Console.WriteLine((value?"PASS ":"FAIL ")+name); if(!value)errors.Add(name); }

const string exe=@"C:\Games\Sample\Game.exe";
var fakeReg=new InMemoryRegistryApi();
var dir=Path.Combine(Path.GetTempPath(),"NvGraphics-regtest-"+Guid.NewGuid().ToString("N"));
var pref=new WindowsGpuPreferenceService(fakeReg,new RegistryBackupService(dir));
var key=WindowsGpuPreferenceService.KeyPath;
try
{
    fakeReg.SetValue(key,exe,"SpecificAdapter=10DE&1B06&85F11043;GpuPreference=1073741824;AutoHDREnable=1;",
        RegistryValueKind.String);
    Assert(pref.ApplyWindowsPolicy(exe,2).Success,"save High performance and verify read-back");
    var val=pref.FindValue(exe);
    Assert(WindowsGpuPreferenceService.ParseWindowsGpuPolicy(val)==2,"read Windows GPU policy");
    Assert(!val.Contains("SpecificAdapter") && val.Contains("AutoHDREnable=1"),
        "policy transition removes old adapter and preserves unrelated tokens");
    Assert(pref.ApplyWindowsPolicy(exe,1).Success && pref.FindValue(exe).Contains("GpuPreference=1;"),
        "Power saving mode");
    Assert(pref.ApplyWindowsPolicy(exe,0).Success && pref.FindValue(exe).Contains("GpuPreference=0;"),
        "Let Windows decide");
    Assert(pref.ApplyPreference(exe,"VEN_10DE&DEV_1B06&SUBSYS_120F10DE").Success &&
        pref.FindValue(exe).Contains("SpecificAdapter=10DE&1B06&120F10DE;"),
        "Specific GPU writes and verifies physical adapter identity");
    Assert(pref.FindValue(exe).Contains("AutoHDREnable=1;"),"Specific GPU preserves other settings");
    Assert(!pref.ApplyWindowsPolicy(exe,5).Success,"invalid GPU policy rejected");
    Assert(pref.RemoveEntry(exe).Success && pref.FindValue(exe)==null,"remove app-specific value only");
    Assert(Directory.GetFiles(dir,"*.json").Length>=4,"backups created before edits");
    const string other=@"C:\Games\Sample\Other.exe";
    fakeReg.SetValue(key,exe,
        "SpecificAdapter=10DE&1B06&85F11043;GpuPreference=1073741824;AutoHDREnable=1;VRROptimizeEnable=0;",
        RegistryValueKind.String);
    fakeReg.SetValue(key,other,"GpuPreference=2;",RegistryValueKind.String);
    var backupCount=Directory.GetFiles(dir,"*.json").Length;
    Assert(pref.ResetGpuSelection(exe).Success,"reset GPU selection succeeds");
    Assert(pref.FindValue(exe)=="AutoHDREnable=1;VRROptimizeEnable=0;",
        "GPU reset preserves unrelated Windows graphics settings");
    Assert(pref.FindValue(other)=="GpuPreference=2;",
        "reset does not modify other applications");
    Assert(Directory.GetFiles(dir,"*.json").Length==backupCount+1,
        "GPU reset creates a backup before mutation");
    var record=new RegistryBackupService(dir).ListBackups().First(r=>r.ValueName==exe);
    Assert(pref.RestoreBackup(record).Success &&
        pref.FindValue(exe).Contains("SpecificAdapter=10DE&1B06&85F11043;"),
        "restored Windows setting verifies the full backed-up GPU value");
    fakeReg.SetValue(key,exe,"GpuPreference=2;",RegistryValueKind.String);
    Assert(pref.ResetGpuSelection(exe).Success && pref.FindValue(exe) is null,
        "reset removes entire value only when no unrelated tokens remain");
    var longId=@"C:\"+new string('Q',430)+".exe";
    var service=new RegistryBackupService(dir);
    var namesBefore=Directory.GetFiles(dir,"*.json").Select(Path.GetFileName).ToHashSet();
    service.SaveBackup(key,longId,"GpuPreference=2;",RegistryValueKind.String,"fast backup one");
    service.SaveBackup(key,longId,"GpuPreference=1;",RegistryValueKind.String,"fast backup two");
    var addedNames=Directory.GetFiles(dir,"*.json").Select(Path.GetFileName)
        .Where(name=>!namesBefore.Contains(name)).ToArray();
    Assert(addedNames.Length==2 && addedNames.Distinct().Count()==2,
        "rapid backups cannot overwrite one another");
    Assert(addedNames.All(name=>name.Length<130),
        "backup filenames stay bounded for very long application IDs");

    const string la="0x00000000_0x00000001", lb="0x00000000_0x00000002"; const int pid=12345;
    var gpuA=new GpuAdapterInfo { DisplayName="GPU A", AdapterId="VEN_10DE&DEV_1B06&SUBSYS_85F11043" };
    var gpuB=new GpuAdapterInfo { DisplayName="GPU B", AdapterId="VEN_10DE&DEV_1B06&SUBSYS_120F10DE" };
    EngineSample E(string luid,string engine,double pct)=>new(pid,luid,engine,pct,DateTimeOffset.UtcNow);
    RuntimeGpuResult Run(params EngineSample[][] windows) =>
        new RuntimeGpuMonitorService(new Counter(windows),new AdapterResolver(
            new Dictionary<string,GpuAdapterInfo>{{la,gpuA},{lb,gpuB}}),new PathResolver(pid,exe),
            new RuntimeGpuMonitorOptions { WindowSamples=windows.Length,PollInterval=TimeSpan.Zero,
                ActivityThresholdPercent=0.5 },_=>{}).Check(exe);
    var partial=Run(
        new[]{E(la,"3d",22),E(la,"copy",17),E(la,"videoencode",10)},
        Array.Empty<EngineSample>(),Array.Empty<EngineSample>());
    Assert(partial.State==RuntimeGpuState.Unknown,
        "multiple engine counters cannot fake three complete PID samples");
    var auxiliary=Run(new[]{E(la,"3d",0.01),E(la,"copy",30)},new[]{E(la,"3d",0.01),E(la,"copy",27)});
    Assert(auxiliary.State==RuntimeGpuState.Unknown,"copy load cannot count as 3D render evidence");
    var single=Run(new[]{E(la,"3d",10)},new[]{E(la,"3d",20)});
    Assert(single.State==RuntimeGpuState.Verified && single.Adapters.Count==1,"single GPU verified");
    var multi=Run(new[]{E(la,"3d",10),E(lb,"3d",20)},new[]{E(la,"3d",12),E(lb,"3d",18)});
    Assert(multi.State==RuntimeGpuState.MultipleAdapters && multi.Adapters.Count==2,
        "simultaneous GPUs are reported separately");

    // Two real GameProfileStore instances sharing one file must reject stale overwrites.
    var profilePath=Path.Combine(dir,"shared-profiles.json");
    var storeA=new GameProfileStore(profilePath);
    var storeB=new GameProfileStore(profilePath);
    var initial=storeA.Load();
    initial.Profiles.Add(new GameProfile {Name="First",ExePath=@"C:\Demo\first.exe"});
    storeA.Save(initial);
    var stale=storeA.Load();
    var updated=storeB.Load();
    updated.Profiles.Add(new GameProfile {Name="Second",ExePath=@"C:\Demo\second.exe"});
    storeB.Save(updated);
    var rejected=false;
    try {
        stale.Profiles.Add(new GameProfile {Name="Third",ExePath=@"C:\Demo\third.exe"});
        storeA.Save(stale);
    } catch (IOException) { rejected=true; }
    Assert(rejected,"cross-process stale profile overwrite rejected");
    Assert(storeB.Load().Profiles.Count==2,"fresh concurrent profile is preserved");
    storeB.Save(updated);
    Assert(storeB.Load().Profiles.Count==2,"same snapshot can be saved again");
    Assert(new GameProfileStore(profilePath).Load().Profiles.Count==2,
        "new process view loads shared profiles");
    // Atomic Update merges targeted edits with fresh data even after another process writes.
    var staleSnapshot=storeA.Load();
    var profileC=@"C:\Demo\merged.exe";
    var merged=storeB.Update(latest=>{
        latest.Profiles.Add(new GameProfile {Name="Merged",ExePath=profileC});
        return true;
    });
    Assert(merged.Profiles.Count==3 && storeA.Load().Profiles.Count==3,
        "atomic targeted update keeps all other profiles");
    var mergeOther=storeA.Update(latest=>{
        var p=latest.Profiles.First(x=>x.ExePath==profileC);
        p.Name="Merged Again";
        return true;
    });
    Assert(mergeOther.Profiles.Count==3 &&
        storeB.Load().Profiles.Single(x=>x.ExePath==profileC).Name=="Merged Again",
        "cross-process update merges latest targeted fields");

    var evt=new NvGraphics.Contracts.GpuPreferencePayload {
        SchemaVersion=1, EventId=Guid.NewGuid().ToString("N"),
        ExePath=exe, DisplayName="Demo", IsSystemDefault=true,
        WindowsGpuPolicy=2, AppliedUtc=DateTimeOffset.UtcNow
    };
    byte[] AsJson(NvGraphics.Contracts.GpuPreferencePayload v)=>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(v);
    Assert(NvGraphics.Contracts.GpuPreferencePayloadValidator.TryValidate(
        AsJson(evt),DateTimeOffset.UtcNow,out _,out _,out _),
        "power preference event accepted by Container contract");
    evt.WindowsGpuPolicy=5;
    Assert(!NvGraphics.Contracts.GpuPreferencePayloadValidator.TryValidate(
        AsJson(evt),DateTimeOffset.UtcNow,out _,out _,out _),
        "invalid power policy rejected by Container contract");
    evt.WindowsGpuPolicy=1;
    evt.AdapterId="10DE&1B06&85F11043";
    Assert(!NvGraphics.Contracts.GpuPreferencePayloadValidator.TryValidate(
        AsJson(evt),DateTimeOffset.UtcNow,out _,out _,out _),
        "conflicting power policy and fixed GPU rejected");
    evt.AdapterId="";
    evt.WindowsGpuPolicy=0;
    Assert(NvGraphics.Contracts.GpuPreferencePayloadValidator.TryValidate(
        AsJson(evt),DateTimeOffset.UtcNow,out _,out _,out _),
        "Windows default choice event accepted");
    Assert(pref.ApplyWindowsPolicy("Example.Package_123!App",1).Success,
        "packaged Microsoft Store AppUserModelID can be assigned a Windows policy");

    var sw=System.Diagnostics.Stopwatch.StartNew();
    var slow=new GpuTelemetryService("powershell.exe",
        "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 8\"");
    var noSample=slow.Sample(TimeSpan.FromMilliseconds(250));
    sw.Stop();
    Assert(noSample is null && !slow.LastAttemptSucceeded && sw.Elapsed<TimeSpan.FromSeconds(5),
        "hung telemetry command times out without freezing caller indefinitely");
    var outputCommand="-NoProfile -NonInteractive -Command "+
        "\"Write-Output 'FakeGPU, 00000000:01:00.0, 15, 10, 100'\"";
    var cachedOne=new GpuTelemetryService("powershell.exe",outputCommand);
    var cachedTwo=new GpuTelemetryService("powershell.exe",outputCommand);
    var first=cachedOne.Sample();
    var second=cachedTwo.Sample();
    Assert(first is not null && ReferenceEquals(first,second) &&
        cachedTwo.LastAttemptSucceeded && second.Samples.Count==1,
        "two GPU Manager callers share the recent 3-second telemetry cache");
}
finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
if (args.Contains("--live-registry-smoke", StringComparer.OrdinalIgnoreCase))
{
    // This is an opt-in Windows integration test. Its GUID-named executable
    // doesn't exist, so no installed game or profile can be affected.
    var realPath = Path.Combine(Path.GetTempPath(),
        "NvGraphics-WindowsGpuTest-" + Guid.NewGuid().ToString("N") + ".exe");
    var realBackupDir = Path.Combine(Path.GetTempPath(),
        "NvGraphics-LiveBackup-" + Guid.NewGuid().ToString("N"));
    var windows = new HkcuRegistryApi();
    var realService = new WindowsGpuPreferenceService(windows,
        new RegistryBackupService(realBackupDir));
    try
    {
        var before = windows.GetValue(key, realPath);
        Assert(before.Value is null && before.Kind is null,
            "live HKCU GPU test name is unused");
        if (before.Value is not null || before.Kind is not null)
            throw new IOException("GUID scratch registry entry already existed.");
        Assert(realService.ApplyWindowsPolicy(realPath, 2).Success,
            "live Windows high-performance GPU preference write and readback");
        Assert(WindowsGpuPreferenceService.ParseWindowsGpuPolicy(
            realService.FindValue(realPath)) == 2,
            "live Windows registry returns high performance");
        Assert(realService.ApplyPreference(realPath,
            "VEN_10DE&DEV_1B06&SUBSYS_85F11043").Success,
            "live Windows registry accepts a specific physical GPU selection");
        Assert(realService.FindValue(realPath).Contains(
            "SpecificAdapter=10DE&1B06&85F11043;"),
            "live Windows specific-GPU selection readback matches");
        Assert(realService.ResetGpuSelection(realPath).Success &&
            realService.FindValue(realPath) is null,
            "live Windows GPU preference reset deletes only the scratch entry");
    }
    catch (Exception e)
    {
        Assert(false, "live registry smoke failed: " + e.Message);
    }
    finally
    {
        // A failing or interrupted test must never leave a GPU preference behind.
        try { windows.DeleteValue(key, realPath); }
        catch (Exception cleanupError)
        {
            Assert(false, "scratch HKCU cleanup failed: " + cleanupError.Message);
        }
        try
        {
            Assert(windows.GetValue(key, realPath).Value is null,
                "live registry scratch entry removed after test");
        }
        catch (Exception e) { Assert(false, "cleanup verification failed: " + e.Message); }
        try { if (Directory.Exists(realBackupDir)) Directory.Delete(realBackupDir, true); }
        catch (Exception e) { Assert(false, "temporary backup cleanup failed: " + e.Message); }
    }
}
Console.WriteLine("RESULT "+(tested-errors.Count)+"/"+tested+" passing");
if(errors.Count>0) { Console.Error.WriteLine(string.Join(" | ",errors)); Environment.ExitCode=1; }

sealed class Counter : IEngineCounterProvider
{
    private readonly EngineSample[][] sets; private int index;
    public string SourceName=>"fake";
    public Counter(EngineSample[][] s)=>sets=s;
    public bool TryCollect(List<EngineSample> target,out string error)
    { error=null; if(index>=sets.Length){error="exhausted";return false;}target.AddRange(sets[index++]);return true; }
}
sealed class AdapterResolver : ILuidAdapterResolver
{
    private readonly IReadOnlyDictionary<string,GpuAdapterInfo> lookup;
    public AdapterResolver(IReadOnlyDictionary<string,GpuAdapterInfo> l)=>lookup=l;
    public GpuAdapterInfo Resolve(string l)=>lookup.GetValueOrDefault(l);
}
sealed class PathResolver : IProcessPathResolver
{
    private readonly int id; private readonly string path;
    public PathResolver(int i,string p){id=i;path=p;}
    public string TryGetPath(int pid)=>pid==id?path:null;
}
