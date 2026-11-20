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
- **Ctrl+Alt+Canc** mentre comandi un altro PC va a quel PC: schermata di sicurezza, accesso dalla schermata di blocco, Gestione attività. Sul tuo PC la schermata di sicurezza compare per un attimo e Varco la chiude da solo. Funziona anche **Ctrl+Alt+Fine**, come in Desktop remoto, senza quel lampo. Serve Varco 0.6 sul PC comandato.
- Quando spegni o riavvii un PC, Varco è l'ultimo a chiudersi: se compare la schermata **Chiusura app in corso** puoi premere *Arresta comunque* o *Annulla* da un altro PC. Varco si chiude solo dopo, e gli altri PC riprendono subito mouse e tastiera. Per provarlo serve un'app che blocchi davvero lo spegnimento: il Blocco note di Windows 11 salva da solo e non lo fa. C'è `scripts\prova-arresto.ps1`, una finestra che lo blocca finché è aperta.
- Mentre comandi un altro PC, il cursore del tuo **sparisce**: resta fermo al centro dello schermo, invisibile, e ricompare quando torni. Quando torni (o passi a un terzo PC) sparisce anche il cursore rimasto sul PC che comandavi, e ricompare appena qualcuno muove il suo mouse o torni a comandarlo. Per questo secondo caso serve Varco 0.6.1 sul PC comandato.
- Se tocchi il mouse o la tastiera del PC comandato, il controllo passa a quel PC. Non contano i programmi che simulano mouse e tastiera, come Desktop remoto o la sessione avanzata di Hyper-V aperti sul PC comandato.
- **Bordi meno sensibili:** negli angoli di ogni monitor non si passa all'altro PC, così la X di una finestra massimizzata non ti porta via per sbaglio. Con **Bloc Scorr** acceso il cursore resta sul PC dove si trova. Mentre un'app a schermo intero (gioco, presentazione) è in primo piano sul PC che stai usando, il bordo non fa passare. Se vuoi, in *Avanzate* puoi anche chiedere una breve sosta contro il bordo prima di passare. Ogni regola si spegne in *Avanzate*, e ogni PC usa le proprie per i propri bordi.
- Mentre comandi un altro PC, i **monitor del tuo PC vanno in standby** dopo il tempo di spegnimento dello schermo impostato in Windows, e si riaccendono quando torni (qualche secondo, per alcuni monitor). Serve un monitor esterno con DDC/CI attivo; un video o una presentazione in corso li tengono accesi. Il PC comandato invece si spegne da solo, con i tempi di Windows, quando non lo usi. Si può disattivare in *Avanzate*.
- **Bloc Maiusc, Bloc Num e Bloc Scorr** seguono la tastiera che stai usando: quando passi a un altro PC, lui prende lo stato del tuo, e se li premi mentre lo comandi cambiano su tutti e due. Così le spie della tastiera sono sempre giuste. Serve Varco 0.5.2 su tutti e due i PC.
- **Appunti condivisi:** copi su un PC e incolli su un altro. Passano testo, testo formattato (HTML, RTF), immagini e **file e cartelle** copiati in Esplora risorse. I file passano solo quando li incolli, con la barra di avanzamento di Windows, e non hanno limiti di dimensione. Restano fuori i contenuti che i gestori di password segnano come privati. Testo e immagini richiedono Varco 0.5 su tutti e due i PC, i file Varco 0.5.1. Si può spegnere tutto in *Avanzate*.
- **Trascinare file da uno schermo all'altro:** prendi file o cartelle in Esplora risorse e portali oltre il bordo, verso l'altro PC, tenendo premuto il pulsante. Il cursore passa con i file al seguito; rilasciali sopra una cartella o sul desktop e Windows li copia, con la sua barra di avanzamento. I file sul PC di partenza non si toccano mai (è sempre una copia). Serve Varco 0.6 su tutti e due i PC e gli appunti condivisi attivi.
- Si trascinano anche **testo e immagini**, per esempio da un browser o da Word: rilasciali in un documento, in una chat o sul desktop (un'immagine diventa un file). Passano fino a circa 900 KB compressi; per cose più grandi usa copia e incolla. Serve Varco 0.7.1 su tutti e due i PC.
- **Diagnostica** (in *Avanzate*): per ogni PC latenza, cavo o Wi-Fi, indirizzi in uso, cadute nelle ultime 24 ore e motivo dell'ultima. **Esporta log** crea uno zip con i log e la configurazione senza la chiave di gruppo, da allegare a una segnalazione.
- **Rimuovi** (per un PC spento) cambia la chiave del gruppo: il PC rimosso, se si riaccende, non si ricollega più. Gli altri PC ricevono la chiave nuova, anche quelli spenti in quel momento, appena si riaccendono. Un PC rimosso si ricollega con **Collega**, come la prima volta.
- **Aggiornamenti automatici:** Varco controlla le [release su GitHub](https://github.com/LoLler111/Varco/releases) qualche minuto dopo l'avvio e poi ogni 6 ore circa. Le nuove versioni si installano da sole, senza conferme, appena nessun PC sta usando mouse e tastiera di un altro. Se non vuoi aspettare, **Aggiorna subito** installa la nuova versione su tutti i PC collegati. Si possono spegnere, o forzare un controllo, in *Avanzate*.
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

### Rimuovere un PC
Chi conosce la chiave di gruppo fa parte del gruppo, quindi «Rimuovi» cambia la chiave:
1. Il PC dove premi «Rimuovi» crea una chiave nuova e la manda (`GroupKeyChange`, sul canale cifrato) ai PC collegati, che la adottano e dimenticano il PC rimosso.
2. La chiave vecchia resta valida, ma solo per i PC del gruppo che in quel momento erano spenti (elenco *in attesa*, in `RetiredKeys` nella configurazione). Quando uno di loro si riaccende si presenta con la chiave vecchia: l'altro lato la accetta perché il suo ID è in elenco, gli passa quella nuova e lo toglie dall'elenco.
3. Il PC rimosso non è in nessun elenco: con la chiave vecchia non passa più.

Un PC del gruppo che non si accende per più di un anno va ricollegato con «Collega». Le versioni precedenti alla 0.6 ignorano il messaggio: restano collegate con la chiave vecchia (sono *in attesa*) e ricevono quella nuova appena si aggiornano. Se due PC scollegati fra loro rimuovono ciascuno un PC diverso, quando si ritrovano vince il cambio più recente.

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
│   ├─ WindowsClipboard + ClipboardSync: appunti e offerte di file      │
│   ├─ FilePipeServer: dati dei file incollati per l'icona              │
│   ├─ NetworkNode: discovery UDP, TCP cifrato, heartbeat, riconnessione │
│   ├─ Abbinamento: richieste in arrivo, codice di conferma              │
│   └─ pipe "Varco.Control" ◄── Varco.exe (icona e file virtuali)        │
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

### File
Funzionano come in Desktop remoto: si copiano in Esplora risorse su un PC e si incollano con Ctrl+V su un altro.
Oppure si trascinano oltre il bordo dello schermo (vedi sotto).

1. Il PC A legge `CF_HDROP` e crea un'**offerta**: nomi relativi, dimensioni e date, anche delle cartelle (al massimo 20.000 elementi). L'offerta viaggia negli appunti come formato `Varco.Files`. Nessun dato viene ancora trasferito.
2. L'agent del PC B passa l'offerta all'icona con un'attesa lunga sulla pipe di controllo (`filesOffer`). L'icona mette negli appunti un `IDataObject` con `FileGroupDescriptorW`, `FileContents` e `Preferred DropEffect` = copia. Deve farlo l'icona perché Esplora risorse, che gira come utente, deve poter chiamare l'oggetto COM, e l'agent è SYSTEM. L'oggetto vive su un thread STA dedicato.
3. Quando l'utente incolla, Esplora risorse chiede un `IStream` per ogni file. L'icona lo legge dalla pipe `Varco.Files` dell'agent. L'agent apre una **connessione TCP dedicata** verso A (marcatore `VRCF`, poi lo stesso handshake e la stessa cifratura delle connessioni normali) e chiede "elemento N dell'offerta X dalla posizione P".
4. A apre il file **con i permessi dell'utente collegato** (`WTSQueryUserToken` e impersonazione). Così un altro PC ottiene solo ciò che quell'utente poteva leggere, e funzionano anche le unità di rete mappate.

Dettagli:
- **Niente limiti di dimensione:** i dati passano a flusso, a pezzi da 64 KB, senza essere caricati in memoria. Si riprende da metà con `IStream::Seek`.
- **Il mouse viene prima:** il file usa una connessione diversa da quella di mouse e tastiera. Se nel frattempo il mouse è in uso verso quel PC e la latenza sale oltre 15 ms, l'invio rallenta.
- **Sicurezza:** chi riceve può chiedere solo gli elementi di un'offerta esistente, mai un percorso. I nomi in arrivo vengono rifiutati se sono assoluti, contengono `..` o caratteri vietati. Se un file cambia durante la copia, l'incolla fallisce invece di lasciare un file troncato.
- **Compatibilità:** i PC con i file annunciano la capacità `Files`. A un PC con la 0.5 arrivano solo gli eventuali altri formati, così non si svuotano i suoi appunti.
- Chiudendo l'icona gli appunti si svuotano. Non si usa mai `OleFlushClipboard`, che scaricherebbe tutti i file.
- **Percorsi lunghi:** nei file virtuali il percorso relativo può avere al massimo 259 caratteri (limite di `FILEDESCRIPTORW`). Per gli elementi più profondi si accorciano i nomi delle cartelle che li contengono, a partire dalla più lunga: *Documentazione tecnica del progetto* diventa *Documentazio~3F2A* (inizio del nome e impronta del percorso completo). Tutto ciò che stava in quella cartella resta insieme, nella cartella accorciata. Resta fuori solo ciò che non ci sta nemmeno così.

#### Trascinamento fra schermi
Esplora risorse gira come utente e non può trascinare file su una finestra di SYSTEM, né riceverli da lei: per questo le due finestre che servono sono dell'icona.
1. Sul PC A il pulsante sinistro è premuto e il cursore spinge contro il bordo verso B. L'agent chiede all'icona (attesa lunga `dragEvents`) di mettere sotto il cursore una finestrella invisibile. Se Esplora risorse sta trascinando dei file, ci passa sopra e l'icona ne legge i percorsi (`dragFiles`). La finestrella non accetta mai il rilascio.
2. L'agent di A crea un'offerta, come per una copia, e al movimento successivo contro il bordo passa a B come sempre, ma con il pulsante ancora premuto. Manda a B `DragFiles` con l'offerta. Su A il trascinamento viene annullato con Esc, poi il pulsante viene rilasciato sopra la finestra che nasconde il cursore: lì non cade niente.
3. L'agent di B passa l'offerta alla sua icona, che mette sotto il cursore un'altra finestrella invisibile e risponde `dragReady`. Nel frattempo i movimenti in arrivo da A si accumulano. Poi l'agent preme il pulsante sopra la finestrella, da cui parte un trascinamento OLE con gli stessi file virtuali dell'incolla, e applica i movimenti accumulati.
4. Il rilascio del pulsante su A arriva a B e lascia cadere i file: Esplora risorse li copia chiedendo i dati ad A, come per l'incolla.

Testo e immagini (da un browser, da Word) seguono la stessa strada. Se nella finestrella non arrivano file, l'icona legge dall'oggetto OLE gli stessi formati degli appunti condivisi (`CF_UNICODETEXT`, `HTML Format`, `Rich Text Format`, `PNG`, `CF_DIB`) e i "file senza percorso" (`FileGroupDescriptorW` + `FileContents`, come le immagini trascinate da Chrome o Edge), fino a 8 MB, e li manda all'agent con `dragData`. Viaggiano nello stesso messaggio `DragFiles`, compressi e con un primo byte (`0xDA`) diverso da quello di un'offerta di file, solo verso i PC che annunciano la capacità `DragData` (0.7.1). Sul PC di arrivo l'icona fa partire il trascinamento da un oggetto OLE in memoria. Se arriva un solo file immagine senza un formato immagine, lo offre anche come PNG e `CF_DIB`.

Se il pulsante viene rilasciato prima che il trascinamento parta, o se la connessione cade, il trascinamento si annulla. L'offerta compressa deve stare sotto i 900 KB (decine di migliaia di nomi): oltre, si usa copia e incolla. I PC con la 0.6 annunciano la capacità `DragFiles`; verso i PC più vecchi non si prova nemmeno.

### Sicurezza
- **Chiave di gruppo:** 160 bit casuali, creata automaticamente e mai mostrata all'utente.
- **Handshake:** ECDH P-256 effimero, chiavi derivate con HKDF usando la chiave di gruppo come salt, conferma reciproca con HMAC. Garantisce forward secrecy.
- **Traffico:** cifrato con AES-256-GCM, contatore come nonce, una chiave per direzione.
- **Beacon UDP:** quelli del gruppo sono firmati con HMAC e gli estranei li ignorano. Esiste anche un annuncio pubblico (ID, nome, porta), che serve solo a far comparire il PC nella lista *PC trovati*.
- **Configurazione:** `%ProgramData%\Varco\config.json`, con ACL ristretta a SYSTEM e amministratori.
- **Aggiornamenti:** ogni release contiene `Varco.exe` e `Varco.exe.sig`. Il `.sig` riporta versione, SHA-256 e dimensione dell'exe ed è firmato con ECDSA P-256. La chiave pubblica è dentro Varco, la privata resta sul PC di chi pubblica. L'agent (SYSTEM) scarica in `%ProgramData%\Varco\updates`, una cartella accessibile solo a SYSTEM e amministratori, ricontrolla firma e hash subito prima di avviare `Varco.exe --install --update` e non accetta mai versioni uguali o più vecchie. Un exe non firmato con quella chiave non viene installato, nemmeno se qualcuno riesce a caricarlo nella pagina delle release.

### Come avviene un aggiornamento
1. L'agent trova una versione più recente, la scarica e la verifica. Se in quel momento stai usando un altro PC, aspetta e riprova ogni minuto. Mentre aspetta, **Aggiorna subito** (nella finestra principale e in *Avanzate*) manda a tutti i PC collegati il messaggio `InstallUpdate`: ognuno installa subito l'ultima release firmata, scaricandola se non l'ha ancora fatto, e il PC da cui è partita la richiesta si aggiorna per ultimo, dopo 3 secondi. Il messaggio sa solo far saltare l'attesa: quello che si installa resta la release firmata su GitHub. Funziona sui PC che hanno già la versione 0.5.6 o successiva.
2. Avvia il nuovo exe con `--install --update`. Il nuovo exe ferma il servizio, sposta da parte l'exe in uso (`Varco.old-*.exe`, che un'icona aperta tiene bloccato), copia quello nuovo e riavvia il servizio. Se qualcosa va storto rimette a posto il vecchio e riavvia comunque il servizio.
3. L'icona nella tray si accorge che l'agent è più nuovo, avvia la nuova versione e si chiude. Compare la notifica *Varco aggiornato*.

I PC del gruppo si aggiornano ognuno per conto proprio, quindi per qualche ora possono avere versioni diverse: il protocollo di rete deve restare compatibile tra una versione e la successiva.

### Ctrl+Alt+Canc
Il vero Ctrl+Alt+Canc non arriva mai agli hook: Windows lo gestisce da sé, sul PC fisico, e apre la schermata di sicurezza sul desktop Winlogon. Mentre si comanda un altro PC l'agent lo riconosce così: il desktop di input passa da `Default` a `Winlogon` mentre Ctrl e Alt sono premuti (o rilasciati da meno di mezzo secondo, perché durante il cambio di desktop nessun hook vede i rilasci) e Win no. AltGr non conta: Windows lo manda come Ctrl sinistro finto (scancode 0x21D) più Alt destro, e finché quel Ctrl finto è giù l'Alt destro non vale come Alt. Così una richiesta UAC che compare mentre si scrive @ non viene scambiata per Ctrl+Alt+Canc. Allora rilascia Ctrl e Alt sul PC comandato, gli manda `SecureAttention` e chiude la schermata locale con un Esc iniettato sul desktop Winlogon. Per accorciare il lampo, mentre si comanda un altro PC con Ctrl e Alt fisici premuti il desktop si controlla ogni 10 ms invece che ogni 100 (al massimo per 5 secondi). Il primo Esc parte 40 ms dopo il cambio di desktop, preceduto dal rilascio iniettato di Ctrl e Alt (altrimenti diventerebbe Ctrl+Alt+Esc). Poi si riprova ogni 60 ms, e dal quinto tentativo ogni 200 ms, finché il desktop torna `Default`, perché la schermata ci mette un attimo a essere pronta. Un Esc partito quando il desktop è già tornato normale non arriva a nessuno: il thread di iniezione è ancora sul desktop Winlogon e `SendInput` fallisce. Nel log restano i tempi: quanto dopo Ctrl+Alt è comparsa la schermata e dopo quanti millisecondi e quanti Esc si è chiusa. Anche **Ctrl+Alt+Fine** manda `SecureAttention`, che il PC comandato accetta solo da chi lo sta comandando. Un programma può simulare Ctrl+Alt+Canc solo con `SendSAS`, e solo se è un servizio e il criterio *SoftwareSASGeneration* lo permette. Per questo lo esegue il servizio Varco: l'agent segnala l'evento `Global\Varco.SAS`, accessibile solo a SYSTEM. All'avvio il servizio aggiunge al criterio il permesso per i servizi (valore 1, lasciando il resto com'era) e la disinstallazione lo toglie. Se un criterio di gruppo aziendale lo vieta, Windows lo rimette e Ctrl+Alt+Fine non fa nulla.

