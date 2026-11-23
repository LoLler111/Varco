using System.Diagnostics;
using Varco.Core.Audio;
using Varco.Core.Input;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class AudioRoutingTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Only_the_chosen_pcs_send_and_never_the_hub()
    {
        var r = AudioRouting.Changed(new AudioRouting(), A, [A, B], DateTime.UtcNow);
        Assert.Equal([B], r.Sources); // il centrale non manda a sé stesso
        Assert.True(r.Sends(B));
        Assert.False(r.Sends(A));
        Assert.False(r.Sends(C));
        var off = AudioRouting.Changed(r, Guid.Empty, [B], DateTime.UtcNow);
        Assert.False(off.Enabled);
        Assert.False(off.Sends(B));
    }

    [Fact]
    public void A_change_is_always_newer_even_with_a_clock_behind()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var first = AudioRouting.Changed(new AudioRouting(), A, [B], now);
        var second = AudioRouting.Changed(first, B, [A], now.AddMinutes(-5)); // orologio di questo PC indietro
        Assert.True(second.ChangedUtc > first.ChangedUtc);
        Assert.True(first.IsOlderThan(second.ToMessage()));
        Assert.False(second.IsOlderThan(first.ToMessage()));
    }

    [Fact]
    public void The_message_round_trips_and_the_session_id_is_stable_per_pc()
    {
        var r = AudioRouting.Changed(new AudioRouting(), A, [B, C], DateTime.UtcNow);
        var back = AudioRouting.FromMessage(Assert.IsType<AudioRoutingMsg>(MessageCodec.Decode(MessageCodec.Encode(r.ToMessage()))));
        Assert.Equal(r.Hub, back.Hub);
        Assert.Equal(r.Sources, back.Sources);
        Assert.Equal(r.ChangedUtc, back.ChangedUtc);

        Assert.Equal(AudioRouting.SessionFor(B), AudioRouting.SessionFor(B));
        Assert.NotEqual(AudioRouting.SessionFor(B), AudioRouting.SessionFor(C));
        Assert.NotEqual(B, AudioRouting.SessionFor(B));
    }

    [Fact]
    public void Audio_frames_survive_the_codec()
    {
        var f = new AudioFrame(65535, [1, 2, 3, 250]);
        var back = Assert.IsType<AudioFrame>(MessageCodec.Decode(MessageCodec.Encode(f)));
        Assert.Equal(f.Seq, back.Seq);
        Assert.Equal(f.Data, back.Data);
    }
}

public class AudioCodecTests
{
    private static float[] Sine(int frames, double hz = 440, float amplitude = 0.5f)
    {
        var s = new float[frames * AudioFormat.Channels];
        for (var i = 0; i < frames; i++)
            s[2 * i] = s[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / AudioFormat.SampleRate));
        return s;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += v * v;
        return Math.Sqrt(sum / Math.Max(1, s.Length));
    }

    [Fact]
    public void Music_survives_compression_in_20_ms_packets()
    {
        var enc = new AudioEncoder();
        var dec = new AudioDecoder();
        var packets = new List<byte[]>();
        var input = Sine(AudioFormat.SampleRate); // 1 s, a pezzi di lunghezza qualsiasi come dalla cattura
        for (var i = 0; i < input.Length; i += 882) enc.Push(input.AsSpan(i, Math.Min(882, input.Length - i)), packets.Add);
        Assert.Equal(50, packets.Count);
        Assert.All(packets, p => Assert.InRange(p.Length, 10, 1275));

        var output = new List<float>();
        foreach (var p in packets)
        {
            var pcm = dec.Decode(p);
            Assert.Equal(AudioFormat.FrameFloats, pcm.Length);
            output.AddRange(pcm.ToArray());
        }
        var tail = output.Skip(output.Count / 2).ToArray(); // dopo l'avvio del codec
        Assert.InRange(Rms(tail), 0.30, 0.42); // seno ampio 0,5 → RMS 0,354
    }

    [Fact]
    public void After_a_second_of_silence_no_packets_go_out_until_sound_returns()
    {
        var enc = new AudioEncoder();
        var packets = new List<byte[]>();
        enc.Push(Sine(AudioFormat.FrameSamples * 5), packets.Add);
        Assert.Equal(5, packets.Count);

        packets.Clear();
        enc.Push(new float[AudioFormat.FrameFloats * 200], packets.Add); // 4 s di silenzio
        Assert.Equal(AudioEncoder.SilentFramesBeforePause, packets.Count);
        Assert.True(enc.Paused);

        packets.Clear();
        enc.Push(Sine(AudioFormat.FrameSamples * 3), packets.Add);
        Assert.Equal(3, packets.Count);
        Assert.False(enc.Paused);
    }

    [Fact]
    public void A_silent_stream_from_the_start_sends_nothing()
    {
        var enc = new AudioEncoder();
        var packets = new List<byte[]>();
        enc.Push(new float[AudioFormat.FrameFloats * 10], packets.Add);
        Assert.Empty(packets);
    }

    [Fact]
    public void Compression_is_well_within_real_time()
    {
        var enc = new AudioEncoder();
        var dec = new AudioDecoder();
        var input = Sine(AudioFormat.SampleRate * 5); // 5 s
        enc.Push(input.AsSpan(0, AudioFormat.FrameFloats * 10), _ => { }); // riscaldamento
        var sw = Stopwatch.StartNew();
        enc.Push(input, p => dec.Decode(p));
        sw.Stop();
        // Codifica + decodifica di 5 s: in tempo reale sarebbero 5000 ms. Anche su un PC lento deve restare una piccola frazione.
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{sw.ElapsedMilliseconds} ms per 5 s di audio");
    }
}

