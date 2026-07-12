using System.Runtime.InteropServices;

namespace TMSpeech.Recognizer.LLMAudio;

internal static class AudioFrameConverter
{
    public static (byte[] Pcm, float Rms) FloatBytesToPcm16(byte[] data)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data);
        var pcm = new byte[floats.Length * 2];
        double sumSquares = 0;
        for (var i = 0; i < floats.Length; i++)
        {
            var value = floats[i];
            sumSquares += (double)value * value;
            var sample = (short)Math.Clamp(value * 32767f, -32768f, 32767f);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        var rms = floats.Length == 0 ? 0 : (float)Math.Sqrt(sumSquares / floats.Length);
        return (pcm, rms);
    }
}
