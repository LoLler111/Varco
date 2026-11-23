using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Audio;
using static Varco.Agent.Wasapi;

namespace Varco.Agent;

/// <summary>
/// Audio fra i PC su Windows: cattura in loopback di ciò che esce dalle casse (sui PC che mandano) e riproduzione
/// dell'audio degli altri PC (sul centrale). Segue sempre il dispositivo predefinito: se l'utente passa dalle casse alle
/// cuffie, cattura e riproduzione si spostano da sole.
/// </summary>
internal sealed class WindowsAudio : IAudioPlatform
{
    private readonly string _muteFile;

    /// <param name="configDir">Lì si ricorda quale uscita è stata messa in muto, per toglierlo anche dopo un crash.</param>
    public WindowsAudio(string configDir)
    {
        _muteFile = Path.Combine(configDir, "audio-muted.txt");
        SpeakerMute.RecoverAfterCrash(_muteFile);
    }

    public IDisposable StartCapture(Action<ReadOnlySpan<float>> onSamples) => new LoopbackCapture(onSamples, new SpeakerMute(_muteFile));

    public IDisposable StartPlayer(Guid session, string displayName, PlayoutBuffer source) => new Player(session, displayName, source);

    /// <summary>Thread audio: apre il dispositivo predefinito, lavora finché non cambia o sparisce, poi lo riapre.</summary>
    private abstract class DeviceLoop : IDisposable
    {
        private readonly Thread _thread;
        protected readonly ManualResetEventSlim Stop = new();
        private readonly string _what;
        private string? _lastError;

        protected DeviceLoop(string what)
        {
            _what = what;
            _thread = new Thread(Run) { IsBackground = true, Name = $"Varco.Audio.{what}", Priority = ThreadPriority.AboveNormal };
        }

        protected void Begin() => _thread.Start();

        private void Run()
        {
            while (!Stop.IsSet)
            {
                try
                {
                    RunOnDefaultDevice();
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    // Nessun dispositivo, dispositivo in uso esclusivo, driver che si riavvia: si riprova, senza riempire il log.
                    var text = $"{ex.GetType().Name}: {ex.Message}";
                    if (text != _lastError) Log.Warn($"Audio ({_what}), riprovo: {text}");
                    _lastError = text;
                }
                Stop.Wait(2000);
            }
        }

        /// <summary>Lavora sul dispositivo predefinito; torna quando va riaperto (cambiato, sparito) o quando si chiude.</summary>
        protected abstract void RunOnDefaultDevice();

        /// <summary>Il dispositivo predefinito è ancora <paramref name="id"/>? Controllato al massimo una volta al secondo.</summary>
        protected static bool StillDefault(IMMDeviceEnumerator enumerator, string id, ref long nextCheck)
        {
            var now = Environment.TickCount64;
            if (now < nextCheck) return true;
            nextCheck = now + 1000;
            return DefaultDeviceId(enumerator, EDataFlow.eRender) == id;
        }

        public void Dispose()
        {
            Stop.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(3000);
        }
    }

    /// <summary>Cattura di ciò che esce dalle casse, che intanto restano mute.</summary>
    private sealed class LoopbackCapture : DeviceLoop
    {
        private readonly Action<ReadOnlySpan<float>> _onSamples;
        private readonly SpeakerMute _mute;
        private float[] _silence = new float[AudioFormat.FrameFloats];

        public LoopbackCapture(Action<ReadOnlySpan<float>> onSamples, SpeakerMute mute) : base("cattura")
        {
            _onSamples = onSamples;
            _mute = mute;
            Begin();
        }

        protected override unsafe void RunOnDefaultDevice()
        {
            var enumerator = CreateEnumerator();
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            try
            {
                device = DefaultDevice(enumerator, EDataFlow.eRender);
                if (device is null) return; // nessuna uscita audio: si riprova fra poco
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                client = Activate<IAudioClient>(device, IID_IAudioClient);
                // La sessione della cattura non va mostrata nel Mixer volume: non è un suono.
                Initialize(client, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_NOPERSIST | AUDCLNT_SESSIONFLAGS_DISPLAY_HIDE, 2_000_000, Guid.Empty);
                capture = GetService<IAudioCaptureClient>(client, IID_IAudioCaptureClient);
                _mute.Mute(device, id);
                Marshal.ThrowExceptionForHR(client.Start());
                long nextCheck = 0;
                while (!Stop.IsSet)
                {
                    Stop.Wait(10);
                    if (!Drain(capture)) return;
                    if (!StillDefault(enumerator, id, ref nextCheck))
                    {
                        Log.Info("Uscita audio predefinita cambiata: la cattura si sposta");
                        return;
                    }
                }
            }
            finally
            {
                try { client?.Stop(); } catch { }
                _mute.Restore();
                Release(capture);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }

        /// <summary>Passa avanti tutti i pacchetti pronti. False se il dispositivo va riaperto.</summary>
        private unsafe bool Drain(IAudioCaptureClient capture)
        {
            while (true)
            {
                var hr = capture.GetNextPacketSize(out var next);
                if (hr != 0) return LogDeviceGone(hr);
                if (next == 0) return true;
                hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                if (hr != 0) return LogDeviceGone(hr);
                try
                {
                    var floats = (int)frames * AudioFormat.Channels;
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                    {
                        if (_silence.Length < floats) _silence = new float[floats];
                        _onSamples(_silence.AsSpan(0, floats));
                    }
                    else _onSamples(new ReadOnlySpan<float>((void*)data, floats));
                }
                finally { capture.ReleaseBuffer(frames); }
            }
        }
    }

