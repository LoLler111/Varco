using Concentus.Enums;
using Concentus.Structs;

namespace Varco.Core.Audio;

/// <summary>Formato dell'audio fra i PC: 48 kHz, stereo, float interlacciato (sinistro, destro, sinistro...).</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    /// <summary>Campioni per canale in un pacchetto Opus (20 ms).</summary>
    public const int FrameSamples = SampleRate / 50;
    /// <summary>Valori float in un pacchetto (tutti e due i canali).</summary>
    public const int FrameFloats = FrameSamples * Channels;

    public static int MsToFrames(int ms) => SampleRate / 1000 * ms;
}

/// <summary>
/// Comprime in pacchetti Opus da 20 ms ciò che arriva dalla cattura, a pezzi di lunghezza qualsiasi. Dopo un secondo di
/// silenzio assoluto smette di produrre pacchetti finché non torna il suono: con la musica in pausa non passa nulla in rete.
/// Non thread-safe: va usato da un solo thread (quello della cattura).
/// </summary>
public sealed class AudioEncoder
{
    /// <summary>Pacchetti di silenzio mandati prima di fermarsi (il centrale riceve la fine del suono, non uno stacco).</summary>
    public const int SilentFramesBeforePause = 50;
    private const float SilenceLevel = 1e-5f;

#pragma warning disable CS0618 // il costruttore diretto usa solo il codice C#; la factory potrebbe caricare una opus.dll trovata nel PATH
    private readonly OpusEncoder _opus = new(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
    private readonly float[] _pending = new float[AudioFormat.FrameFloats];
    private readonly byte[] _packet = new byte[4000];
    private int _fill;
    private int _silentFrames = SilentFramesBeforePause; // pacchetti di silenzio già mandati; all'inizio non c'è nulla da chiudere

    public AudioEncoder(int bitrate = 128_000)
    {
        _opus.Bitrate = bitrate;
        _opus.Complexity = 5; // C# puro: un buon compromesso fra qualità e CPU
        _opus.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>Pacchetti fermati per il silenzio?</summary>
    public bool Paused => _silentFrames >= SilentFramesBeforePause;

    public void Push(ReadOnlySpan<float> samples, Action<byte[]> onPacket)
    {
        while (samples.Length > 0)
        {
            var n = Math.Min(samples.Length, _pending.Length - _fill);
            samples[..n].CopyTo(_pending.AsSpan(_fill));
            _fill += n;
            samples = samples[n..];
            if (_fill < _pending.Length) break;
            _fill = 0;
            EncodeFrame(onPacket);
        }
    }

    private void EncodeFrame(Action<byte[]> onPacket)
    {
        var silent = true;
        foreach (var v in _pending)
            if (v > SilenceLevel || v < -SilenceLevel) { silent = false; break; }
        if (silent)
        {
            if (_silentFrames >= SilentFramesBeforePause) return;
            _silentFrames++;
        }
        else _silentFrames = 0;
        var len = _opus.Encode(_pending, AudioFormat.FrameSamples, _packet, _packet.Length);
        onPacket(_packet.AsSpan(0, len).ToArray());
    }
}

/// <summary>Decomprime i pacchetti Opus di un PC. Non thread-safe.</summary>
public sealed class AudioDecoder
{
#pragma warning disable CS0618 // vedi AudioEncoder
    private readonly OpusDecoder _opus = new(AudioFormat.SampleRate, AudioFormat.Channels);
#pragma warning restore CS0618
    // Il pacchetto Opus più lungo dura 120 ms.
    private readonly float[] _pcm = new float[AudioFormat.MsToFrames(120) * AudioFormat.Channels];

    /// <summary>I campioni del pacchetto (validi fino alla chiamata successiva).</summary>
    public ReadOnlySpan<float> Decode(byte[] packet)
    {
        var frames = _opus.Decode(packet, _pcm, _pcm.Length / AudioFormat.Channels, false);
        return _pcm.AsSpan(0, frames * AudioFormat.Channels);
    }

    public void Reset() => _opus.ResetState();
}
