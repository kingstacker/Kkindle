using Kkindle;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class TtsTests
{
    [Fact]
    public void TextSnapshotMapsTtsOffsetsBackToBodyText()
    {
        var snapshot = new ReaderTtsTextSnapshot(
            "甲乙\n丙",
            2,
            [10, 11, 24, 25]);

        Assert.Equal(2, snapshot.StartOffset);
        Assert.Equal((10, 2), snapshot.MapToSource(0, 2));
        Assert.Equal((24, 2), snapshot.MapToSource(2, 2));
        Assert.Equal(2, snapshot.GetTextOffsetAtOrAfterSource(12));
    }

    [Fact]
    public void SegmenterRemovesMarkupAndPreservesNaturalChunks()
    {
        var source =
            "<p>"
            + new string('甲', 150)
            + "。</p><p>"
            + new string('乙', 150)
            + "！最后一句</p>";

        var segments = TtsTextSegmenter.Split(source, maxCharacters: 200);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new string('甲', 150) + "。", segments[0].Text);
        Assert.StartsWith(new string('乙', 150) + "！", segments[1].Text);
        Assert.DoesNotContain("<p>", string.Concat(segments.Select(item => item.Text)));
    }

    [Fact]
    public void SegmenterKeepsEntitiesAsReadableText()
    {
        var prepared = TtsTextSegmenter.Prepare(
            "<p>引号：“你好” &amp; 世界。</p>");

        Assert.Equal("引号：“你好” & 世界。\n", prepared.Text);
        Assert.Equal((3, 1), prepared.MapToSource(0, 1));
    }

    [Fact]
    public void SegmenterDoesNotSplitAConfiguredAsciiWord()
    {
        const string source =
            "这是一个很长的句子，里面有一个 edge-tts-command-name 需要保持完整，然后结束。";

        var segments = TtsTextSegmenter.Split(source, maxCharacters: 120);

        Assert.Equal(
            source,
            string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Text.Contains(
                "edge-tts-command-name",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SentenceSegmenterKeepsOneSpokenSentencePerSegment()
    {
        const string source = "第一句内容。第二句内容！“第三句？”\n第四句。";

        var segments = TtsTextSegmenter.SplitSentences(source, maxCharacters: 120);

        Assert.Equal(
            ["第一句内容。", "第二句内容！", "“第三句？”", "第四句。"],
            segments.Select(segment => segment.Text).ToArray());
    }

    [Fact]
    public void SettingsConvertToEdgeArgumentsAndNormalize()
    {
        var settings = TtsSettings.Normalize(new TtsSettings
        {
            Provider = "microsoft",
            Voice = " ",
            Speed = double.NaN,
            Volume = 999,
            Pitch = -999,
        });

        var options = settings.ToOptions();

        Assert.Equal(TtsSettings.DefaultProvider, settings.Provider);
        Assert.Equal(TtsOptions.DefaultVoice, settings.Voice);
        Assert.Equal("+0%", options.RateArgument);
        Assert.Equal("+100%", options.VolumeArgument);
        Assert.Equal("-100Hz", options.PitchArgument);
    }

    [Fact]
    public async Task CacheKeyChangesWhenVoiceOrAudioOptionsChange()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cache = new TtsCacheManager(root);
            var first = cache.GetCachePath(
                "book",
                "chapter-1",
                "相同文字",
                new TtsOptions());
            var second = cache.GetCachePath(
                "book",
                "chapter-1",
                "相同文字",
                new TtsOptions { Voice = "zh-CN-YunxiNeural" });

            Assert.NotEqual(first, second);

            var sourcePath = Path.Combine(
                Path.GetTempPath(),
                "kkindle-tts-source-" + Guid.NewGuid().ToString("N") + ".mp3");
            await File.WriteAllBytesAsync(
                sourcePath,
                [1, 2, 3]);
            var cached = await cache.WriteAsync(
                "book",
                "chapter-1",
                "相同文字",
                new TtsOptions(),
                sourcePath);
            try { File.Delete(sourcePath); }
            catch { }
            var statistics = await cache.GetStatisticsAsync();

            Assert.True(File.Exists(cached));
            Assert.Equal(1, statistics.FileCount);
            Assert.Equal(3, statistics.TotalBytes);

            await cache.DeleteBookAsync("book");
            Assert.Equal(
                new TtsCacheStatistics(0, 0),
                await cache.GetStatisticsAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ServicePrefetchesAndPlaysEverySegment()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var engine = new FakeEngine();
            var player = new FakePlayer();
            var document = new ReaderTtsDocument(
                "book:chapter",
                new string('字', 257),
                0,
                (_, _) => Task.CompletedTask,
                () => { },
                BookKey: "book",
                ChapterKey: "chapter");
            var stopped = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var service = new TtsService(
                engine,
                new TtsCacheManager(root),
                player,
                () => document);
            service.StateChanged += (_, args) =>
            {
                if (args.State == TtsPlaybackState.Stopped)
                    stopped.TrySetResult(true);
            };

            await service.StartAsync(new TtsSettings
            {
                MaxCharactersPerRequest = 120,
                AutoAdvance = false,
            });
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, player.PlayCount);
            // The first two chunks contain the same text and therefore share
            // one SHA256 cache entry.
            Assert.Equal(2, engine.SynthesisCount);
            Assert.Equal(TtsPlaybackState.Stopped, service.State);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "kkindle-tts-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class FakeEngine : ITtsEngine
    {
        private int _synthesisCount;

        public int SynthesisCount => Volatile.Read(ref _synthesisCount);

        public string Id => TtsSettings.DefaultProvider;
        public bool IsAvailable => true;

        public Task<TtsAvailability> CheckAvailabilityAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TtsAvailability(true, "fake"));

        public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TtsVoiceInfo>>([]);

        public async Task<TtsResult> SynthesizeAsync(
            string text,
            TtsOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _synthesisCount);
            var path = Path.Combine(
                Path.GetTempPath(),
                "kkindle-fake-" + Guid.NewGuid().ToString("N") + ".mp3");
            await File.WriteAllBytesAsync(path, [1], cancellationToken);
            return TtsResult.Success(path);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlayer : ITtsAudioPlayer
    {
        public int PlayCount { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsPaused => false;

        public Task PlayAsync(
            string audioPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(audioPath));
            PlayCount++;
            return Task.CompletedTask;
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