    /// <summary>Riproduzione dell'audio di un altro PC, con il suo nome nel Mixer volume.</summary>
    private sealed class Player : DeviceLoop
    {
        /// <summary>Audio tenuto nella scheda audio: il resto della riserva sta in <see cref="PlayoutBuffer"/>.</summary>
        private static readonly int DeviceFrames = AudioFormat.MsToFrames(30);

        private readonly Guid _session;
        private readonly string _name;
        private readonly PlayoutBuffer _source;

        public Player(Guid session, string name, PlayoutBuffer source) : base("riproduzione")
        {
            _session = session;
            _name = name;
            _source = source;
            Begin();
        }

        protected override unsafe void RunOnDefaultDevice()
        {
            var enumerator = CreateEnumerator();
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioRenderClient? render = null;
            IAudioSessionControl? control = null;
            using var wake = new AutoResetEvent(false);
            try
            {
                device = DefaultDevice(enumerator, EDataFlow.eRender);
                if (device is null) return;
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                client = Activate<IAudioClient>(device, IID_IAudioClient);
                // Senza NOPERSIST: Windows ricorda volume e muto della sessione (stesso id a ogni avvio).
                Initialize(client, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 1_000_000, _session);
                Marshal.ThrowExceptionForHR(client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle()));
                control = GetService<IAudioSessionControl>(client, IID_IAudioSessionControl);
                var ctx = Guid.Empty;
                control.SetDisplayName(_name, ref ctx);
                if (Environment.ProcessPath is { } exe) control.SetIconPath($"{exe},0", ref ctx);
                render = GetService<IAudioRenderClient>(client, IID_IAudioRenderClient);
                Marshal.ThrowExceptionForHR(client.GetBufferSize(out var bufferFrames));
                var target = (uint)Math.Min(DeviceFrames, (int)bufferFrames);
                if (!Fill(client, render, target)) return;
                Marshal.ThrowExceptionForHR(client.Start());
                long nextCheck = 0;
                while (!Stop.IsSet)
                {
                    wake.WaitOne(200);
                    if (!Fill(client, render, target)) return;
                    if (!StillDefault(enumerator, id, ref nextCheck))
                    {
                        Log.Info($"Uscita audio predefinita cambiata: {_name} si sposta");
                        return;
                    }
                }
            }
            finally
            {
                try { client?.Stop(); } catch { }
                Release(render);
                Release(control);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }

        /// <summary>Porta l'audio nella scheda a <paramref name="target"/> frame. False se il dispositivo va riaperto.</summary>
        private unsafe bool Fill(IAudioClient client, IAudioRenderClient render, uint target)
        {
            var hr = client.GetCurrentPadding(out var padding);
            if (hr != 0) return LogDeviceGone(hr);
            if (padding >= target) return true;
            var frames = target - padding;
            hr = render.GetBuffer(frames, out var data);
            if (hr != 0) return LogDeviceGone(hr);
            _source.Read(new Span<float>((void*)data, (int)frames * AudioFormat.Channels), Environment.TickCount64);
            render.ReleaseBuffer(frames, 0);
            return true;
        }
    }

    private static bool LogDeviceGone(int hr)
    {
        if (hr != AUDCLNT_E_DEVICE_INVALIDATED) Marshal.ThrowExceptionForHR(hr);
        Log.Info("Dispositivo audio scollegato o cambiato: lo riapro");
        return false;
    }

    /// <summary>
    /// Muto delle casse mentre il loro audio va al PC centrale. Si toglie solo il muto messo da Varco: se le casse erano
    /// già mute restano così. Il dispositivo messo in muto si scrive su file: se l'agent cade, al riavvio lo si toglie.
    /// </summary>
    private sealed class SpeakerMute(string stateFile)
    {
        private IAudioEndpointVolume? _volume;

        public void Mute(IMMDevice device, string id)
        {
            Restore();
            var volume = Activate<IAudioEndpointVolume>(device, IID_IAudioEndpointVolume);
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            if (muted)
            {
                Release(volume); // già muto per scelta dell'utente: non è affar nostro
                return;
            }
            try { File.WriteAllText(stateFile, id); }
            catch (Exception ex) { Log.Warn($"Impossibile ricordare il muto delle casse: {ex.Message}"); }
            var ctx = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(true, ref ctx));
            _volume = volume;
        }

        public void Restore()
        {
            if (_volume is not { } volume) return;
            _volume = null;
            try
            {
                var ctx = Guid.Empty;
                volume.SetMute(false, ref ctx);
            }
            catch (Exception ex) { Log.Warn($"Impossibile togliere il muto alle casse: {ex.Message}"); }
            finally { Release(volume); }
            try { File.Delete(stateFile); } catch { }
        }

        /// <summary>L'agent è caduto mentre le casse erano mute per l'audio fra i PC: si toglie il muto.</summary>
        public static void RecoverAfterCrash(string stateFile)
        {
            if (!File.Exists(stateFile)) return;
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioEndpointVolume? volume = null;
            try
            {
                var id = File.ReadAllText(stateFile).Trim();
                enumerator = CreateEnumerator();
                if (id.Length > 0 && enumerator.GetDevice(id, out device) == 0)
                {
                    volume = Activate<IAudioEndpointVolume>(device, IID_IAudioEndpointVolume);
                    var ctx = Guid.Empty;
                    volume.SetMute(false, ref ctx);
                    Log.Info("Tolto il muto alle casse rimasto dall'audio fra i PC");
                }
                File.Delete(stateFile);
            }
            catch (Exception ex) { Log.Warn($"Muto delle casse rimasto dall'audio fra i PC: {ex.Message}"); }
            finally
            {
                Release(volume);
                Release(device);
                Release(enumerator);
            }
        }
    }
}
