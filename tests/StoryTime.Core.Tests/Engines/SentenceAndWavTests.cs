using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines.Narration;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

public class SentenceSplitterTests
{
    [Fact]
    public void Splits_OnTerminators()
    {
        var s = SentenceSplitter.Split("The fox slept. The moon rose! Was it late? It was.");
        Assert.Equal(4, s.Count);
        Assert.Equal("The moon rose!", s[1]);
    }

    [Fact]
    public void KeepsAbbreviationsTogether()
    {
        var s = SentenceSplitter.Split("Mr. Badger waved. Mrs. Owl smiled.");
        Assert.Equal(2, s.Count);
        Assert.Equal("Mr. Badger waved.", s[0]);
    }

    [Fact]
    public void ParagraphBreak_EndsASentence()
    {
        var s = SentenceSplitter.Split("A soft ending without a dot\n\nNext paragraph starts.");
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void AttachesClosingQuotes()
    {
        var s = SentenceSplitter.Split("\"Goodnight,\" said Pip. \"Sleep well!\" said Mo.");
        Assert.Equal(2, s.Count);
        Assert.EndsWith("said Pip.", s[0]);
        Assert.StartsWith("\"Sleep well!\"", s[1]);
    }

    [Fact]
    public void MergesTinyFragments()
    {
        var s = SentenceSplitter.Split("Oh! The fox curled up by the warm stove and slept.");
        Assert.Single(s);
    }

    [Fact]
    public void EmptyInput_YieldsNoSentences()
    {
        Assert.Empty(SentenceSplitter.Split("   \n  "));
    }

    [Fact]
    public void EmDash_BeforeCapital_IsASoftTerminator()
    {
        var s = SentenceSplitter.Split("The fox blinked twice — Every ripple smoothed away, soft and slow.");
        Assert.Equal(2, s.Count);
        Assert.Equal("The fox blinked twice —", s[0]);
        Assert.StartsWith("Every ripple", s[1]);
    }

    [Fact]
    public void EmDash_BeforeLowercase_StaysInline()
    {
        var s = SentenceSplitter.Split("They followed small clues — a thread of song, a friendly paw print.");
        Assert.Single(s);
    }

    [Fact]
    public void EmDash_BeforeQuote_IsASoftTerminator()
    {
        var s = SentenceSplitter.Split("The heron paused — \"Almost bedtime,\" she said.");
        Assert.Equal(2, s.Count);
        Assert.Equal("The heron paused —", s[0]);
    }
}

public class WavAudioTests
{
    [Fact]
    public void ToPcm16_ClampsAndScales()
    {
        var pcm = WavAudio.ToPcm16(new[] { 0f, 1f, -1f, 2f, -2f });
        Assert.Equal(0, pcm[0]);
        Assert.Equal(short.MaxValue, pcm[1]);
        Assert.Equal(-short.MaxValue, pcm[2]);
        Assert.Equal(short.MaxValue, pcm[3]); // clamped
        Assert.Equal(-short.MaxValue, pcm[4]); // clamped
    }

    [Fact]
    public void WriteThenReadDuration_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tw-wav-{Guid.NewGuid():N}.wav");
        try
        {
            // 24000 samples @ 24kHz mono = exactly 1 second, split over two chunks (concat math).
            WavAudio.Write(path, [new short[9000], new short[15000]], 24000);
            Assert.Equal(1.0, WavAudio.ReadDurationSeconds(path), 3);
            Assert.Equal(44 + 24000 * 2, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Concat_JoinsParts_ByteExactDurations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tw-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.wav");
            var b = Path.Combine(dir, "b.wav");
            var joined = Path.Combine(dir, "joined.wav");
            WavAudio.Write(a, [new short[24000]], 24000);          // 1.0 s
            WavAudio.Write(b, [new short[12000], new short[6000]], 24000); // 0.75 s
            var duration = WavAudio.Concat(joined, [a, b]);
            Assert.Equal(1.75, duration, 6);
            Assert.Equal(1.75, WavAudio.ReadDurationSeconds(joined), 6);
            Assert.Equal(44 + (24000 + 18000) * 2, new FileInfo(joined).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Concat_RejectsSampleRateMismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tw-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.wav");
            var b = Path.Combine(dir, "b.wav");
            WavAudio.Write(a, [new short[100]], 24000);
            WavAudio.Write(b, [new short[100]], 22050);
            Assert.Throws<InvalidOperationException>(() => WavAudio.Concat(Path.Combine(dir, "j.wav"), [a, b]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class NarrationScriptTests
{
    [Fact]
    public void SplitScript_MarksParagraphStarts()
    {
        var script = SentenceSplitter.SplitScript("The fox slept. The moon rose.\n\nA new morning came. Birds sang.");
        Assert.Equal(4, script.SentenceList.Count);
        Assert.Equal([2], script.ParagraphStartSet.Order().ToArray());
    }

    [Fact]
    public void SplitScript_FirstSentence_IsNeverAParagraphStart()
    {
        var script = SentenceSplitter.SplitScript("\n\nOnly paragraph here. Second sentence.");
        Assert.DoesNotContain(0, script.ParagraphStartSet);
    }

    [Fact]
    public void Take_KeepsOnlyParagraphStartsInRange()
    {
        var script = SentenceSplitter.SplitScript("One long sentence here.\n\nSecond paragraph opens. And continues.\n\nThird begins.");
        var teaser = script.Take(2);
        Assert.Equal(2, teaser.SentenceList.Count);
        Assert.Equal([1], teaser.ParagraphStartSet.Order().ToArray());
    }

    [Fact]
    public void SplitScript_Sentences_MatchFlatSplit()
    {
        const string text = "The fox slept. The moon rose!\n\nA new day. It was bright.";
        Assert.Equal(SentenceSplitter.Split(text), SentenceSplitter.SplitScript(text).SentenceList);
    }
}
