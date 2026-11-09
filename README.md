# Varco

Un solo mouse e una sola tastiera per 2–4 PC Windows sulla stessa rete locale, via cavo o Wi-Fi.
Funziona come Mouse Without Borders, ma con un obiettivo preciso: **quando la connessione cade, si ricollega da sola**.
Ci riesce dopo uno standby, un cambio di rete, un riavvio o un cavo staccato, senza "Refresh connections".

## Come si usa

1. Sul PC di sviluppo: `.\scripts\publish.ps1` → crea `publish\Varco`.
2. Copia la cartella su ogni PC ed esegui **come amministratore** `install.ps1`.
3. Primo PC: icona Varco → *Impostazioni* → **Nuovo gruppo** → **Copia** la chiave.
4. Altri PC: incolla la chiave in **Unisciti**. I PC si trovano da soli.
5. Disposizione: in *Impostazioni* ordina i PC da sinistra a destra e premi **Applica**.
   La disposizione si propaga a tutto il gruppo.

- **Ctrl+Alt+F12** riporta sempre mouse e tastiera sul PC fisico (via di fuga).
- Se tocchi il mouse o la tastiera del PC comandato, il controllo passa a quel PC.

Per disinstallare: `uninstall.ps1` come amministratore (`-KeepConfig` mantiene chiave e disposizione).

## Architettura

```
┌──────────── PC ─────────────────────────────────────────────────────┐
│ Varco.Service  (servizio Windows, LocalSystem, sessione 0)          │
│   └─ avvia e sorveglia ▼  (CreateProcessAsUser nella sessione attiva)│
│ Varco.Agent    (SYSTEM, sessione dell'utente)                        │
│   ├─ WindowsInput: hook LL + SendInput, segue il desktop attivo      │
│   │     (Default ↔ Winlogon: schermata di blocco, UAC, Ctrl+Alt+Canc)│
│   ├─ InputCoordinator: Local / Controlling / Controlled             │
│   ├─ NetworkNode: discovery UDP, TCP cifrato, heartbeat, riconnessione│
│   └─ pipe "Varco.Control" ◄── Varco.Tray (icona, impostazioni, utente)│
└─────────────────────────────────────────────────────────────────────┘
```

| Progetto | Contenuto |
|---|---|
| `src/Varco.Core` | Rete, cifratura, protocollo, layout, logica dell'input. Non dipende da Windows ed è coperto dai test. |
| `src/Varco.Agent` | Parti Win32: hook, iniezione, cambio di desktop, notifiche di standby, pipe di controllo. |
| `src/Varco.Service` | Servizio che tiene l'agent vivo nella sessione della console. |
| `src/Varco.Tray` | Icona nella tray (WinForms) con stato e impostazioni. |
| `tests/Varco.Tests` | Test su crittografia, riconnessione reale su loopback, geometria, layout e coordinatore. |

### Riconnessione: perché qui non si blocca

| Problema di Mouse Without Borders | Soluzione in Varco |
|---|---|
| Trova i PC per nome (DNS/NetBIOS) | Ogni PC ha un GUID. Lo trova tramite beacon UDP firmati, sugli ultimi indirizzi riusciti salvati e sugli indirizzi manuali. |
| Socket "mezzi aperti" dopo standby o cavo staccato | Heartbeat applicativo ogni 500 ms: dopo 3 s di silenzio la connessione è chiusa (il valore tollera i picchi del Wi-Fi). |
| Nessuno ritenta da solo | Per ogni PC c'è un ciclo che ritenta. L'attesa cresce da 0,5 s a un massimo di 5 s e si azzera a ogni segnale: beacon, cambio di rete, risveglio, caduta della connessione. |
| Dipende da chi torna online per primo | Entrambi i lati compongono. Se nascono due connessioni, la scelta è deterministica: nella finestra di gara vince quella aperta dall'ID più basso, altrimenti la più recente. |
| Socket rimasti vecchi dopo lo standby | Al risveglio tutte le connessioni vengono chiuse e riaperte. |
| Il firewall blocca dopo che la rete diventa "Pubblica" | Le regole del firewall valgono per tutti i profili. |
| Tasti incastrati quando cade la connessione | Il PC comandato rilascia tutti i tasti e i pulsanti premuti. Il cursore torna sul PC fisico. |

### Sicurezza
- **Chiave di gruppo:** 160 bit casuali.
- **Handshake:** ECDH P-256 effimero, chiavi derivate con HKDF usando la chiave di gruppo come salt, conferma reciproca con HMAC. Garantisce forward secrecy.
- **Traffico:** cifrato con AES-256-GCM, contatore come nonce, una chiave per direzione.
- **Beacon UDP:** firmati con HMAC. Gli estranei li ignorano.
- **Configurazione:** `%ProgramData%\Varco\config.json`, con ACL ristretta a SYSTEM e amministratori.

### Porte
- TCP 47800: connessioni
- UDP 47801: discovery

La discovery funziona tra cavo e Wi-Fi se sono sulla stessa sottorete. Altrimenti si aggiunge l'indirizzo IP a mano.

## Sviluppo

```powershell
dotnet build Varco.slnx
dotnet test tests/Varco.Tests
```

Due agent sullo stesso PC, solo rete e senza input:

```powershell
$exe = "src\Varco.Agent\bin\Debug\net10.0-windows\Varco.Agent.exe"
& $exe --console --config .\dev\A --port 48800 --discovery-port 0 --no-input --pipe Varco.Dev.A
& $exe --console --config .\dev\B --port 48810 --discovery-port 0 --no-input --pipe Varco.Dev.B
```

Mettere la stessa `GroupKey` nei due `config.json` e in A `"ManualAddresses": ["127.0.0.1:48810"]`.
Lanciato a mano come utente normale, l'agent funziona solo sul desktop normale: per la schermata di blocco serve il servizio.

Log: `%ProgramData%\Varco\logs\agent.log` e `service.log`.

## Limiti noti e prossimi passi
- **Ctrl+Alt+Canc** non si può intercettare: agisce sempre sul PC fisico. Inviarlo al remoto richiede `SendSAS` con la policy *SoftwareSASGeneration*.
- Sul PC che comanda il cursore resta parcheggiato al centro dello schermo. Si potrebbe nasconderlo.
- Fase 2: appunti (testo e immagini), editor della disposizione a griglia (sopra/sotto), coppia di PC con PIN invece della chiave da copiare, Wake-on-LAN.
- Rimozione di un PC dal gruppo: oggi si fa creando una nuova chiave.
