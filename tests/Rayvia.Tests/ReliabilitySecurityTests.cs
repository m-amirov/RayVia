using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class ReliabilitySecurityTests
{
    [Fact]
    public void SingleInstanceAllowsOnlyOneOwner()
    {
        using var first = new SingleInstanceService();
        using var second = new SingleInstanceService();
        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void ElevationHandoffArgumentsRoundTripAndRejectInvalidInput()
    {
        var handoff = ElevationHandoff.Create(Environment.ProcessId);
        var arguments = handoff.ToArguments(autoConnect: true);

        Assert.True(ElevationHandoff.TryParse(arguments, out var parsed));
        Assert.Equal(handoff, parsed);
        Assert.Contains("--autoconnect", arguments);
        Assert.False(ElevationHandoff.TryParse(["--elevation-handoff=not-a-guid", "--elevation-parent-pid=1"], out _));
    }

    [Fact]
    public async Task ElevationHandoffDoesNotTakeOverBeforeParentExits()
    {
        var handoff = ElevationHandoff.Create(Environment.ProcessId);
        using var signal = SingleInstanceService.CreateHandoffSignal(handoff);
        signal.Set();
        Assert.False(await SingleInstanceService.WaitForParentExitAsync(handoff, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task ElevationHandoffReservesOwnershipBeforeParentExitAndBlocksThirdInstance()
    {
        using var parent = new SingleInstanceService();
        using var child = new SingleInstanceService();
        using var third = new SingleInstanceService();
        var handoff = ElevationHandoff.Create(Environment.ProcessId);

        Assert.True(parent.TryAcquire());
        Assert.True(parent.BeginElevationHandoff(handoff));

        var takeover = child.TryAcquireForHandoffAsync(handoff, TimeSpan.FromSeconds(1));
        Assert.False(third.TryAcquire());
        Assert.True(parent.SignalElevationHandoff());
        Assert.True(await takeover);
        Assert.True(parent.WaitForElevationTakeover(TimeSpan.FromSeconds(1)));
        Assert.True(child.OwnsInstance);
        Assert.False(third.TryAcquire());

        Assert.False(await SingleInstanceService.WaitForParentExitAsync(handoff, TimeSpan.FromMilliseconds(50)));
        parent.CompleteElevationHandoff();
        Assert.False(third.TryAcquire());
    }

    [Fact]
    public async Task ChildHandoffOwnershipCanBeDisposedAndReacquired()
    {
        using var parent = new SingleInstanceService();
        using var child = new SingleInstanceService();
        var handoff = ElevationHandoff.Create(Environment.ProcessId);

        Assert.True(parent.TryAcquire());
        Assert.True(parent.BeginElevationHandoff(handoff));
        var takeover = child.TryAcquireForHandoffAsync(handoff, TimeSpan.FromSeconds(1));
        Assert.True(parent.SignalElevationHandoff());
        Assert.True(await takeover);
        Assert.True(parent.WaitForElevationTakeover(TimeSpan.FromSeconds(1)));
        parent.CompleteElevationHandoff();

        DisposeOnDedicatedThread(child);
        using var replacement = new SingleInstanceService();
        Assert.True(replacement.TryAcquire());
    }

    [Fact]
    public async Task RepeatedHandoffAfterTakeoverDoesNotLeaveHiddenOwnership()
    {
        using var firstParent = new SingleInstanceService();
        using var firstChild = new SingleInstanceService();
        var firstHandoff = ElevationHandoff.Create(Environment.ProcessId);

        Assert.True(firstParent.TryAcquire());
        Assert.True(firstParent.BeginElevationHandoff(firstHandoff));
        var firstTakeover = firstChild.TryAcquireForHandoffAsync(firstHandoff, TimeSpan.FromSeconds(1));
        Assert.True(firstParent.SignalElevationHandoff());
        Assert.True(await firstTakeover);
        Assert.True(firstParent.WaitForElevationTakeover(TimeSpan.FromSeconds(1)));
        firstParent.CompleteElevationHandoff();
        DisposeOnDedicatedThread(firstChild);

        using var secondParent = new SingleInstanceService();
        using var secondChild = new SingleInstanceService();
        var secondHandoff = ElevationHandoff.Create(Environment.ProcessId);

        Assert.True(secondParent.TryAcquire());
        Assert.True(secondParent.BeginElevationHandoff(secondHandoff));
        var secondTakeover = secondChild.TryAcquireForHandoffAsync(secondHandoff, TimeSpan.FromSeconds(1));
        Assert.True(secondParent.SignalElevationHandoff());
        Assert.True(await secondTakeover);
        Assert.True(secondParent.WaitForElevationTakeover(TimeSpan.FromSeconds(1)));
        secondParent.CompleteElevationHandoff();
        DisposeOnDedicatedThread(secondChild);

        using var finalOwner = new SingleInstanceService();
        Assert.True(finalOwner.TryAcquire());
    }

    [Fact]
    public void AbortedElevationHandoffLeavesParentOwner()
    {
        using var parent = new SingleInstanceService();
        using var third = new SingleInstanceService();
        var handoff = ElevationHandoff.Create(Environment.ProcessId);

        Assert.True(parent.TryAcquire());
        Assert.True(parent.BeginElevationHandoff(handoff));
        Assert.True(parent.AbortElevationHandoff());
        Assert.True(parent.OwnsInstance);
        Assert.False(third.TryAcquire());
    }

    [Fact]
    public void RuntimeStateRoundTripsWithoutUserFiles()
    {
        using var temp = new TemporaryDirectory();
        var service = new RuntimeStateService(Path.Combine(temp.Path, "runtime-state.json"));
        service.Save(new RuntimeState { RayviaPid = 42, XrayPid = 43, OwnsSystemProxy = true, SessionId = "session" });
        var loaded = service.Load();
        Assert.Equal(42, loaded?.RayviaPid);
        Assert.Equal(43, loaded?.XrayPid);
        service.Delete();
        Assert.Null(service.Load());
    }

    private static void DisposeOnDedicatedThread(SingleInstanceService service)
        => Task.Factory.StartNew(service.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .GetAwaiter()
            .GetResult();

    [Fact]
    public void MalformedOrMissingUpdateChecksumIsRejected()
    {
        Assert.Null(UpdateService.ParseExpectedHash("deadbeef  other.exe"));
        Assert.Null(UpdateService.ParseExpectedHash("not-a-hash  Rayvia-Setup-x64.exe"));
        Assert.Null(UpdateService.ParseExpectedHash(""));
    }

    [Fact]
    public void UpdateHashHelperRejectsTamperedFile()
    {
        using var file = new TemporaryFile(); File.WriteAllText(file.Path, "installer");
        Assert.False(UpdateService.VerifySha256(file.Path, new string('0', 64)));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rayvia-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