### Cursore nascosto
Mentre si comanda un altro PC, sul PC fisico il cursore resta parcheggiato al centro. Per nasconderlo l'agent mette sotto il cursore una finestrella trasparente (alfa 1/255), sempre in primo piano, che risponde a `WM_SETCURSOR` con «nessun cursore». I cursori di sistema non si toccano: se Varco si chiudesse male, la finestra sparirebbe e il cursore tornerebbe visibile da solo. La finestra vive sul thread degli hook, quindi segue anche la schermata di blocco e l'UAC, e ogni secondo torna in primo piano.

Windows applica «nessun cursore» qualche millisecondo dopo che il cursore è arrivato sopra la finestra: spostandolo subito al centro lo si vedrebbe lì per un attimo. Quindi, quando si passa a un altro PC, la finestra copre per un momento sia il bordo sia il centro: il cursore sparisce sul bordo e solo dopo (10-30 ms) viene portato al centro, senza uscire dalla finestra. In quell'intervallo i movimenti si misurano dal bordo.

La stessa finestra nasconde il cursore del PC comandato quando il controllo passa altrove (`Leave`, o il PC che comandava si scollega): lì il cursore resterebbe fermo sul bordo. Si mette dopo le iniezioni ancora in coda, dove il cursore si è fermato davvero, e sparisce al primo evento del mouse fisico di quel PC, prima di un eventuale clic, oppure quando un altro PC torna a comandarlo.

