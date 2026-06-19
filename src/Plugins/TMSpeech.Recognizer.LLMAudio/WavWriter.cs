using System.Text;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>把 32-bit float 单声道采样编码为 16-bit PCM 的 WAV 字节流。</summary>
public static class WavWriter
{
    public static byte[] FromFloat(float[] samples, int sampleRate)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = samples.Length * 2;

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms, Encoding.ASCII, true);

        // RIFF header
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // PCM fmt chunk size
        w.Write((short)1);                 // audio format = PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        foreach (var f in samples)
        {
            short s = (short)Math.Clamp(f * 32767f, -32768f, 32767f);
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }
}
