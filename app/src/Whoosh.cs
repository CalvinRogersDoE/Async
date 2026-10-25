using System.Media;

namespace Async;

/// <summary>Synthesised portal sounds (a thump plus a pitch-swept whoosh), so there are no audio files to ship.</summary>
static class Whoosh
{
    public static SoundPlayer Make(bool opening)
    {
        const int rate = 44100;
        double seconds = opening ? 0.62 : 0.48;
        int n = (int)(rate * seconds);
        var pcm = new short[n];
        var rng = new Random(opening ? 7 : 11);
        double phase = 0, low = 0;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n, sec = (double)i / rate;
            double sweep = opening ? 90 + 560 * t * t : 620 - 540 * Math.Sqrt(t);
            phase += 2 * Math.PI * sweep / rate;
            double cutoff = opening ? 0.04 + 0.3 * t : 0.34 - 0.3 * t;
            low += cutoff * (rng.NextDouble() * 2 - 1 - low);
            double env = Math.Pow(Math.Sin(Math.PI * Math.Min(1, t * 1.08)), 1.4);
            double thump = Math.Sin(2 * Math.PI * 58 * sec) * Math.Exp(-sec * 26) * (opening ? 1.0 : 0.5);
            double s = env * (0.5 * low * 2.2 + 0.32 * Math.Sin(phase) + 0.12 * Math.Sin(phase * 2.01)) + thump;
            pcm[i] = (short)(Math.Clamp(s, -1, 1) * 0.55 * short.MaxValue);
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var v in pcm) w.Write(v);
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}