public class PlayoutBufferTests
{
    private static readonly int Packet = AudioFormat.FrameFloats; // 20 ms

    private static float[] Tone(int floats, float value = 0.25f) => Enumerable.Repeat(value, floats).ToArray();

    [Fact]
    public void Plays_only_once_the_reserve_reaches_the_target()
    {
        var b = new PlayoutBuffer();
        var dest = new float[Packet];
        b.Push(Tone(Packet), 0); // 20 ms < 40 ms di obiettivo
        Assert.Equal(0, b.Read(dest, 1));
        Assert.All(dest, v => Assert.Equal(0f, v));
        b.Push(Tone(Packet), 20);
        Assert.Equal(Packet, b.Read(dest, 21));
        Assert.All(dest, v => Assert.Equal(0.25f, v));
    }

    [Fact]
    public void A_slow_network_raises_the_target_and_calm_lowers_it_again()
    {
        var b = new PlayoutBuffer();
        var dest = new float[Packet * 3];
        b.Push(Tone(Packet * 2), 1000);
        Assert.Equal(Packet * 2, b.Read(dest, 1010)); // arrivano ancora pacchetti: è la rete
        Assert.Equal(1, b.Underruns);
        Assert.Equal(60, b.TargetMs);

        b.Push(Tone(Packet), 1000 + PlayoutBuffer.RelaxMs + 10);
        Assert.Equal(40, b.TargetMs);
    }

    [Fact]
    public void Running_dry_after_the_music_stops_is_not_blamed_on_the_network()
    {
        var b = new PlayoutBuffer();
        var dest = new float[Packet * 3];
        b.Push(Tone(Packet * 2), 1000);
        Assert.Equal(Packet * 2, b.Read(dest, 1000 + PlayoutBuffer.StreamGapMs + 50));
        Assert.Equal(0, b.Underruns);
        Assert.Equal(40, b.TargetMs);
    }

    [Fact]
    public void Extra_delay_left_over_from_the_start_is_removed()
    {
        var b = new PlayoutBuffer();
        var chunk = AudioFormat.MsToFrames(10) * AudioFormat.Channels;
        b.Push(Tone(chunk * 11), 0); // 110 ms accumulati mentre la scheda audio si apriva
        var dest = new float[chunk];
        for (long t = 10; t <= 2500; t += 10) // poi arriva e si consuma a ritmo costante
        {
            b.Push(Tone(chunk), t);
            Assert.Equal(chunk, b.Read(dest, t));
        }
        Assert.InRange(b.BufferedMs, 30, 60);
        Assert.Equal(0, b.Underruns);
    }

    [Fact]
    public void A_burst_is_trimmed_to_keep_the_delay_low()
    {
        var b = new PlayoutBuffer();
        b.Push(Tone(Packet * 25), 0); // 500 ms tutti insieme
        Assert.Equal(40, b.BufferedMs);
        Assert.True(b.DroppedFrames > 0);
        // Si buttano i campioni più vecchi e sempre frame interi: i canali non si scambiano.
        var left = Enumerable.Range(0, Packet / 2).SelectMany(_ => new[] { 1f, -1f }).ToArray();
        var c = new PlayoutBuffer();
        c.Push(left, 0);
        c.Push(left, 0);
        c.Push(Tone(Packet * 20), 0);
        c.Push(left.Concat(left).Concat(left).ToArray(), 0);
        var dest = new float[c.BufferedMs * AudioFormat.SampleRate / 1000 * AudioFormat.Channels];
        c.Read(dest, 0);
        Assert.Equal(1f, dest[^2]);
        Assert.Equal(-1f, dest[^1]);
    }
}

public class AudioShareTests
{
    private static readonly Guid Hub = Guid.NewGuid(), Laptop = Guid.NewGuid(), Office = Guid.NewGuid();

