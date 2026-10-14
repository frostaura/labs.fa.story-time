namespace StoryTime.Core.Engines.Narration;

/// <summary>Minimal 16-bit PCM mono WAV writing + PCM concat math for narration assembly.</summary>
public static class WavAudio
{
    /// <summary>Convert float samples [-1,1] to 16-bit PCM.</summary>
    public static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            pcm[i] = (short)Math.Round(v * short.MaxValue);
        }
        return pcm;
    }

    /// <summary>Write a mono 16-bit PCM WAV file.</summary>
    public static void Write(string path, IReadOnlyList<short[]> pcmChunks, int sampleRate)
    {
        var totalSamples = pcmChunks.Sum(c => (long)c.Length);
        var dataBytes = totalSamples * 2;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16u);            // PCM chunk size
        w.Write((ushort)1);      // PCM format
        w.Write((ushort)1);      // mono
        w.Write((uint)sampleRate);
        w.Write((uint)(sampleRate * 2)); // byte rate (mono, 16-bit)
        w.Write((ushort)2);      // block align
        w.Write((ushort)16);     // bits per sample
        w.Write("data"u8);
        w.Write((uint)dataBytes);
        foreach (var chunk in pcmChunks)
            foreach (var s in chunk)
                w.Write(s);
    }

    /// <summary>Concatenate mono 16-bit PCM WAV parts (written by <see cref="Write"/>,
    /// canonical 44-byte header) into one WAV. Sample rates must match; the parts' PCM
    /// data is streamed byte-for-byte, so durations and offsets add exactly.</summary>
    public static double Concat(string outputPath, IReadOnlyList<string> partPaths)
    {
        if (partPaths.Count == 0) throw new ArgumentException("No WAV parts to concatenate");

        var meta = new List<(uint SampleRate, long DataBytes)>(partPaths.Count);
        foreach (var part in partPaths)
        {
            using var fs = new FileStream(part, FileMode.Open, FileAccess.Read);
            using var r = new BinaryReader(fs);
            fs.Seek(24, SeekOrigin.Begin);
            var rate = r.ReadUInt32();
            fs.Seek(40, SeekOrigin.Begin);
            var bytes = r.ReadUInt32();
            if (meta.Count > 0 && meta[0].SampleRate != rate)
                throw new InvalidOperationException($"WAV sample-rate mismatch: {meta[0].SampleRate} vs {rate} ({part})");
            meta.Add((rate, bytes));
        }

        var sampleRate = meta[0].SampleRate;
        var totalData = meta.Sum(m => m.DataBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var os = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(os))
        {
            w.Write("RIFF"u8);
            w.Write((uint)(36 + totalData));
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16u);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write(sampleRate);
            w.Write(sampleRate * 2);
            w.Write((ushort)2);
            w.Write((ushort)16);
            w.Write("data"u8);
            w.Write((uint)totalData);
            for (var p = 0; p < partPaths.Count; p++)
            {
                using var fs = new FileStream(partPaths[p], FileMode.Open, FileAccess.Read);
                fs.Seek(44, SeekOrigin.Begin);
                fs.CopyTo(os);
            }
        }
        return (double)totalData / (sampleRate * 2);
    }

    /// <summary>Read duration (seconds) from a WAV header — validation without a media stack.</summary>
    public static double ReadDurationSeconds(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);
        if (fs.Length < 44) return 0;
        fs.Seek(24, SeekOrigin.Begin);
        var sampleRate = r.ReadUInt32();
        fs.Seek(32, SeekOrigin.Begin);
        var blockAlign = r.ReadUInt16();
        fs.Seek(40, SeekOrigin.Begin);
        var dataBytes = r.ReadUInt32();
        return sampleRate == 0 || blockAlign == 0 ? 0 : (double)dataBytes / (sampleRate * blockAlign);
    }
}
