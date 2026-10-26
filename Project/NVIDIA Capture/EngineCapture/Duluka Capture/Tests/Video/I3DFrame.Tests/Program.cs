// SPN-I3D-003 / Agent B: deterministic device-free transaction/resource tests.
// NO calls to real DdagrabBackend Initialize/Start/AcquireNextFrame/DuplicateOutput.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DulukaEngine.Video;
using DulukaEngine.Video.Backends.Ddagrab;

static class Program
{
    static readonly Assembly Native = typeof(D3D11VideoFrame).Assembly;
    static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static int passed, failed;

    static int Main()
    {
        Console.WriteLine("SPN-I3D-003 B / FAKE FRAMES ONLY / NO LIVE CAPTURE");
        Run("Acquire no frame never releases/pushes", NoAcquire);
        foreach (var fault in new [] { "QI", "timestamp", "construct", "cursor" })
            Run("Successful acquire then " + fault + " fault releases once", () => BuildFault(fault));
        Run("Release occurs before sink and owned frame survives transfer", PushSuccess);
        Run("Replaced transfers once; sink owns frame", Replaced);
        Run("Dropped disposes producer-owned frame", Dropped);
        Run("Throwing sink disposes producer-owned frame", SinkThrows);
        Run("Unknown outcome disposes producer-owned frame", UnknownOutcome);
        Run("Release failure still disposes frame; sink not called", ReleaseThrows);
        Run("Null callbacks are rejected before acquire", NullGuards);
        Run("Builder direct route transfers single texture", BuilderDirect);
        Run("Builder shared route transfers texture plus HANDLE", BuilderShared);
        foreach (var fault in new [] { "staging", "prepare", "shared", "copy", "handle", "zero-handle", "construct", "null-frame" })
            Run("Builder " + fault + " fault cleans all owned resources", () => BuilderFault(fault));
        Run("Frame.Dispose concurrently closes NT handle exactly once", FrameDisposeRace);
        Run("Frame.Dispose after fake texture throws still closes HANDLE", FrameDisposeTextureThrows);
        Run("Replacement success clears old and transfers replacement", DuplicationSuccess);
        Run("Replacement failure clears old and closes temp scope", DuplicationFailure);
        Run("Replacement scope Dispose throw cleans candidate", DuplicationScopeThrows);
        Console.WriteLine($"B FRAME SUMMARY Passed={passed} Failed={failed} Skipped=0 Total={passed + failed}");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action action)
    {
        try { action(); ++passed; Console.WriteLine("PASS " + name); }
        catch (Exception e) { ++failed; Console.WriteLine("FAIL " + name + " -> " + e); }
    }

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    static object? Call(string type, string method, Type[]? typeArgs, params object?[] args)
    {
        var t = Native.GetType("DulukaEngine.Video.Backends.Ddagrab." + type, true)!;
        var m = t.GetMethod(method, Flags)!;
        if (typeArgs != null) m = m.MakeGenericMethod(typeArgs);
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    static bool Transaction(Func<bool> acquire, Func<IVideoFrame> build, Action release,
                            Func<IVideoFrame, PushOutcome> push) =>
        (bool)Call("FrameTransaction", "Execute", null, acquire, build, release, push)!;

    sealed class FakeTexture : IDisposable
    {
        public readonly string Name;
        public int DisposeCalls;
        public bool ThrowOnDispose;
        public FakeTexture(string name, bool throwOnDispose = false) { Name = name; ThrowOnDispose = throwOnDispose; }
        public void Dispose() { Interlocked.Increment(ref DisposeCalls); if (ThrowOnDispose) throw new Exception("fake_dispose_fault"); }
    }

    sealed class FakeFrame : IVideoFrame
    {
        public int Disposals;
        public FakeTexture? Texture;
        public IntPtr Handle;
        public Action<IntPtr>? Closer;
        public Action? Released;
        public VideoFrameOrigin Origin => VideoFrameOrigin.GpuD3D11Texture;
        public VideoPixelFormat PixelFormat => VideoPixelFormat.Bgra8;
        public VideoFrameDimensions Dimensions => new(16, 16);
        public FrameDiagnostics Diagnostics => new(1, 100, 100);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref Disposals, 1) != 0) return;
            try { Texture?.Dispose(); }
            finally
            {
                try { if (Handle != IntPtr.Zero) Closer?.Invoke(Handle); }
                finally { Released?.Invoke(); }
            }
        }
    }

    static void NoAcquire()
    {
        int build = 0, released = 0, pushed = 0;
        Check(!Transaction(() => false, () => { ++build; return new FakeFrame(); },
            () => ++released, _ => { ++pushed; return PushOutcome.Pushed; }), "false acquire returned true");
        Check(build == 0 && released == 0 && pushed == 0, "side effects on no acquire");
    }

    static void BuildFault(string stage)
    {
        int released = 0, pushed = 0;
        try
        {
            Transaction(() => true, () => throw new Exception("fake_" + stage + "_failed"),
                () => ++released, _ => { ++pushed; return PushOutcome.Pushed; });
            throw new Exception("missing expected fault");
        }
        catch (Exception e) when (e.Message == "fake_" + stage + "_failed") { }
        Check(released == 1 && pushed == 0, stage + ": must ReleaseFrame once before any callback");
    }

    static void PushSuccess()
    {
        var frame = new FakeFrame(); int released = 0, sink = 0; FakeFrame? sinkOwned = null;
        Check(Transaction(() => true, () => frame, () => ++released, f =>
        {
            ++sink; Check(released == 1 && frame.Disposals == 0, "sink before release or premature frame Dispose");
            sinkOwned = (FakeFrame)f; return PushOutcome.Pushed;
        }), "successful acquired frame expected");
        Check(sink == 1 && frame.Disposals == 0 && ReferenceEquals(frame, sinkOwned), "handoff failed");
        sinkOwned!.Dispose(); Check(frame.Disposals == 1, "sink frame cleanup must be once");
    }

    static void Replaced()
    {
        var frame = new FakeFrame(); int released = 0;
        Transaction(() => true, () => frame, () => ++released, f => { Check(released == 1, "released after Replace"); return PushOutcome.Replaced; });
        Check(frame.Disposals == 0, "producer reclaimed Replaced frame");
        frame.Dispose(); Check(frame.Disposals == 1, "sink frame disposal missing");
    }

    static void Dropped()
    {
        var frame = new FakeFrame(); int released = 0;
        Transaction(() => true, () => frame, () => ++released, f => { Check(released == 1, "late release"); return PushOutcome.Dropped; });
        Check(frame.Disposals == 1, "producer did not Dispose dropped frame");
    }

    static void SinkThrows()
    {
        var frame = new FakeFrame(); int released = 0;
        try { Transaction(() => true, () => frame, () => ++released, _ => throw new Exception("fake_sink_throw")); Check(false, "expected sink fault"); }
        catch (Exception e) when (e.Message == "fake_sink_throw") { }
        Check(released == 1 && frame.Disposals == 1, "sink fault ownership broken");
    }

    static void UnknownOutcome()
    {
        var frame = new FakeFrame(); int released = 0;
        try { Transaction(() => true, () => frame, () => ++released, _ => (PushOutcome)12345); Check(false, "unknown outcome accepted"); }
        catch (Exception e) when (e.Message == "source_unknown_push_outcome") { }
        Check(released == 1 && frame.Disposals == 1, "unknown outcome should release owned frame");
    }

    static void ReleaseThrows()
    {
        var frame = new FakeFrame(); int released = 0, pushed = 0;
        try { Transaction(() => true, () => frame, () => { ++released; throw new Exception("fake_release_error"); },
            _ => { ++pushed; return PushOutcome.Pushed; }); Check(false, "release exception swallowed"); }
        catch (Exception e) when (e.Message == "fake_release_error") { }
        Check(released == 1 && pushed == 0 && frame.Disposals == 1, "release fault must not push, must dispose frame");
    }

    static void NullGuards()
    {
        int calls = 0;
        try
        {
            Call("FrameTransaction", "Execute", null, null,
                (Func<IVideoFrame>)(() => { ++calls; return new FakeFrame(); }),
                (Action)(() => ++calls), (Func<IVideoFrame, PushOutcome>)(_ => PushOutcome.Pushed));
            Check(false, "null acquire accepted");
        }
        catch (ArgumentNullException) { }
        Check(calls == 0, "null callbacks should fail before side effects");
    }

    sealed class BuilderRun
    {
        public readonly List<FakeTexture> Textures = new();
        public int Closed, Released;
        public FakeFrame? Built;
        public string Failure = "";
        public IVideoFrame? Run(bool shared)
        {
            return (IVideoFrame?)Call("OwnedFrameBuilder", "Build", new[] { typeof(FakeTexture) },
                (Func<bool, FakeTexture>)(isShared =>
                {
                    if ((isShared && Failure == "shared") || (!isShared && Failure == "staging"))
                        throw new Exception("fake_" + Failure);
                    var item = new FakeTexture(isShared ? "shared" : "staging");
                    Textures.Add(item); return item;
                }),
                (Action<FakeTexture>)(_ => { if (Failure == "prepare") throw new Exception("fake_prepare"); }),
                (Action<FakeTexture, FakeTexture>)((_, _) => { if (Failure == "copy") throw new Exception("fake_copy"); }),
                shared,
                (Func<FakeTexture, IntPtr>)(_ =>
                {
                    if (Failure == "handle") throw new Exception("fake_handle");
                    return Failure == "zero-handle" ? IntPtr.Zero : new IntPtr(4242);
                }),
                (Action<IntPtr>)(_ => ++Closed),
                (Func<FakeTexture, IntPtr, IVideoFrame>)((texture, handle) =>
                {
                    if (Failure == "construct") throw new Exception("fake_construct");
                    if (Failure == "null-frame") return null!;
                    Built = new FakeFrame { Texture = texture, Handle = handle, Closer = _ => ++Closed, Released = () => ++Released };
                    return Built;
                }),
                (Action)(() => ++Released));
        }
        public void CheckTextures(int disposalPerTexture = 1)
        {
            foreach (var t in Textures) Check(t.DisposeCalls == disposalPerTexture,
                $"{t.Name} released {t.DisposeCalls}, expected {disposalPerTexture}");
        }
    }

    static void BuilderDirect()
    {
        var b = new BuilderRun(); var frame = b.Run(false);
        Check(frame is not null && b.Textures.Count == 1, "direct route did not create one texture");
        Check(b.Textures[0].DisposeCalls == 0 && b.Closed == 0, "direct texture freed before transfer");
        frame!.Dispose(); b.CheckTextures(); Check(b.Closed == 0 && b.Released == 1, "direct ownership release error");
    }

    static void BuilderShared()
    {
        var b = new BuilderRun(); var frame = b.Run(true);
        Check(frame is not null && b.Textures.Count == 2, "shared fixture needs staging + shared");
        Check(b.Textures[0].DisposeCalls == 1 && b.Textures[1].DisposeCalls == 0 && b.Closed == 0,
            "shared ownership wrong before transfer");
        frame!.Dispose(); b.CheckTextures(); Check(b.Closed == 1 && b.Released == 2, "shared transfer/handle close not exactly once");
    }

    static void BuilderFault(string fault)
    {
        var b = new BuilderRun { Failure = fault };
        try { b.Run(true); Check(false, "fault " + fault + " not injected"); }
        catch (Exception e) when (e.Message == "fake_" + fault ||
                                  e.Message == "source_shared_handle_missing" ||
                                  e.Message == "source_frame_construction_returned_null") { }
        b.CheckTextures();
        int expectedClose = fault is "construct" or "null-frame" ? 1 : 0;
        Check(b.Closed == expectedClose, fault + ": handle closure count incorrect " + b.Closed);
        Check(b.Released == b.Textures.Count, fault + ": texture release tracking incorrect");
    }

    static D3D11VideoFrame NewFakeNativeFrame(FakeTexture texture, IntPtr handle, Action<IntPtr> close)
    {
        var ctor = typeof(D3D11VideoFrame).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        var selected = Array.Find(ctor, c => c.GetParameters().Length == 10 &&
            c.GetParameters()[0].ParameterType == typeof(IDisposable));
        Check(selected is not null, "B4 device-free Friend constructor unavailable");
        return (D3D11VideoFrame)selected!.Invoke(new object?[] { texture, 16, 16, 7L, 100L, 100L, handle, null, 9L, close });
    }

    static void FrameDisposeRace()
    {
        var texture = new FakeTexture("native-frame"); int closed = 0, callback = 0;
        var frame = NewFakeNativeFrame(texture, new IntPtr(4242), _ => Interlocked.Increment(ref closed));
        frame.OnDisposed = () => Interlocked.Increment(ref callback);
        Check(frame.SharedHandle == new IntPtr(4242) && ReferenceEquals(frame.NativeTexture, texture), "test-owned frame missing resource");
        Parallel.For(0, 24, _ => frame.Dispose());
        frame.Dispose();
        Check(frame.DisposeCount == 1 && texture.DisposeCalls == 1 && closed == 1 && callback == 1,
            $"native frame double dispose texture={texture.DisposeCalls} handle={closed} callback={callback}");
        Check(frame.SharedHandle == IntPtr.Zero && frame.NativeTexture is null, "disposed frame exposes handle/texture");
        try { frame.OnDisposed = () => { }; Check(false, "callback setter accepted disposed frame"); }
        catch (ObjectDisposedException) { }
    }

    static void FrameDisposeTextureThrows()
    {
        var texture = new FakeTexture("throw", true); int closed = 0, callback = 0;
        var frame = NewFakeNativeFrame(texture, new IntPtr(4243), _ => Interlocked.Increment(ref closed));
        frame.OnDisposed = () => Interlocked.Increment(ref callback);
        frame.Dispose(); frame.Dispose();
        Check(texture.DisposeCalls == 1 && closed == 1 && callback == 1, "texture throw skipped handle or repeated release");
    }

    sealed class FakeScope : IDisposable
    {
        public int Disposals;
        public bool ThrowWhenDisposed;
        public void Dispose()
        {
            ++Disposals;
            if (ThrowWhenDisposed) throw new Exception("fake_scope_dispose");
        }
    }

    static void Replace(ref FakeTexture? current, Func<FakeScope> scope,
                        Func<FakeScope, FakeTexture> create)
    {
        // Direct CLR ByRef: reflection does not always copy back a changed
        // ByRef argument when the invoked target exits by throwing.
        DuplicationReplacement.Replace(ref current, scope, create);
    }

    static void DuplicationSuccess()
    {
        FakeTexture? old = new FakeTexture("old"), next = new FakeTexture("next");
        var scope = new FakeScope(); var previous = old;
        Replace(ref old, () => scope, _ => next);
        Check(previous.DisposeCalls == 1 && scope.Disposals == 1 && ReferenceEquals(old, next) &&
              next.DisposeCalls == 0, "replacement transfer incorrect");
        old!.Dispose(); Check(next.DisposeCalls == 1, "replacement not caller-owned");
    }

    static void DuplicationFailure()
    {
        FakeTexture? old = new FakeTexture("old"); var previous = old;
        var scope = new FakeScope();
        try { Replace(ref old, () => scope, _ => throw new Exception("fake_create_duplication")); Check(false, "failure lost"); }
        catch (Exception e) when (e.Message == "fake_create_duplication") { }
        Check(old is null && previous.DisposeCalls == 1 && scope.Disposals == 1, "failed duplication left stale wrapper or scope");
    }

    static void DuplicationScopeThrows()
    {
        FakeTexture? old = new FakeTexture("old"); var previous = old;
        var scope = new FakeScope { ThrowWhenDisposed = true };
        var candidate = new FakeTexture("candidate");
        try { Replace(ref old, () => scope, _ => candidate); Check(false, "scope Dispose fault lost"); }
        catch (Exception e) when (e.Message == "fake_scope_dispose") { }
        Check(old is null && previous.DisposeCalls == 1 && candidate.DisposeCalls == 1 && scope.Disposals == 1,
            "temporary candidate was not released on scope Dispose failure");
    }
}