    private sealed class Net(Guid self) : IPeerNetwork
    {
        private readonly object _gate = new();
        private readonly List<(Guid To, Message Msg)> _sent = [];
        public readonly HashSet<Guid> Connected = [];
        public Guid SelfId { get; } = self;
        public bool IsConnected(Guid id) { lock (_gate) return Connected.Contains(id); }
        public bool Send(Guid id, Message m) { lock (_gate) { _sent.Add((id, m)); return Connected.Contains(id); } }
        public void Broadcast(Message m) { lock (_gate) foreach (var c in Connected) _sent.Add((c, m)); }
        public List<(Guid To, Message Msg)> Sent { get { lock (_gate) return _sent.ToList(); } }
    }

    private sealed class Platform : IAudioPlatform
    {
        public Action<ReadOnlySpan<float>>? Capture;
        public int CaptureStarts, CaptureStops;
        public readonly List<(Guid Session, string Name, PlayoutBuffer Source)> Players = [];
        public int PlayerStops;

        public IDisposable StartCapture(Action<ReadOnlySpan<float>> onSamples)
        {
            Capture = onSamples;
            CaptureStarts++;
            return new Stopper(() => { Capture = null; CaptureStops++; });
        }

        public IDisposable StartPlayer(Guid session, string displayName, PlayoutBuffer source)
        {
            Players.Add((session, displayName, source));
            return new Stopper(() => PlayerStops++);
        }

        private sealed class Stopper(Action stop) : IDisposable { public void Dispose() => stop(); }
    }

    private static (AudioShare Share, Net Net, Platform Platform, Func<AudioRouting> Routing) Make(Guid self, AudioRouting? initial = null)
    {
        var net = new Net(self);
        var platform = new Platform();
        var routing = initial ?? new AudioRouting();
        var names = new Dictionary<Guid, string> { [Hub] = "DESKTOP", [Laptop] = "PORTATILE", [Office] = "UFFICIO" };
        var share = new AudioShare(net, platform, () => routing, r => routing = r, id => names[id]);
        return (share, net, platform, () => routing);
    }

    private static byte[] Packet()
    {
        var enc = new AudioEncoder();
        byte[]? packet = null;
        var tone = Enumerable.Range(0, AudioFormat.FrameFloats).Select(i => (float)Math.Sin(i / 10.0) * 0.3f).ToArray();
        enc.Push(tone, p => packet = p);
        return packet!;
    }

    [Fact]
    public void The_hub_plays_each_chosen_pc_with_its_name_and_ignores_the_others()
    {
        var (share, net, platform, _) = Make(Hub, AudioRouting.Changed(new AudioRouting(), Hub, [Laptop], DateTime.UtcNow));
        using var _ = share;
        net.Connected.UnionWith([Laptop, Office]);
        var packet = Packet();

        Assert.True(share.OnMessage(Laptop, new AudioFrame(0, packet)));
        Assert.True(share.OnMessage(Laptop, new AudioFrame(1, packet)));
        Assert.True(share.OnMessage(Office, new AudioFrame(0, packet))); // non è tra i PC scelti
        var player = Assert.Single(platform.Players);
        Assert.Equal("Audio PORTATILE", player.Name);
        Assert.Equal(AudioRouting.SessionFor(Laptop), player.Session);
        Assert.Equal(40, player.Source.BufferedMs);
        Assert.Equal([Laptop], share.Receiving(Environment.TickCount64));
        Assert.True(share.Playing);

        share.OnPeerDisconnected(Laptop);
        Assert.Equal(1, platform.PlayerStops);
        Assert.False(share.Playing);
    }

    [Fact]
    public void A_source_sends_only_while_the_hub_is_connected()
    {
        var (share, net, platform, _) = Make(Laptop, AudioRouting.Changed(new AudioRouting(), Hub, [Laptop], DateTime.UtcNow));
        using var _ = share;
        share.Start();
        Assert.Equal(0, platform.CaptureStarts); // centrale non collegato

        net.Connected.Add(Hub);
        share.OnPeerConnected(Hub);
        Assert.True(share.Sending);
        Assert.Contains(net.Sent, s => s.To == Hub && s.Msg is AudioRoutingMsg);

        // Ciò che esce dalle casse arriva al centrale in pacchetti da 20 ms.
        var tone = Enumerable.Range(0, AudioFormat.FrameFloats * 3).Select(i => (float)Math.Sin(i / 10.0) * 0.3f).ToArray();
        platform.Capture!(tone);
        Assert.Equal([0, 1, 2], net.Sent.Where(s => s.To == Hub).Select(s => s.Msg).OfType<AudioFrame>().Select(f => (int)f.Seq));

        net.Connected.Remove(Hub);
        share.OnPeerDisconnected(Hub);
        Assert.False(share.Sending);
        Assert.Equal(1, platform.CaptureStops);
    }

