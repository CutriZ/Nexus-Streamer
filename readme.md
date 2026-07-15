# Nexus Streamer

**Nexus Streamer** è un programma per **Windows** che serve a **trasmettere in diretta** (streaming) e a **registrare video**. È simile a OBS Studio, il programma che usano gli streamer e le TV online.

In parole povere: è come una **piccola regia televisiva** dentro il computer. Prendi quello che vuoi mostrare (lo schermo, una webcam, un video, un'immagine…), lo componi come vuoi, e lo mandi in diretta su internet o lo salvi come file.

---

## A cosa serve

- **Fare dirette** su piattaforme come YouTube, Twitch, o su un server/TV.
- **Registrare** quello che succede sullo schermo o dalla webcam.
- **Mettere insieme più cose** nella stessa schermata: video, webcam, scritte, loghi, immagini.
- **Gestire l'audio**: microfono, audio del computer, audio dei video, con volumi separati.
- **Cambiare "scena"** al volo, come cambia inquadratura una regia TV.

---

## Le "sorgenti": cosa puoi mostrare

Una **sorgente** è una cosa che vuoi far vedere. Puoi metterne quante vuoi, una sopra l'altra (come dei fogli impilati: quello in cima copre quelli sotto).

- **Schermo** – tutto quello che vedi sul monitor
- **Finestra** – una singola finestra di un programma (es. solo il browser)
- **Webcam / scheda di acquisizione** – telecamere, webcam USB, schede video
- **Video o musica** – file (MP4, MKV…) o flussi online (es. un canale internet)
- **Immagine** – foto, loghi, PNG/JPG
- **Testo** – scritte personalizzate
- **Colore** – un riquadro colorato (sfondo o segnaposto)
- **Solo audio** – prende solo l'audio da un dispositivo, senza video
- **NDI** – riceve video da altri programmi/computer sulla stessa rete

Ogni sorgente si può **spostare, ingrandire, ridurre** trascinandola col mouse nell'anteprima, proprio come sposti una foto in un editor.

---

## Le "scene": come le TV

Una **scena** è una combinazione di sorgenti pronta all'uso (es. "webcam grande + logo in basso"). Puoi crearne tante e **passare da una all'altra** con un clic, con effetti di transizione:

- **Cut** – cambio immediato
- **Fade** – dissolvenza morbida
- **Slide** – scorrimento

C'è anche la **modalità Studio**: prepari la scena successiva "dietro le quinte" e la mandi in onda solo quando sei pronto, come fanno le regie vere.

---

## Effetti sulle sorgenti

- **Trasparenza** – rendere una sorgente più o meno visibile
- **Chroma key** – togliere lo sfondo verde/blu (l'effetto "green screen")

Le modifiche si vedono **subito**, in tempo reale.

---

## Audio

Puoi mixare più fonti audio insieme:

- **Microfono**
- **Audio del computer** (quello che esce dalle casse)
- **Audio dei video/webcam** che stai usando

Ogni fonte ha la sua **barretta col volume**, un indicatore che si muove col suono (VU-meter) e un tasto **Muto**. Tutto viene unito in un audio unico per la diretta o la registrazione.

---

## Registrare

Premi **Registra** e il programma salva un file video (MP4, MKV o FLV) sul computer.
Usa la **scheda video NVIDIA** (se presente) per non appesantire il computer, altrimenti usa il processore.

---

## Fare una diretta (streaming)

Nelle **Impostazioni → Stream** scegli come mandare il video:

- **RTMP** – il modo classico per YouTube, Twitch e simili. Inserisci l'indirizzo del server e la "chiave stream".
- **SRT** – un modo più professionale/robusto, usato per TV e piattaforme come Restreamer. Puoi inserire indirizzo e porta, oppure incollare l'**indirizzo completo** che ti dà la piattaforma.

> **Nota importante su SRT:** alcune piattaforme (es. Restreamer) chiedono un codice speciale nell'indirizzo (lo *streamid* con un *token*). Se metti solo indirizzo e porta senza quel codice, il server **rifiuta** la connessione e vedrai un errore. In quel caso usa il campo **"URL SRT completo"** e incolla tutto l'indirizzo che ti ha dato la piattaforma.

---

## Le pubblicità automatiche (SCTE-35)

Questa è una funzione da **TV professionale**. Serve a inserire nel flusso dei **segnali invisibili** che dicono "qui parte la pubblicità" e "qui finisce". Le piattaforme TV (es. Samsung TV Plus) usano questi segnali per infilare gli spot al momento giusto.

Come funziona in pratica: il programma legge un **file del palinsesto** (la scaletta della giornata, con orari e durata delle pubblicità) e, per ogni stacco pubblicitario, inserisce automaticamente i segnali nel flusso — **in anticipo** rispetto allo stacco e **ripetuti**, come spiegato qui sotto. Si attiva in **Impostazioni → Stream** (solo con SRT), spuntando la casella e indicando il file del palinsesto.

**Ogni quanto e quante volte parte il segnale?**

Il programma usa un modello **professionale con anticipo e ripetizioni**, quello che si aspettano le piattaforme che inseriscono pubblicità lato server (SSAI). Funziona così, per ogni stacco pubblicitario:

- **Inizio pubblicità (Cue-Out):** il segnale viene mandato **in anticipo** e **3 volte** — a **8, 5 e 2 secondi prima** dello stacco. L'anticipo serve alla piattaforma a valle per prepararsi e recuperare gli spot in tempo; le 3 ripetizioni servono in caso la rete perda qualche pacchetto (così almeno uno arriva).
- **Fine pubblicità (Cue-In):** allo stesso modo, il segnale di rientro al programma viene mandato **3 volte**, a **8, 5 e 2 secondi prima** della fine dello stacco.
- Ogni segnale non dice "adesso", ma **indica il fotogramma futuro esatto** in cui avverrà la transizione (così la piattaforma sa al millisecondo dove tagliare). Le 3 ripetizioni sono **identiche** e puntano tutte allo stesso punto, così il ricevitore capisce che sono lo stesso evento e non lo esegue tre volte.
- **Il "cartello" di fondo** che avvisa il ricevitore dell'esistenza del canale dei segnali viene invece ripetuto **in continuazione, circa 10 volte al secondo**, così chi si collega in qualsiasi momento sa subito dove guardare.

In sintesi: **ogni pubblicità è annunciata con 8 secondi di anticipo e ribadita 3 volte**, sia all'inizio che alla fine. È il comportamento richiesto dalle TV/piattaforme professionali.

**E nelle dirette lunghissime?** I segnali usano un "orologio" interno che, per come è fatto lo standard TV, arriva a un valore massimo e poi **si azzera e riparte da zero** — succede ogni **~26 ore e mezza** di diretta continua. Il programma **gestisce correttamente questo azzeramento**: anche in una diretta che dura giorni, i marker restano sempre agganciati al fotogramma giusto, senza sfasarsi.

---

## Inviare/ricevere video sulla rete (NDI)

Con **NDI** puoi mandare il tuo video ad altri programmi (vMix, OBS, TriCaster…) sulla stessa rete, o riceverne da loro, **senza cavi video** — tutto passa dalla rete.

> Per usare NDI serve installare a parte il **runtime NDI** (gratuito, dal sito NDI). Senza, il resto del programma funziona comunque.

---

## Cosa vedi nel programma

- **Anteprima** grande al centro: quello che stai per mandare in onda
- **Pannelli** spostabili intorno: elenco scene, elenco sorgenti, mixer audio, transizioni, proprietà
- **Barra di stato**: da quanto tempo sei in diretta, da quanto registri, quanti fotogrammi al secondo, quanto usi il computer e la rete
- Tema **scuro** e interfaccia in **5 lingue** (italiano, inglese, spagnolo, francese, tedesco), cambiabile al volo

Il programma **salva da solo** il tuo lavoro (scene, sorgenti, impostazioni) e lo ricarica alla riapertura.

---

## Cosa serve per usarlo

- **Windows 10** (versione 1903 o più recente) o Windows 11
- Una **scheda grafica** recente (per NVIDIA: encoding video accelerato)
- Nessuna installazione complicata: c'è un **installer** che mette tutto a posto e crea i collegamenti

---

## Come è fatto (per curiosi)

- Scritto da zero in **C#** con **.NET 9** (tecnologia Microsoft).
- La grafica (unire le sorgenti) gira sulla **scheda video** per essere veloce e fluida.
- Video e audio sono gestiti con **FFmpeg**, il "motore" open source usato da mezzo mondo per elaborare i media.
- Non è basato su OBS: è un programma **indipendente**, costruito su misura.

---

## Cosa manca ancora

Alcune cose non ci sono (per ora):

- Sorgente **browser/chat** (overlay web tipo Streamlabs)
- **Webcam virtuale** vera (per usare Nexus come "telecamera" in altri programmi)
- Transizioni **stinger** (animazioni personalizzate)
- Effetti audio avanzati (riduzione rumore, compressore) e scorciatoie da tastiera globali
