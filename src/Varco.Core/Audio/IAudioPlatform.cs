namespace Varco.Core.Audio;

/// <summary>La parte dell'audio fra i PC che dipende da Windows (WASAPI). Formato: vedi <see cref="AudioFormat"/>.</summary>
public interface IAudioPlatform
{
    /// <summary>
    /// Cattura ciò che esce dalle casse di questo PC (il dispositivo predefinito, anche quando l'utente lo cambia) e lo
    /// passa a <paramref name="onSamples"/> dal thread di cattura. Finché la cattura è attiva le casse restano mute: il
    /// suono si sente sul PC centrale. Chiudendo la cattura tornano come prima.
    /// </summary>
    IDisposable StartCapture(Action<ReadOnlySpan<float>> onSamples);

    /// <summary>
    /// Riproduce sul dispositivo predefinito l'audio letto da <paramref name="source"/>, come sessione
    /// <paramref name="session"/> con il nome <paramref name="displayName"/> nel Mixer volume di Windows.
    /// </summary>
    IDisposable StartPlayer(Guid session, string displayName, PlayoutBuffer source);
}
