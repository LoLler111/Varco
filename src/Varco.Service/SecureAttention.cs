using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Varco.Core;
using Varco.Core.Control;

namespace Varco.Service;

/// <summary>
/// Ctrl+Alt+Canc chiesto da un altro PC. Il vero Ctrl+Alt+Canc arriva solo dalla tastiera fisica: un programma può
/// simularlo solo con SendSAS, e solo se è un servizio (o un'app di accessibilità) e la policy SoftwareSASGeneration
/// lo permette. Per questo lo esegue il servizio, non l'agent: l'agent segnala l'evento <see cref="ControlProtocol.SasEventName"/>,
/// accessibile solo a SYSTEM.
/// </summary>
internal sealed class SecureAttention : IDisposable
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "SoftwareSASGeneration";
    /// <summary>Segno lasciato dal setup e dal servizio: la policy l'ha attivata Varco (la disinstallazione la rimette com'era).</summary>
    public const string MarkerKey = @"SOFTWARE\Varco";
    public const string MarkerValue = "AddedSoftwareSAS";

    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _wait;
    private long _last;

    public SecureAttention()
    {
        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        _event = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ControlProtocol.SasEventName, out _, sec);
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => Send(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void Send()
    {
        // Al massimo uno al secondo: tenere premuto Ctrl+Alt+Fine ripete il tasto.
        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _last, now) < 1000) return;
        try
        {
            EnsurePolicy();
            SendSAS(false);
            Log.Info("Ctrl+Alt+Canc chiesto dal PC che comanda: inviato");
        }
        catch (Exception ex)
        {
            Log.Error("Ctrl+Alt+Canc non inviato", ex);
        }
    }

    /// <summary>
    /// SoftwareSASGeneration: 1 = servizi, 2 = app di accessibilità, 3 = entrambi. Si aggiunge il bit dei servizi
    /// lasciando com'era il resto. Se l'ha già impostato un criterio di gruppo che lo vieta, Windows lo rimette e SendSAS non fa nulla.
    /// </summary>
    public static void EnsurePolicy()
    {
        using var key = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true);
        var current = key.GetValue(PolicyValue) is int v ? v : 0;
        if ((current & 1) != 0) return;
        key.SetValue(PolicyValue, current | 1, RegistryValueKind.DWord);
        using (var marker = Registry.LocalMachine.CreateSubKey(MarkerKey, writable: true))
            marker.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
        Log.Info($"Criterio SoftwareSASGeneration: {current} → {current | 1} (i servizi possono simulare Ctrl+Alt+Canc)");
    }

    /// <summary>Disinstallazione: toglie il bit dei servizi se l'aveva aggiunto Varco.</summary>
    public static void RestorePolicy()
    {
        try
        {
            using (var marker = Registry.LocalMachine.OpenSubKey(MarkerKey))
                if (marker?.GetValue(MarkerValue) is not int added || added == 0) return;
            using (var key = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true))
            {
                if (key?.GetValue(PolicyValue) is int v)
                {
                    if ((v & ~1) == 0) key.DeleteValue(PolicyValue, throwOnMissingValue: false);
                    else key.SetValue(PolicyValue, v & ~1, RegistryValueKind.DWord);
                }
            }
            Registry.LocalMachine.DeleteSubKeyTree(MarkerKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Criterio SoftwareSASGeneration non ripristinato: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _event.Dispose();
    }

    /// <param name="asUser">FALSE: chi chiama è un servizio (LocalSystem), non l'utente collegato.</param>
    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);
}