    [Fact]
    public void The_newest_setting_wins_across_the_group()
    {
        var (share, net, platform, routing) = Make(Laptop);
        using var _ = share;
        net.Connected.Add(Hub);
        var now = DateTime.UtcNow;
        var fromHub = AudioRouting.Changed(new AudioRouting(), Hub, [Laptop], now);
        share.OnMessage(Hub, fromHub.ToMessage());
        Assert.Equal(Hub, routing().Hub);
        Assert.True(share.Sending);

        var stale = AudioRouting.Changed(new AudioRouting(), Guid.Empty, [], now.AddMinutes(-1));
        share.OnMessage(Hub, stale.ToMessage());
        Assert.Equal(Hub, routing().Hub); // più vecchia: ignorata

        // Cambiata qui: passa a tutti i collegati e la cattura si ferma.
        share.SetRouting(Hub, [], now.AddMinutes(1));
        Assert.False(share.Sending);
        Assert.Contains(net.Sent, s => s.To == Hub && s.Msg is AudioRoutingMsg { Sources.Length: 0 });
        Assert.Equal(1, platform.CaptureStops);
    }

    [Fact]
    public void Removing_a_pc_from_the_list_closes_its_player()
    {
        var (share, net, platform, _) = Make(Hub, AudioRouting.Changed(new AudioRouting(), Hub, [Laptop, Office], DateTime.UtcNow));
        using var _ = share;
        net.Connected.UnionWith([Laptop, Office]);
        share.OnMessage(Laptop, new AudioFrame(0, Packet()));
        share.OnMessage(Office, new AudioFrame(0, Packet()));
        Assert.Equal(2, platform.Players.Count);
        share.SetRouting(Hub, [Office], DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(1, platform.PlayerStops);
        Assert.Equal([Office], share.Receiving(Environment.TickCount64));
    }

    [Fact]
    public void Volume_keys_for_a_pc_that_sends_its_audio_go_to_the_hub()
    {
        var (share, net, _, _) = Make(Hub, AudioRouting.Changed(new AudioRouting(), Hub, [Laptop], DateTime.UtcNow));
        using var _ = share;
        Assert.Null(share.VolumeKeysTarget(Laptop)); // non collegato: niente deviazione
        net.Connected.Add(Laptop);
        Assert.Equal(Hub, share.VolumeKeysTarget(Laptop));
        Assert.Null(share.VolumeKeysTarget(Office));
        Assert.Null(share.VolumeKeysTarget(Hub));
    }
}

public partial class CoordinatorTests
{
    [Fact]
    public void Volume_keys_follow_the_audio_to_the_hub_while_play_pause_goes_to_the_pc_that_plays()
    {
        var (c, net, _, other) = Setup();
        var router = new MediaKeyRouter(net.SelfId);
        c.MediaRouter = router;
        router.SetPlaying(other, true, Environment.TickCount64);
        c.VolumeKeysTarget = pc => pc == other ? net.SelfId : null; // l'altro PC manda il suo audio qui

        Assert.False(c.OnLocalKey(0xAF, 0x30, KeyFlags.Extended)); // volume su: resta qui, dove si sente
        Assert.False(c.OnLocalKey(0xAF, 0x30, KeyFlags.Extended | KeyFlags.Up));
        Assert.True(c.OnLocalKey(0xB3, 0x22, KeyFlags.Extended)); // play/pausa: al PC che suona
        Assert.True(c.OnLocalKey(0xB3, 0x22, KeyFlags.Extended | KeyFlags.Up));
        Assert.Equal([0xB3, 0xB3], net.Sent.Where(s => s.To == other).Select(s => s.Msg).OfType<MediaKeyMsg>().Select(m => (int)m.Vk));
    }

    [Fact]
    public void While_controlling_a_pc_that_sends_its_audio_the_volume_keys_stay_on_the_hub()
    {
        var (c, net, _, other) = Setup();
        c.MediaRouter = new MediaKeyRouter(net.SelfId);
        c.MediaKeysFollowAudio = () => false; // anche con l'opzione spenta: il volume dell'altro PC non si sentirebbe
        c.VolumeKeysTarget = pc => pc == other ? net.SelfId : null;
        c.OnLocalMouseMove(1919, 540);
        Assert.Equal(InputMode.Controlling, c.Mode);
        Assert.False(c.OnLocalKey(0xAE, 0x2E, KeyFlags.Extended));
        Assert.False(c.OnLocalKey(0xAE, 0x2E, KeyFlags.Extended | KeyFlags.Up));
        Assert.DoesNotContain(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: 0xAE } or MediaKeyMsg);
    }
}