Se l'agent si chiude o va in crash, Windows distrugge le sue finestre e il cursore ricompare da solo (e il servizio riavvia l'agent). Se si bloccasse senza chiudersi, Windows dopo poco smette di aspettare i suoi hook e il mouse torna a muoversi: basta spostarlo di qualche centimetro per uscire dalla finestrella (64 pixel) e rivedere il cursore.

### Monitor in standby mentre si comanda un altro PC
Windows non spegne mai lo schermo del PC che comanda: l'input fisico che Varco blocca negli hook conta lo stesso come «utente presente». Azzera il timer di inattività e riaccende anche un monitor spento con `SC_MONITORPOWER` (misurato). Quindi l'agent conta da solo: legge «Spegni lo schermo dopo» dal piano di alimentazione attivo (con l'alimentatore o a batteria) e, passato quel tempo da quando il controllo è andato a un altro PC, manda i monitor in standby via DDC/CI (VCP `D6` = 4). Windows non sa nulla di questo standby, quindi i tasti mandati all'altro PC non lo interrompono. Non succede se un programma chiede di tenere acceso lo schermo (`ES_DISPLAY_REQUIRED`, letto con `CallNtPowerInformation(SystemExecutionState)`), né se in Windows lo schermo non si spegne mai.

Quando il controllo torna qui l'agent manda `D6` = 1 e lo ripete, circa una volta al secondo, finché il monitor non risponde «acceso»: alcuni monitor in standby rispondono a singhiozzo e ignorano i primi comandi (misurato: 4-8 tentativi). Mai `D6` = 5, da cui si esce solo col tasto. Finché ci sono monitor in standby esiste `monitor-standby.txt` nella cartella della configurazione: se l'agent si chiude male, quello nuovo li riaccende all'avvio. Le chiamate DDC/CI sono lente e girano su un thread a parte. I monitor senza DDC/CI (lo schermo dei portatili) restano accesi.

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
- **Ctrl+Alt+Canc** vero mentre si comanda un altro PC: Varco lo riconosce dal passaggio del tuo PC alla schermata di sicurezza. Se il tuo PC era già sulla schermata di blocco, quel passaggio non c'è e il Ctrl+Alt+Canc resta lì: usa Ctrl+Alt+Fine.
- File: si incollano o si trascinano in Esplora risorse e nei programmi che accettano file virtuali. Testo e immagini trascinati passano fino a circa 900 KB compressi (un solo messaggio); le immagini WebP arrivano come file, senza la versione PNG per Word o Paint.
- Le cadute della diagnostica si contano da quando l'agent è partito: dopo un riavvio o un aggiornamento ripartono da zero.
- Wake-on-LAN.
