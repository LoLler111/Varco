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
- **Appunti condivisi:** copi su un PC e incolli su un altro. Passano testo, testo formattato (HTML, RTF) e immagini. I file no. Restano fuori anche i contenuti che i gestori di password segnano come privati. Funziona tra PC con Varco 0.5 o successivo e si può spegnere in *Avanzate*.
- **Aggiornamenti automatici:** Varco controlla le [release su GitHub](https://github.com/LoLler111/Varco/releases) qualche minuto dopo l'avvio e poi ogni 6 ore circa. Le nuove versioni si installano da sole, senza conferme, appena nessun PC sta usando mouse e tastiera di un altro. Si possono spegnere, o forzare un controllo, in *Avanzate*.
- Per aggiornare a mano: apri il nuovo `Varco.exe` e premi **Aggiorna**. I PC già collegati restano collegati.
- Per disinstallare: *Impostazioni → App → App installate → Varco → Disinstalla*.

`Varco.exe` si scarica dalle [release](https://github.com/LoLler111/Varco/releases/latest) oppure si crea con `scripts\publish.ps1` (un solo file di circa 50 MB, .NET incluso).

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
│   ├─ WindowsClipboard + ClipboardSync: appunti condivisi              │
│   ├─ NetworkNode: discovery UDP, TCP cifrato, heartbeat, riconnessione │
│   ├─ Abbinamento: richieste in arrivo, codice di conferma              │
│   └─ pipe "Varco.Control" ◄── Varco.exe (icona e finestra, utente)     │
└───────────────────────────────────────────────────────────────────────┘
```

| Progetto | Contenuto |
|---|---|
| `src/Varco.Core` | Rete, cifratura, abbinamento, protocollo, layout, logica dell'input. Non dipende da Windows ed è coperto dai test. |
| `src/Varco.Agent` | Parti Win32: hook, iniezione, cambio di desktop, appunti, notifiche di standby, pipe di controllo. |
| `src/Varco.Service` | Servizio che tiene l'agent vivo nella sessione della console. |
| `src/Varco.App` | `Varco.exe`: setup, icona nella tray, finestre (WinForms) e smistamento dei ruoli. |
| `tests/Varco.Tests` | Test su crittografia, riconnessione e abbinamento reali su loopback, geometria, layout, coordinatore, appunti condivisi e aggiornamenti firmati. |
| `tools/Varco.Release` | Chiave, firma e verifica delle release (usato da `scripts/release.ps1`). |
### Riconnessione: perché qui non si blocca

| Problema di Mouse Without Borders | Soluzione in Varco |
|---|---|
| Trova i PC per nome (DNS/NetBIOS) | Ogni PC ha un GUID. Lo trova tramite beacon UDP firmati, sugli ultimi indirizzi riusciti salvati e sugli indirizzi manuali. |
| Socket "mezzi aperti" dopo standby o cavo staccato | Heartbeat applicativo ogni 500 ms: dopo 3 s di silenzio la connessione è chiusa (il valore tollera i picchi del Wi-Fi). |
| Nessuno ritenta da solo | Per ogni PC c'è un ciclo che ritenta. L'attesa cresce da 0,5 s a un massimo di 5 s e si azzera a ogni segnale: beacon, cambio di rete, risveglio, caduta della connessione. |
| Dipende da chi torna online per primo | Entrambi i lati compongono. Se nascono due connessioni, la scelta è deterministica: nella finestra di gara vince quella aperta dall'ID più basso, altrimenti la più recente. |
| Socket rimasti vecchi dopo lo standby | Al risveglio tutte le connessioni vengono chiuse e riaperte. |
| Il firewall blocca dopo che la rete diventa "Pubblica" | Le regole del firewall valgono per tutti i profili. |
| Il router non inoltra i broadcast tra cavo e Wi-Fi | Ogni 30 s (e dopo un cambio di rete) Varco annuncia sé stesso in unicast a ogni indirizzo della sottorete, e chi riceve un annuncio risponde direttamente. Basta che passi in una direzione. |
| Tasti incastrati quando cade la connessione | Il PC comandato rilascia tutti i tasti e i pulsanti premuti. Il cursore torna sul PC fisico. |

### Appunti
- Una finestra invisibile dell'agent riceve `WM_CLIPBOARDUPDATE`. Gli appunti sono della window station (`WinSta0`), quindi funzionano anche mentre l'input passa alla schermata di blocco.
- Si condividono `CF_UNICODETEXT`, `HTML Format`, `Rich Text Format`, `PNG` e `CF_DIB`, compressi con Brotli. Il limite è 96 MB non compressi.
- I dati viaggiano a pezzi da 32 KB in una coda a bassa priorità della connessione: fra un pezzo e l'altro passano mouse e tastiera, così un'immagine copiata non fa scattare il cursore.
- **Compatibilità:** ogni PC annuncia le proprie funzioni con un messaggio `Capabilities` appena connesso. Le versioni fino alla 0.4.0 non lo mandano, e quindi non ricevono appunti. Ignorano comunque i messaggi che non conoscono.
- **Niente rimbalzi:** le scritture fatte da Varco non vengono rimandate indietro. Un'impronta SHA-256 evita di rispedire un contenuto appena ricevuto.
- **Chi invia:** non manda gli appunti un PC che sta comandando un altro. In quel momento le copie locali arrivano da programmi, non dall'utente.
- **Privacy:** si rispettano i formati `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore` e `CanUploadToCloudClipboard = 0`, usati dai gestori di password.

### Sicurezza
- **Chiave di gruppo:** 160 bit casuali, creata automaticamente e mai mostrata all'utente.
- **Handshake:** ECDH P-256 effimero, chiavi derivate con HKDF usando la chiave di gruppo come salt, conferma reciproca con HMAC. Garantisce forward secrecy.
- **Traffico:** cifrato con AES-256-GCM, contatore come nonce, una chiave per direzione.
- **Beacon UDP:** quelli del gruppo sono firmati con HMAC e gli estranei li ignorano. Esiste anche un annuncio pubblico (ID, nome, porta), che serve solo a far comparire il PC nella lista *PC trovati*.
- **Configurazione:** `%ProgramData%\Varco\config.json`, con ACL ristretta a SYSTEM e amministratori.
- **Aggiornamenti:** ogni release contiene `Varco.exe` e `Varco.exe.sig`. Il `.sig` riporta versione, SHA-256 e dimensione dell'exe ed è firmato con ECDSA P-256. La chiave pubblica è dentro Varco, la privata resta sul PC di chi pubblica. L'agent (SYSTEM) scarica in `%ProgramData%\Varco\updates`, una cartella accessibile solo a SYSTEM e amministratori, ricontrolla firma e hash subito prima di avviare `Varco.exe --install --update` e non accetta mai versioni uguali o più vecchie. Un exe non firmato con quella chiave non viene installato, nemmeno se qualcuno riesce a caricarlo nella pagina delle release.

### Come avviene un aggiornamento
1. L'agent trova una versione più recente, la scarica e la verifica. Se in quel momento stai usando un altro PC, aspetta e riprova ogni minuto.
2. Avvia il nuovo exe con `--install --update`. Il nuovo exe ferma il servizio, sposta da parte l'exe in uso (`Varco.old-*.exe`, che un'icona aperta tiene bloccato), copia quello nuovo e riavvia il servizio. Se qualcosa va storto rimette a posto il vecchio e riavvia comunque il servizio.
3. L'icona nella tray si accorge che l'agent è più nuovo, avvia la nuova versione e si chiude. Compare la notifica *Varco aggiornato*.

I PC del gruppo si aggiornano ognuno per conto proprio, quindi per qualche ora possono avere versioni diverse: il protocollo di rete deve restare compatibile tra una versione e la successiva.

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

### Pubblicare una nuova versione

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 0.4.0 -Notes "Cosa cambia"
```

Lo script vuole il working tree pulito, lancia i test, crea `Varco.exe` con quella versione, lo firma con `%USERPROFILE%\.varco\varco-update-key.pem` e crea il tag e la release su GitHub con `Varco.exe` e `Varco.exe.sig`.
**Conserva una copia della chiave privata in un posto sicuro**: senza quella chiave i PC già installati non accettano più aggiornamenti e andrebbero reinstallati a mano con una versione che contiene una chiave nuova.
`tools/Varco.Release` crea la chiave (`keygen`), firma (`sign`) e verifica (`verify`).
GitHub Actions (`.github/workflows/build.yml`) compila e lancia i test a ogni push, ma non firma nulla.

Per provare un aggiornamento senza GitHub: in `%ProgramData%\Varco\config.json` imposta `"UpdateFeedUrl"` su un server locale che risponde come l'API delle release di GitHub.

### Logo e icone
Il disegno vive in `src/Varco.App/VarcoIcon.cs`, su una griglia 128×128 identica a `assets/varco.svg`. Sotto i 32 px si usa la versione senza cursore, `assets/varco-small.svg`.
Dopo aver modificato il disegno, rigenera `assets/varco.ico` e le anteprime:

```powershell
dotnet run --project tools/Varco.IconGen
```

## Limiti noti e prossimi passi
- **Ctrl+Alt+Canc** non si può intercettare: agisce sempre sul PC fisico. Inviarlo al remoto richiede `SendSAS` con la policy *SoftwareSASGeneration*.
- Sul PC che comanda il cursore resta parcheggiato al centro dello schermo. Si potrebbe nasconderlo.
- Appunti: i file copiati in Esplora risorse non passano (servirebbe trasferirne il contenuto).
- Wake-on-LAN.
- Un PC rimosso con *Rimuovi* conosce ancora la vecchia chiave: se torna acceso, si ricollega. Per escluderlo del tutto, su quel PC usa *Avanzate → Scollega questo PC da tutti* oppure disinstalla Varco.
