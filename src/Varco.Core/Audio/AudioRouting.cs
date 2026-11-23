using System.Text.Json.Serialization;
using Varco.Core.Protocol;

namespace Varco.Core.Audio;

/// <summary>
/// Audio fra i PC: un PC centrale (quello con le cuffie o le casse) riceve l'audio degli altri PC scelti uno a uno.
/// L'audio va in una sola direzione, dagli altri PC al centrale. È un'impostazione di tutto il gruppo, come la
/// disposizione degli schermi: si cambia da qualsiasi PC e passa agli altri con <see cref="AudioRoutingMsg"/>; fra due
/// cambi vince il più recente.
/// </summary>
public sealed class AudioRouting
{
    /// <summary>Il PC centrale. <see cref="Guid.Empty"/> = funzione spenta.</summary>
    public Guid Hub { get; set; }
    /// <summary>I PC che mandano il loro audio al centrale.</summary>
    public List<Guid> Sources { get; set; } = [];
    /// <summary>Quando è stata cambiata l'ultima volta (decide fra due cambi fatti su PC diversi).</summary>
    public DateTime ChangedUtc { get; set; }

    [JsonIgnore]
    public bool Enabled => Hub != Guid.Empty;

    /// <summary>Il PC deve mandare il suo audio al centrale?</summary>
    public bool Sends(Guid pc) => Enabled && pc != Hub && Sources.Contains(pc);

    public AudioRoutingMsg ToMessage() => new(Hub, Sources.ToArray(), ChangedUtc.Ticks);

    public static AudioRouting FromMessage(AudioRoutingMsg m) => new()
    {
        Hub = m.Hub,
        Sources = m.Sources.Where(id => id != m.Hub).Distinct().ToList(),
        ChangedUtc = new DateTime(m.ChangedUtcTicks, DateTimeKind.Utc),
    };

    /// <summary>L'impostazione arrivata da un altro PC sostituisce la nostra? Solo se è più recente.</summary>
    public bool IsOlderThan(AudioRoutingMsg incoming) => incoming.ChangedUtcTicks > ChangedUtc.Ticks;

    /// <summary>
    /// Nuova impostazione cambiata qui, con l'ora del cambio. Sempre più recente della precedente, anche se l'orologio di
    /// questo PC è indietro rispetto a quello del PC che l'ha cambiata prima: altrimenti gli altri PC la ignorerebbero.
    /// </summary>
    public static AudioRouting Changed(AudioRouting previous, Guid hub, IEnumerable<Guid> sources, DateTime nowUtc) => new()
    {
        Hub = hub,
        Sources = hub == Guid.Empty ? [] : sources.Where(id => id != hub && id != Guid.Empty).Distinct().ToList(),
        ChangedUtc = nowUtc > previous.ChangedUtc ? nowUtc : previous.ChangedUtc.AddTicks(1),
    };

    /// <summary>Id della sessione audio di Windows per l'audio di un PC: sempre lo stesso, così il Mixer volume ne ricorda il volume.</summary>
    public static Guid SessionFor(Guid pc)
    {
        var b = pc.ToByteArray();
        ReadOnlySpan<byte> salt = [0x56, 0x61, 0x72, 0x63, 0x6F, 0x2E, 0x41, 0x75, 0x64, 0x69, 0x6F, 0x2E, 0x50, 0x43, 0x00, 0x01];
        for (var i = 0; i < b.Length; i++) b[i] ^= salt[i];
        return new Guid(b);
    }
}
