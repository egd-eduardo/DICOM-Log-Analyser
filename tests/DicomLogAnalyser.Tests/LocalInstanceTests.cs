using DicomLogAnalyser.Web.Platform;

namespace DicomLogAnalyser.Tests;

/// <summary>A second start by the same user hands over to the running instance through a current-user-only pipe.</summary>
public sealed class LocalInstanceTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Second_start_asks_the_running_instance_to_open_the_browser()
    {
        var a = _temp.Sub("a");
        var b = _temp.Sub("b");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var listening = LocalInstance.ListenAsync(a, () => opened.TrySetResult(), cts.Token);

        var answered = false;
        for (var i = 0; i < 50 && !answered; i++)
        {
            answered = LocalInstance.TryOpenRunning(a, TimeSpan.FromMilliseconds(200));
            if (!answered) await Task.Delay(50);
        }

        Assert.True(answered);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Another data folder is another instance: nobody answers.
        Assert.False(LocalInstance.TryOpenRunning(b, TimeSpan.FromMilliseconds(300)));
        await cts.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pipe_name_is_stable_per_folder_and_reveals_no_path()
    {
        var a = _temp.Sub("a");
        Assert.Equal(LocalInstance.PipeName(a), LocalInstance.PipeName(a + Path.DirectorySeparatorChar));
        Assert.NotEqual(LocalInstance.PipeName(a), LocalInstance.PipeName(_temp.Sub("b")));
        Assert.Matches("^dla-[0-9a-f]{16}$", LocalInstance.PipeName(a));
    }
}
