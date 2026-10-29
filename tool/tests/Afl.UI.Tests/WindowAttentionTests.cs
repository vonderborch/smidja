using Afl.Core.Settings;
using Afl.UI.Attention;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Afl.UI.Tests;

/// <summary>
///     Tests for drawing the user's eye to afl's window when a run needs them or finishes. They use a recording
///     attention, so no real taskbar flashes or Dock bounces.
/// </summary>
public class WindowAttentionTests : AbstractUiTest
{
    private readonly RecordingAttention _attention = new();

    private WindowRunNotifier Notifier(AbstractWindowAttention? attention) => new(null, new SettingsStore(this.Paths), attention);

    [AvaloniaFact]
    public void AWindowThatIsNotActive_IsAskedForAttentionUntilTheUserComesBack()
    {
        var notifier = this.Notifier(this._attention);
        var window = new Window();

        Assert.True(notifier.RequestAttention(window, true));
        Assert.True(notifier.RequestAttention(window, false));
        Assert.Equal([true, false], this._attention.Requests);

        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsActive);
        Assert.Equal(1, this._attention.Clears);
        Assert.False(notifier.RequestAttention(window, true));
        Assert.Equal(2, this._attention.Requests.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void WithoutAnAttentionForThisSystem_NothingIsAsked()
    {
        Assert.False(this.Notifier(null).RequestAttention(new Window(), true));
    }

    [Fact]
    public void EachSystem_HasOneAttention()
    {
        var registry = WindowAttentionRegistry.Instance;

        Assert.Equal(["mac-dock", "windows-flash", "x11-urgency"], registry.Keys.Order());
        Assert.Single(registry.CreateAll(), a => a.IsForThisSystem);
    }

    /// <summary>
    ///     An attention that keeps what it is asked for.
    /// </summary>
    private sealed class RecordingAttention() : AbstractWindowAttention("recording")
    {
        /// <summary>
        ///     Gets whether each request was urgent.
        /// </summary>
        public List<bool> Requests { get; } = [];

        /// <summary>
        ///     Gets how many times attention was cleared.
        /// </summary>
        public int Clears { get; private set; }

        /// <inheritdoc />
        public override bool IsForThisSystem => false;

        /// <inheritdoc />
        public override void Request(Window window, bool urgent)
        {
            this.Requests.Add(urgent);
        }

        /// <inheritdoc />
        public override void Clear(Window window)
        {
            this.Clears++;
        }
    }
}
