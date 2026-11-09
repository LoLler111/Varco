<img src="assets/varco.svg" width="96" alt="Logo di Varco: due schermi separati da un varco a V attraversato dal cursore">

# Varco

Un solo mouse e una sola tastiera per 2–4 PC Windows sulla stessa rete locale, via cavo o Wi-Fi.
Funziona come Mouse Without Borders, ma con un obiettivo preciso: **quando la connessione cade, si ricollega da sola**.
Ci riesce dopo uno standby, un cambio di rete, un riavvio o un cavo staccato, senza "Refresh connections".

## Come si usa

1. Apri `Varco.exe` con un doppio clic e premi **Installa**. Windows chiede una conferma, poi basta.
2. Fai lo stesso sull'altro PC.
3. I PC si trovano da soli. Nella finestra di Varco l'altro PC compare sotto *PC trovati nella rete*: premi **Collega**.
4. Sull'altro PC compare una richiesta con un codice di 6 cifre. Se è uguale a quello che vedi, premi **Accetta**. Fatto.
5. Se i PC sulla scrivania sono in ordine diverso, trascina gli schermi nella finestra di Varco.

- **Ctrl+Alt+F12** riporta sempre mouse e tastiera sul PC fisico (via di fuga).
- Se tocchi il mouse o la tastiera del PC comandato, il controllo passa a quel PC.
- Per aggiornare: apri il nuovo `Varco.exe` e premi **Aggiorna**. I PC già collegati restano collegati.
- Per disinstallare: *Impostazioni → App → App installate → Varco → Disinstalla*.

`Varco.exe` si crea con `scripts\publish.ps1` (un solo file di circa 50 MB, .NET incluso).

### Cosa fa il setup
- Copia `Varco.exe` in `C:\Program Files\Varco`.
- Registra il servizio *Varco* (avvio automatico, riavvio se cade).
- Crea una regola del firewall per Varco valida su tutti i tipi di rete.
- Avvia l'icona a ogni accesso e aggiunge Varco al menu Start e ad *App installate*.

### Collegamento fra PC
Ogni PC nasce con una propria chiave di gruppo. Quando ne colleghi due:
1. Si scambiano chiavi effimere. Chi avvia si impegna prima sulla propria chiave pubblica (commitment), così nessuno può mettersi in mezzo e far coincidere i codici.
2. Entrambi mostrano lo stesso codice di 6 cifre, derivato da tutto lo scambio.
3. Il PC che riceve la richiesta deve premere **Accetta**: nessuno può controllare il tuo PC senza il tuo consenso.
4. Sul canale cifrato, il gruppo con più PC passa la sua chiave all'altro. Da quel momento si riconnettono da soli per sempre.

## Architettura

Un solo file, `Varco.exe`, con più ruoli:

```
Varco.exe              → setup (se non è installato) oppure icona nella tray
Varco.exe --service    → servizio Windows (LocalSystem): tiene vivo l'agent
Varco.exe --agent      → rete + input, come SYSTEM nella sessione dell'utente
                          (funziona anche su schermata di blocco, UAC, Ctrl+Alt+Canc)
Varco.exe --uninstall  → disinstallazione
```

```
┌──────────── PC ───────────────────────────────────────────────────────┐
│ Varco.exe --service   (sessione 0)                                     │
│   └─ avvia e sorveglia ▼  (CreateProcessAsUser nella sessione attiva)  │
│ Varco.exe --agent     (SYSTEM, sessione dell'utente)                   │
│   ├─ WindowsInput: hook LL + SendInput, segue il desktop attivo        │
│   ├─ InputCoordinator: Local / Controlling / Controlled               │
│   ├─ NetworkNode: discovery UDP, TCP cifrato, heartbeat, riconnessione │
│   ├─ Abbinamento: richieste in arrivo, codice di conferma              │
│   └─ pipe "Varco.Control" ◄── Varco.exe (icona e finestra, utente)     │
└───────────────────────────────────────────────────────────────────────┘
```

| Progetto | Contenuto |
|---|---|
| `src/Varco.Core` | Rete, cifratura, abbinamento, protocollo, layout, logica dell'input. Non dipende da Windows ed è coperto dai test. |
| `src/Varco.Agent` | Parti Win32: hook, iniezione, cambio di desktop, notifiche di standby, pipe di controllo. |
| `src/Varco.Service` | Servizio che tiene l'agent vivo nella sessione della console. |
| `src/Varco.App` | `Varco.exe`: setup, icona nella tray, finestre (WinForms) e smistamento dei ruoli. |
| `tests/Varco.Tests` | Test su crittografia, riconnessione e abbinamento reali su loopback, geometria, layout e coordinatore. |
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
- **Chiave di gruppo:** 160 bit casuali, creata automaticamente e mai mostrata all'utente.
- **Handshake:** ECDH P-256 effimero, chiavi derivate con HKDF usando la chiave di gruppo come salt, conferma reciproca con HMAC. Garantisce forward secrecy.
- **Traffico:** cifrato con AES-256-GCM, contatore come nonce, una chiave per direzione.
- **Beacon UDP:** quelli del gruppo sono firmati con HMAC e gli estranei li ignorano. Esiste anche un annuncio pubblico (ID, nome, porta), che serve solo a far comparire il PC nella lista *PC trovati*.
- **Configurazione:** `%ProgramData%\Varco\config.json`, con ACL ristretta a SYSTEM e amministratori.

### Porte
- TCP 47800: connessioni
- UDP 47801: rilevamento

Il rilevamento funziona tra cavo e Wi-Fi se i PC sono sulla stessa rete (stessa sottorete). Altrimenti, in *Avanzate*, si collega un PC tramite indirizzo IP.

## Sviluppo

```powershell
dotnet build Varco.slnx
dotnet test tests/Varco.Tests
```

Due agent sullo stesso PC, senza mouse e tastiera, più la finestra collegata al primo:

```powershell
$exe = "src\Varco.App\bin\Debug\net10.0-windows\Varco.exe"
& $exe --agent --console --no-input --config .\dev\A --port 48800
& $exe --agent --console --no-input --config .\dev\B --port 48810 --pipe Varco.Dev.B
& $exe --tray
```

I due agent si trovano da soli e il secondo compare in *PC trovati nella rete*.
Lanciato a mano come utente normale, l'agent funziona solo sul desktop normale: per la schermata di blocco serve il servizio.
Log: `%ProgramData%\Varco\logs\` (`agent.log`, `service.log`, `setup.log`).

### Logo e icone
Il disegno vive in `src/Varco.App/VarcoIcon.cs`, su una griglia 128×128 identica a `assets/varco.svg`. Sotto i 32 px si usa la versione senza cursore, `assets/varco-small.svg`.
Dopo aver modificato il disegno, rigenera `assets/varco.ico` e le anteprime:

```powershell
dotnet run --project tools/Varco.IconGen
```

## Limiti noti e prossimi passi
- **Ctrl+Alt+Canc** non si può intercettare: agisce sempre sul PC fisico. Inviarlo al remoto richiede `SendSAS` con la policy *SoftwareSASGeneration*.
- Sul PC che comanda il cursore resta parcheggiato al centro dello schermo. Si potrebbe nasconderlo.
- Fase 2: appunti (testo e immagini), disposizione a griglia (sopra/sotto), Wake-on-LAN.
- Un PC rimosso con *Rimuovi* conosce ancora la vecchia chiave: se torna acceso, si ricollega. Per escluderlo del tutto, su quel PC usa *Avanzate → Scollega questo PC da tutti* oppure disinstalla Varco.
