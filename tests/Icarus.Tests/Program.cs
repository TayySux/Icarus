using Icarus.Core;
using Icarus.Tests;

int passed = 0;
void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
async Task Test(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); passed++; }
await Test("Frame hold targets", () => { Assert(Timing.MinimumHold(60,25)==25,"60 FPS"); Assert(Timing.MinimumHold(144,25)==11,"144 FPS"); Assert(Timing.MinimumHold(240,25)==7,"240 FPS"); Assert(Timing.MinimumHold(null,25)==25,"unavailable FPS"); return Task.CompletedTask; });
await Test("Profile share code round trip", () => { var p = Sample(); var s = ProfileCodec.Export(p); Assert(ProfileCodec.Import(s).Steps[0].ScanCode==30,"round trip"); Assert(ProfileCodec.ToJson(p).Contains("scan_code"),"snake case"); return Task.CompletedTask; });
await Test("Ordered timeline and minimum holds at all frame rates", async () =>
{
    foreach (double fps in new[] { 60d, 144d, 240d })
    {
        var events = new List<string>(); var engine = new MacroEngine(new Output(events), new Clock(events));
        await engine.RunAsync(Sample() with { MeasuredFps = fps }, () => true, default);
        int h = Timing.MinimumHold(fps,25); int gap = Math.Max(h,10);
        Assert(events.SequenceEqual(new[] { "down:30", $"wait:{h}", "up:30", $"wait:{gap}" }), string.Join(",",events));
        Assert(engine.HeldCount==0 && engine.CompletedRuns==1,"completion state");
    }
});
await Test("Cancellation releases owned input", async () =>
{
    var events = new List<string>(); using var c = new CancellationTokenSource();
    var engine = new MacroEngine(new Output(events), new CancelClock(c));
    try { await engine.RunAsync(Sample(), () => true, c.Token); throw new Exception("Cancellation missing"); }
    catch (OperationCanceledException) { }
    Assert(engine.HeldCount==0,"held leak"); Assert(events.SequenceEqual(new[]{"down:30","up:30"}),"release timeline");
    Assert(engine.CompletedRuns==0,"canceled run counted");
});
await Test("Focus mismatch sends nothing", async () =>
{
    var events=new List<string>(); var engine=new MacroEngine(new Output(events),new Clock(events));
    try { await engine.RunAsync(Sample(),()=>false,default); throw new Exception("Focus guard missing"); } catch(OperationCanceledException) { }
    Assert(events.Count==0,"focus leak");
});
await Test("Duplicate down fails and releases", async () =>
{
    var events = new List<string>(); var engine = new MacroEngine(new Output(events),new Clock(events));
    var p=Sample() with { Steps=[new(){Kind=StepKind.KeyDown,ScanCode=30},new(){Kind=StepKind.KeyDown,ScanCode=30}] };
    try { await engine.RunAsync(p,()=>true,default); throw new Exception("duplicate permitted"); } catch(InvalidOperationException) { }
    Assert(engine.HeldCount==0,"duplicate leak"); Assert(events.Count(x=>x=="down:30")==1,"duplicate emitted");
});
await Test("Release failure remains tracked and blocks completion", async () =>
{
    var engine=new MacroEngine(new FailingOutput(),new Clock([]));
    try { await engine.RunAsync(Sample(),()=>true,default); throw new Exception("release failure ignored"); } catch(AggregateException) { }
    Assert(engine.HeldCount==1,"lost ownership"); Assert(engine.CompletedRuns==0,"failed run counted");
});
await Test("Radial deadzone and saturation", () =>
{
    Assert(StickMath.Transform(0.02,0.03,0.1,1,1,0)==(0d,0d),"drift not removed");
    var p=StickMath.Transform(1,1,0.1,1,1,0); Assert(Math.Abs(p.X*p.X+p.Y*p.Y-1)<1e-10,"saturation");
    return Task.CompletedTask;
});
await Test("Legacy trigger conditions fail explicitly", () =>
{
    string json=ProfileCodec.ToJson(Sample()).TrimEnd(); json=json[..^1]+",\"conditions\": []}";
    try { ProfileCodec.FromJson(json); throw new Exception("unsupported semantics accepted"); } catch(System.IO.InvalidDataException) { }
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} test groups passed.");

// Phase A measurement layer.
foreach (var (name, run) in LatencyTests.All().Concat(InputLatencyTests.All())
    .Concat(ProbeTests.All()).Concat(LanguageGuardTests.All()).Concat(WatchdogTests.All()))
    await Test(name, run);

Console.WriteLine($"{(passed)} test groups passed total.");
static MacroProfile Sample() => new() { Name="Test", Steps=[new(){Kind=StepKind.KeyPress,ScanCode=30,DurationMs=1}] };
sealed class Output(List<string> events) : IInputOutput
{
    public void Down(InputToken i)=>events.Add($"down:{i.Code}");
    public void Up(InputToken i)=>events.Add($"up:{i.Code}");
    public void Move(int x,int y)=>events.Add($"move:{x},{y}");
}
sealed class Clock(List<string> events) : IClock { public Task WaitAsync(int ms,CancellationToken t) { t.ThrowIfCancellationRequested(); events.Add($"wait:{ms}"); return Task.CompletedTask; } }
sealed class CancelClock(CancellationTokenSource source) : IClock { public Task WaitAsync(int ms,CancellationToken t) { source.Cancel(); t.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
sealed class FailingOutput : IInputOutput { public void Down(InputToken i) { } public void Up(InputToken i)=>throw new IOException("Test release failure"); public void Move(int x,int y) { } }
