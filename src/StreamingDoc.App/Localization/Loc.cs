using System.ComponentModel;

namespace StreamingDoc.App.Localization;

public enum Lang { It = 0, En = 1, Es = 2, Fr = 3, De = 4 }

/// <summary>
/// Sorgente di binding per le stringhe localizzate. In XAML:
/// <c>Content="{Binding [Settings], Source={StaticResource Loc}}"</c>.
/// Al cambio lingua, <see cref="Refresh"/> notifica "Item[]" e tutti i binding si aggiornano live.
/// </summary>
public sealed class LocProvider : INotifyPropertyChanged
{
    public static LocProvider Instance { get; } = new();
    public string this[string key] => Loc.T(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Localizzazione runtime (live-switch). Italiano = default e fallback.</summary>
public static class Loc
{
    private static Lang _lang = Lang.It;
    public static Lang Current => _lang;

    /// <summary>Scattato dopo il cambio lingua: la finestra aggiorna titoli pannelli e testi dinamici.</summary>
    public static event Action? LanguageChanged;

    public sealed record LangItem(string Code, string Name)
    {
        public override string ToString() => Name; // la casella del ComboBox mostra il nome
    }

    public static readonly LangItem[] Languages =
    {
        new("it", "Italiano"), new("en", "English"), new("es", "Español"), new("fr", "Français"), new("de", "Deutsch"),
    };

    public static string CurrentCode => _lang switch
    { Lang.En => "en", Lang.Es => "es", Lang.Fr => "fr", Lang.De => "de", _ => "it" };

    public static void Set(string? code)
    {
        _lang = code switch { "en" => Lang.En, "es" => Lang.Es, "fr" => Lang.Fr, "de" => Lang.De, _ => Lang.It };
        LocProvider.Instance.Refresh();
        LanguageChanged?.Invoke();
    }

    public static string T(string key)
    {
        if (Table.TryGetValue(key, out var a))
        {
            int i = (int)_lang;
            return i < a.Length && !string.IsNullOrEmpty(a[i]) ? a[i] : a[0];
        }
        return key;
    }

    // key -> [it, en, es, fr, de]
    private static readonly Dictionary<string, string[]> Table = new()
    {
        // Pannelli
        ["Scene"] = new[] { "Scene", "Scenes", "Escenas", "Scènes", "Szenen" },
        ["Sources"] = new[] { "Sorgenti", "Sources", "Fuentes", "Sources", "Quellen" },
        ["Preview"] = new[] { "Anteprima", "Preview", "Vista previa", "Aperçu", "Vorschau" },
        ["Status"] = new[] { "Stato", "Status", "Estado", "État", "Status" },
        ["Controls"] = new[] { "Controlli", "Controls", "Controles", "Contrôles", "Steuerung" },
        ["AudioMixer"] = new[] { "Mixer audio", "Audio Mixer", "Mezclador de audio", "Mixage audio", "Audio-Mixer" },
        ["Transitions"] = new[] { "Transizioni", "Transitions", "Transiciones", "Transitions", "Übergänge" },
        ["SourceProps"] = new[] { "Proprietà sorgente", "Source Properties", "Propiedades de la fuente", "Propriétés de la source", "Quelleneigenschaften" },

        // Controlli (statici + dinamici)
        ["StartStreaming"] = new[] { "Avvia Streaming", "Start Streaming", "Iniciar transmisión", "Démarrer le streaming", "Streaming starten" },
        ["StopStreaming"] = new[] { "■ Ferma Streaming", "■ Stop Streaming", "■ Detener transmisión", "■ Arrêter le streaming", "■ Streaming stoppen" },
        ["StartRecording"] = new[] { "Avvia Registrazione", "Start Recording", "Iniciar grabación", "Démarrer l'enregistrement", "Aufnahme starten" },
        ["StopRecording"] = new[] { "■ Ferma Registrazione", "■ Stop Recording", "■ Detener grabación", "■ Arrêter l'enregistrement", "■ Aufnahme stoppen" },
        ["StartVCam"] = new[] { "Avvia Camera Virtuale", "Start Virtual Camera", "Iniciar cámara virtual", "Démarrer la caméra virtuelle", "Virtuelle Kamera starten" },
        ["StopVCam"] = new[] { "■ Ferma Camera Virtuale", "■ Stop Virtual Camera", "■ Detener cámara virtual", "■ Arrêter la caméra virtuelle", "■ Virtuelle Kamera stoppen" },
        ["Settings"] = new[] { "Impostazioni", "Settings", "Configuración", "Paramètres", "Einstellungen" },
        ["Exit"] = new[] { "Esci", "Exit", "Salir", "Quitter", "Beenden" },
        ["CaptureDesktopAudio"] = new[] { "Cattura audio desktop", "Capture desktop audio", "Capturar audio del escritorio", "Capturer l'audio du bureau", "Desktop-Audio aufnehmen" },

        // Top bar
        ["Offline"] = new[] { "OFFLINE", "OFFLINE", "DESCONECTADO", "HORS LIGNE", "OFFLINE" },
        ["Live"] = new[] { "LIVE", "LIVE", "EN VIVO", "EN DIRECT", "LIVE" },
        ["Untitled"] = new[] { "Senza titolo", "Untitled", "Sin título", "Sans titre", "Unbenannt" },

        // Scene / Sorgenti toolbar
        ["Save"] = new[] { "Salva", "Save", "Guardar", "Enregistrer", "Speichern" },
        ["Load"] = new[] { "Carica", "Load", "Cargar", "Charger", "Laden" },
        ["NewScene"] = new[] { "Nuova scena", "New scene", "Nueva escena", "Nouvelle scène", "Neue Szene" },
        ["RemoveScene"] = new[] { "Rimuovi scena", "Remove scene", "Eliminar escena", "Supprimer la scène", "Szene entfernen" },
        ["Add"] = new[] { "Aggiungi", "Add", "Añadir", "Ajouter", "Hinzufügen" },
        ["Remove"] = new[] { "Rimuovi", "Remove", "Eliminar", "Supprimer", "Entfernen" },
        ["MoveUpFront"] = new[] { "Su (avanti)", "Up (front)", "Subir (frente)", "Monter (avant)", "Hoch (vorne)" },
        ["MoveDownBack"] = new[] { "Giù (dietro)", "Down (back)", "Bajar (atrás)", "Descendre (arrière)", "Runter (hinten)" },
        ["Lock"] = new[] { "Blocca", "Lock", "Bloquear", "Verrouiller", "Sperren" },
        ["Visible"] = new[] { "Visibile", "Visible", "Visible", "Visible", "Sichtbar" },

        // Barra fonte selezionata
        ["NoSourceSelected"] = new[] { "Nessuna fonte selezionata", "No source selected", "Ninguna fuente seleccionada", "Aucune source sélectionnée", "Keine Quelle ausgewählt" },
        ["PropertiesBtn"] = new[] { "⚙ Proprietà", "⚙ Properties", "⚙ Propiedades", "⚙ Propriétés", "⚙ Eigenschaften" },
        ["Filters"] = new[] { "Filtri", "Filters", "Filtros", "Filtres", "Filter" },

        // Transizioni
        ["TransCut"] = new[] { "Taglio", "Cut", "Corte", "Coupe", "Schnitt" },
        ["TransFade"] = new[] { "Dissolvenza", "Fade", "Fundido", "Fondu", "Überblendung" },
        ["TransSlide"] = new[] { "Scorrimento", "Slide", "Deslizar", "Glissement", "Schieben" },
        ["DoTransition"] = new[] { "⇄ Transiziona", "⇄ Transition", "⇄ Transición", "⇄ Transition", "⇄ Übergang" },
        ["StudioMode"] = new[] { "Modalità studio", "Studio mode", "Modo estudio", "Mode studio", "Studio-Modus" },

        // Proprietà sorgente (pannello)
        ["Name"] = new[] { "Nome", "Name", "Nombre", "Nom", "Name" },
        ["Opacity"] = new[] { "Opacità", "Opacity", "Opacidad", "Opacité", "Deckkraft" },
        ["Locked"] = new[] { "Bloccata", "Locked", "Bloqueada", "Verrouillée", "Gesperrt" },
        ["PropertiesDots"] = new[] { "Proprietà…", "Properties…", "Propiedades…", "Propriétés…", "Eigenschaften…" },
        ["FiltersDots"] = new[] { "Filtri…", "Filters…", "Filtros…", "Filtres…", "Filter…" },

        // Tooltip rail
        ["ResetLayout"] = new[] { "Ripristina disposizione pannelli", "Reset panel layout", "Restablecer disposición de paneles", "Réinitialiser la disposition", "Anordnung zurücksetzen" },
        ["Info"] = new[] { "Info", "Info", "Información", "Infos", "Info" },
        ["Menu"] = new[] { "Menu", "Menu", "Menú", "Menu", "Menü" },
        ["StudioModeTip"] = new[] { "Modalità studio", "Studio mode", "Modo estudio", "Mode studio", "Studio-Modus" },
        ["Profile"] = new[] { "Profilo", "Profile", "Perfil", "Profil", "Profil" },
        ["SceneCollection"] = new[] { "Collezione scene", "Scene collection", "Colección de escenas", "Collection de scènes", "Szenensammlung" },

        // Pulsanti comuni
        ["OK"] = new[] { "OK", "OK", "Aceptar", "OK", "OK" },
        ["Cancel"] = new[] { "Annulla", "Cancel", "Cancelar", "Annuler", "Abbrechen" },
        ["Close"] = new[] { "Chiudi", "Close", "Cerrar", "Fermer", "Schließen" },
        ["Browse"] = new[] { "Sfoglia…", "Browse…", "Examinar…", "Parcourir…", "Durchsuchen…" },

        // Impostazioni
        ["Language"] = new[] { "Lingua", "Language", "Idioma", "Langue", "Sprache" },
        ["GeneralTab"] = new[] { "Generale", "General", "General", "Général", "Allgemein" },
        ["Output"] = new[] { "Uscita", "Output", "Salida", "Sortie", "Ausgabe" },
        ["Audio"] = new[] { "Audio", "Audio", "Audio", "Audio", "Audio" },
        ["CanvasRes"] = new[] { "Risoluzione canvas (base)", "Canvas resolution (base)", "Resolución del lienzo (base)", "Résolution du canevas (base)", "Canvas-Auflösung (Basis)" },
        ["ResFpsNote"] = new[] { "Il cambio di risoluzione/FPS ricrea il compositor.", "Changing resolution/FPS recreates the compositor.", "Cambiar resolución/FPS recrea el compositor.", "Changer la résolution/FPS recrée le compositeur.", "Auflösung/FPS-Änderung erstellt den Compositor neu." },
        ["RecFolder"] = new[] { "Cartella registrazioni", "Recording folder", "Carpeta de grabaciones", "Dossier d'enregistrement", "Aufnahmeordner" },
        ["RecFormat"] = new[] { "Formato registrazione", "Recording format", "Formato de grabación", "Format d'enregistrement", "Aufnahmeformat" },
        ["Encoder"] = new[] { "Encoder", "Encoder", "Codificador", "Encodeur", "Encoder" },
        ["EncoderAuto"] = new[] { "Automatico", "Automatic", "Automático", "Automatique", "Automatisch" },
        ["RecBitrate"] = new[] { "Bitrate registrazione (kbps)", "Recording bitrate (kbps)", "Bitrate de grabación (kbps)", "Débit d'enregistrement (kbps)", "Aufnahme-Bitrate (kbps)" },
        ["StreamBitrate"] = new[] { "Bitrate streaming (kbps)", "Streaming bitrate (kbps)", "Bitrate de transmisión (kbps)", "Débit de streaming (kbps)", "Streaming-Bitrate (kbps)" },
        ["RtmpServer"] = new[] { "Server RTMP", "RTMP server", "Servidor RTMP", "Serveur RTMP", "RTMP-Server" },
        ["StreamKey"] = new[] { "Chiave stream", "Stream key", "Clave de transmisión", "Clé de stream", "Stream-Schlüssel" },
        ["Server"] = new[] { "Server", "Server", "Servidor", "Serveur", "Server" },
        ["Show"] = new[] { "Mostra", "Show", "Mostrar", "Afficher", "Anzeigen" },
        ["Hide"] = new[] { "Nascondi", "Hide", "Ocultar", "Masquer", "Verbergen" },
        ["UseAuth"] = new[] { "Usa autenticazione", "Use authentication", "Usar autenticación", "Utiliser l'authentification", "Authentifizierung verwenden" },
        ["Username"] = new[] { "Nome utente", "Username", "Nombre de usuario", "Nom d'utilisateur", "Benutzername" },
        ["Password"] = new[] { "Password", "Password", "Contraseña", "Mot de passe", "Passwort" },
        ["StreamExample"] = new[] { "Es. Twitch: rtmp://live.twitch.tv/app — YouTube: rtmp://a.rtmp.youtube.com/live2", "E.g. Twitch: rtmp://live.twitch.tv/app — YouTube: rtmp://a.rtmp.youtube.com/live2", "Ej. Twitch: rtmp://live.twitch.tv/app — YouTube: rtmp://a.rtmp.youtube.com/live2", "Ex. Twitch: rtmp://live.twitch.tv/app — YouTube: rtmp://a.rtmp.youtube.com/live2", "Z.B. Twitch: rtmp://live.twitch.tv/app — YouTube: rtmp://a.rtmp.youtube.com/live2" },
        ["Protocol"] = new[] { "Protocollo", "Protocol", "Protocolo", "Protocole", "Protokoll" },
        ["ProtoNone"] = new[] { "Non impostato", "Not set", "No configurado", "Non défini", "Nicht festgelegt" },
        ["SrtMode"] = new[] { "Modalità SRT", "SRT mode", "Modo SRT", "Mode SRT", "SRT-Modus" },
        ["SrtCaller"] = new[] { "Caller (connetti)", "Caller (connect)", "Caller (conectar)", "Caller (connexion)", "Caller (verbinden)" },
        ["SrtListener"] = new[] { "Listener (ascolta)", "Listener (listen)", "Listener (escuchar)", "Listener (écoute)", "Listener (lauschen)" },
        ["SrtHost"] = new[] { "Host / IP", "Host / IP", "Host / IP", "Hôte / IP", "Host / IP" },
        ["SrtPort"] = new[] { "Porta", "Port", "Puerto", "Port", "Port" },
        ["SrtLatency"] = new[] { "Latenza (ms)", "Latency (ms)", "Latencia (ms)", "Latence (ms)", "Latenz (ms)" },
        ["SrtNote"] = new[]
        {
            "Caller si connette a un server/decoder SRT. Listener attende una connessione in ingresso (in questa modalità l'avvio resta in attesa finché un peer non si collega).",
            "Caller connects to an SRT server/decoder. Listener waits for an incoming connection (in this mode start-up blocks until a peer connects).",
            "Caller se conecta a un servidor/decodificador SRT. Listener espera una conexión entrante (en este modo el inicio se bloquea hasta que un par se conecta).",
            "Caller se connecte à un serveur/décodeur SRT. Listener attend une connexion entrante (dans ce mode le démarrage attend qu'un pair se connecte).",
            "Caller verbindet sich mit einem SRT-Server/Decoder. Listener wartet auf eine eingehende Verbindung (in diesem Modus blockiert der Start, bis ein Peer verbindet).",
        },
        ["StreamBitrateNote"] = new[] { "Il bitrate di streaming si imposta nel tab Uscita.", "The streaming bitrate is set in the Output tab.", "El bitrate de transmisión se configura en la pestaña Salida.", "Le débit de streaming se règle dans l'onglet Sortie.", "Die Streaming-Bitrate wird im Tab Ausgabe festgelegt." },
        ["MsgNoProtocol"] = new[] { "Nessun protocollo di streaming selezionato.", "No streaming protocol selected.", "Ningún protocolo de transmisión seleccionado.", "Aucun protocole de streaming sélectionné.", "Kein Streaming-Protokoll ausgewählt." },
        ["WarnNoProtocolTitle"] = new[] { "Protocollo non impostato", "Protocol not set", "Protocolo no configurado", "Protocole non défini", "Protokoll nicht festgelegt" },
        ["WarnNoProtocolMsg"] = new[] { "Seleziona un protocollo di streaming (RTMP o SRT) in Impostazioni → Stream.", "Select a streaming protocol (RTMP or SRT) in Settings → Stream.", "Selecciona un protocolo de transmisión (RTMP o SRT) en Configuración → Stream.", "Sélectionnez un protocole de streaming (RTMP ou SRT) dans Paramètres → Stream.", "Wähle ein Streaming-Protokoll (RTMP oder SRT) unter Einstellungen → Stream." },
        ["ScteEnable"] = new[] { "Abilita SCTE-35 (ad marker)", "Enable SCTE-35 (ad markers)", "Habilitar SCTE-35 (marcadores)", "Activer SCTE-35 (marqueurs pub)", "SCTE-35 aktivieren (Werbemarker)" },
        ["SctePlayoutFile"] = new[] { "File trigger del playout", "Playout trigger file", "Archivo trigger del playout", "Fichier trigger du playout", "Playout-Trigger-Datei" },
        ["ScteNote"] = new[]
        {
            "A ogni break il playout aggiorna questo file: l'app inietta uno splice_insert SCTE-35 nel flusso MPEG-TS (solo SRT). Formati letti: trigger Duration/EventID, XMLTV, ProgramGuide. Il break è rilevato da category PROMO / rating Commercial.",
            "On each break the playout updates this file: the app injects an SCTE-35 splice_insert into the MPEG-TS stream (SRT only). Formats read: Duration/EventID trigger, XMLTV, ProgramGuide. Breaks are detected via category PROMO / rating Commercial.",
            "En cada pausa el playout actualiza este archivo: la app inyecta un splice_insert SCTE-35 en el flujo MPEG-TS (solo SRT). Formatos: trigger Duration/EventID, XMLTV, ProgramGuide. Pausas detectadas por category PROMO / rating Commercial.",
            "À chaque pause le playout met à jour ce fichier : l'app injecte un splice_insert SCTE-35 dans le flux MPEG-TS (SRT uniquement). Formats : trigger Duration/EventID, XMLTV, ProgramGuide. Pauses détectées via category PROMO / rating Commercial.",
            "Bei jeder Werbepause aktualisiert das Playout diese Datei: die App injiziert ein SCTE-35 splice_insert in den MPEG-TS-Stream (nur SRT). Formate: Duration/EventID-Trigger, XMLTV, ProgramGuide. Pausen werden über category PROMO / rating Commercial erkannt.",
        },
        ["MsgScteSent"] = new[] { "SCTE-35 inviato: break {0}s {1}", "SCTE-35 sent: {0}s break {1}", "SCTE-35 enviado: pausa {0}s {1}", "SCTE-35 envoyé : pause {0}s {1}", "SCTE-35 gesendet: {0}s Pause {1}" },
        ["MsgScteWatchErr"] = new[] { "Watcher SCTE-35 non avviato: {0}", "SCTE-35 watcher failed: {0}", "Watcher SCTE-35 no iniciado: {0}", "Watcher SCTE-35 non démarré : {0}", "SCTE-35-Watcher fehlgeschlagen: {0}" },
        ["SampleRate"] = new[] { "Frequenza di campionamento", "Sample rate", "Frecuencia de muestreo", "Fréquence d'échantillonnage", "Abtastrate" },
        ["AudioNote"] = new[] { "Audio desktop (loopback) e microfono sono rilevati automaticamente nel mixer. Il sample rate interno è 48 kHz.", "Desktop audio (loopback) and microphone are auto-detected in the mixer. Internal sample rate is 48 kHz.", "El audio del escritorio (loopback) y el micrófono se detectan automáticamente en el mezclador. La frecuencia interna es 48 kHz.", "L'audio du bureau (loopback) et le micro sont détectés automatiquement dans le mixage. La fréquence interne est de 48 kHz.", "Desktop-Audio (Loopback) und Mikrofon werden im Mixer automatisch erkannt. Interne Abtastrate ist 48 kHz." },

        // About
        ["AppTagline"] = new[] { "Studio di produzione e diretta live", "Live production and streaming studio", "Estudio de producción y directo", "Studio de production et de direct", "Produktions- und Livestudio" },
        ["AppDesc"] = new[]
        {
            "Compositing video in tempo reale su scene multiple, con mixer audio indipendente per ogni sorgente. Cattura professionale per regie e playout, sorgenti di rete, registrazione e trasmissione a bassa latenza.",
            "Real-time video compositing across multiple scenes, with an independent audio mixer per source. Professional capture for control rooms and playout, network sources, low-latency recording and streaming.",
            "Composición de vídeo en tiempo real en varias escenas, con mezclador de audio independiente por fuente. Captura profesional para realización y playout, fuentes de red, grabación y transmisión de baja latencia.",
            "Composition vidéo en temps réel sur plusieurs scènes, avec un mixage audio indépendant par source. Capture professionnelle pour régies et playout, sources réseau, enregistrement et diffusion à faible latence.",
            "Echtzeit-Videokomposition über mehrere Szenen, mit unabhängigem Audio-Mixer pro Quelle. Professionelle Aufnahme für Regie und Playout, Netzwerkquellen, latenzarme Aufnahme und Übertragung.",
        },
        ["ChipMultiScene"] = new[] { "Multi-scena", "Multi-scene", "Multiescena", "Multi-scène", "Multi-Szene" },
        ["ChipMixer"] = new[] { "Mixer audio", "Audio mixer", "Mezclador", "Mixage audio", "Audio-Mixer" },
        ["ChipNetwork"] = new[] { "Sorgenti di rete", "Network sources", "Fuentes de red", "Sources réseau", "Netzwerkquellen" },
        ["ChipLowLatency"] = new[] { "Bassa latenza", "Low latency", "Baja latencia", "Faible latence", "Niedrige Latenz" },
        ["Version"] = new[] { "Versione", "Version", "Versión", "Version", "Version" },

        // Webcam props
        ["CaptureProps"] = new[] { "Proprietà sorgente di cattura", "Capture source properties", "Propiedades de la fuente de captura", "Propriétés de la source de capture", "Eigenschaften der Aufnahmequelle" },
        ["UseCustomAudio"] = new[] { "Usa un dispositivo audio personalizzato", "Use a custom audio device", "Usar un dispositivo de audio personalizado", "Utiliser un périphérique audio personnalisé", "Benutzerdefiniertes Audiogerät verwenden" },
        ["AudioDevice"] = new[] { "Dispositivo audio", "Audio device", "Dispositivo de audio", "Périphérique audio", "Audiogerät" },
        ["NoAudioDevice"] = new[] { "(nessun dispositivo audio trovato)", "(no audio device found)", "(no se encontró dispositivo de audio)", "(aucun périphérique audio trouvé)", "(kein Audiogerät gefunden)" },

        // Menu principale (☰)
        ["MenuFile"] = new[] { "File", "File", "Archivo", "Fichier", "Datei" },
        ["MenuNewProject"] = new[] { "Nuovo progetto", "New project", "Nuevo proyecto", "Nouveau projet", "Neues Projekt" },
        ["MenuSaveProject"] = new[] { "Salva progetto…", "Save project…", "Guardar proyecto…", "Enregistrer le projet…", "Projekt speichern…" },
        ["MenuLoadProject"] = new[] { "Carica progetto…", "Load project…", "Cargar proyecto…", "Charger le projet…", "Projekt laden…" },
        ["MenuOpenRecFolder"] = new[] { "Apri cartella registrazioni", "Open recordings folder", "Abrir carpeta de grabaciones", "Ouvrir le dossier d'enregistrement", "Aufnahmeordner öffnen" },
        ["MenuEdit"] = new[] { "Modifica", "Edit", "Editar", "Édition", "Bearbeiten" },
        ["MenuCenterSource"] = new[] { "Centra sorgente", "Center source", "Centrar fuente", "Centrer la source", "Quelle zentrieren" },
        ["MenuFitScreen"] = new[] { "Adatta allo schermo", "Fit to screen", "Ajustar a la pantalla", "Ajuster à l'écran", "An Bildschirm anpassen" },
        ["MenuResetTransform"] = new[] { "Reset trasformazione", "Reset transform", "Restablecer transformación", "Réinitialiser la transformation", "Transformation zurücksetzen" },
        ["MenuView"] = new[] { "Visualizza", "View", "Ver", "Affichage", "Ansicht" },
        ["MenuStudioOn"] = new[] { "Attiva modalità studio", "Enable studio mode", "Activar modo estudio", "Activer le mode studio", "Studio-Modus aktivieren" },
        ["MenuStudioOff"] = new[] { "Disattiva modalità studio", "Disable studio mode", "Desactivar modo estudio", "Désactiver le mode studio", "Studio-Modus deaktivieren" },
        ["MenuGpuPreview"] = new[] { "Anteprima GPU (D3DImage, sperimentale)", "GPU preview (D3DImage, experimental)", "Vista previa GPU (D3DImage, experimental)", "Aperçu GPU (D3DImage, expérimental)", "GPU-Vorschau (D3DImage, experimentell)" },
        ["MenuProfileDefault"] = new[] { "Profilo: Default", "Profile: Default", "Perfil: Predeterminado", "Profil : Défaut", "Profil: Standard" },
        ["MenuProfileSettings"] = new[] { "Impostazioni profilo…", "Profile settings…", "Configuración del perfil…", "Paramètres du profil…", "Profileinstellungen…" },
        ["MenuSceneColl"] = new[] { "Collezione di scene", "Scene collection", "Colección de escenas", "Collection de scènes", "Szenensammlung" },
        ["MenuCurrent"] = new[] { "Attuale: ", "Current: ", "Actual: ", "Actuelle : ", "Aktuell: " },
        ["MenuSaveColl"] = new[] { "Salva collezione…", "Save collection…", "Guardar colección…", "Enregistrer la collection…", "Sammlung speichern…" },
        ["MenuLoadColl"] = new[] { "Carica collezione…", "Load collection…", "Cargar colección…", "Charger la collection…", "Sammlung laden…" },
        ["MenuTools"] = new[] { "Strumenti", "Tools", "Herramientas", "Outils", "Werkzeuge" },
        ["MenuSettingsDots"] = new[] { "Impostazioni…", "Settings…", "Configuración…", "Paramètres…", "Einstellungen…" },
        ["StartNdiOut"] = new[] { "▶ Avvia uscita NDI (Program)", "▶ Start NDI output (Program)", "▶ Iniciar salida NDI (Program)", "▶ Démarrer la sortie NDI (Program)", "▶ NDI-Ausgang starten (Program)" },
        ["StopNdiOut"] = new[] { "■ Ferma uscita NDI (Program)", "■ Stop NDI output (Program)", "■ Detener salida NDI (Program)", "■ Arrêter la sortie NDI (Program)", "■ NDI-Ausgang stoppen (Program)" },

        // Menu sorgente (tasto destro)
        ["Transform"] = new[] { "Trasforma", "Transform", "Transformar", "Transformer", "Transformieren" },
        ["TrCenter"] = new[] { "Centra", "Center", "Centrar", "Centrer", "Zentrieren" },
        ["TrStretch"] = new[] { "Stira allo schermo", "Stretch to screen", "Estirar a la pantalla", "Étirer à l'écran", "Auf Bildschirm dehnen" },
        ["TrReset"] = new[] { "Reset (dimensione originale)", "Reset (original size)", "Restablecer (tamaño original)", "Réinitialiser (taille originale)", "Zurücksetzen (Originalgröße)" },

        // Menu "+" (aggiungi sorgente)
        ["AddWindow"] = new[] { "Finestra", "Window", "Ventana", "Fenêtre", "Fenster" },
        ["NoneF"] = new[] { "(nessuna)", "(none)", "(ninguna)", "(aucune)", "(keine)" },
        ["NoneM"] = new[] { "(nessuno)", "(none)", "(ninguno)", "(aucun)", "(keiner)" },
        ["AddAudioCapture"] = new[] { "Cattura audio (dispositivo)", "Audio capture (device)", "Captura de audio (dispositivo)", "Capture audio (périphérique)", "Audio-Aufnahme (Gerät)" },
        ["OutputPrefix"] = new[] { "Uscita ▸ ", "Output ▸ ", "Salida ▸ ", "Sortie ▸ ", "Ausgabe ▸ " },
        ["AddNdiNet"] = new[] { "NDI (rete)", "NDI (network)", "NDI (red)", "NDI (réseau)", "NDI (Netzwerk)" },
        ["NdiNotInstalled"] = new[] { "Runtime NDI non installata — installa NDI Tools", "NDI runtime not installed — install NDI Tools", "Runtime NDI no instalado — instala NDI Tools", "Runtime NDI non installé — installez NDI Tools", "NDI-Runtime nicht installiert — NDI Tools installieren" },
        ["NoNdiSources"] = new[] { "(nessuna sorgente trovata)", "(no source found)", "(no se encontró fuente)", "(aucune source trouvée)", "(keine Quelle gefunden)" },
        ["RefreshList"] = new[] { "Aggiorna elenco", "Refresh list", "Actualizar lista", "Actualiser la liste", "Liste aktualisieren" },
        ["AddMedia"] = new[] { "Media (file video/audio)...", "Media (video/audio file)...", "Multimedia (archivo de vídeo/audio)...", "Média (fichier vidéo/audio)...", "Medien (Video-/Audiodatei)..." },
        ["AddNetStream"] = new[] { "Flusso di rete (URL m3u8 / RTMP)...", "Network stream (m3u8 / RTMP URL)...", "Flujo de red (URL m3u8 / RTMP)...", "Flux réseau (URL m3u8 / RTMP)...", "Netzwerk-Stream (m3u8 / RTMP-URL)..." },
        ["AddImage"] = new[] { "Immagine...", "Image...", "Imagen...", "Image...", "Bild..." },
        ["AddText"] = new[] { "Testo", "Text", "Texto", "Texte", "Text" },
        ["AddColor"] = new[] { "Colore (grigio)", "Color (gray)", "Color (gris)", "Couleur (gris)", "Farbe (grau)" },
        ["NetStreamTitle"] = new[] { "Flusso di rete", "Network stream", "Flujo de red", "Flux réseau", "Netzwerk-Stream" },
        ["NetStreamPrompt"] = new[] { "URL (HLS .m3u8, RTMP, HTTP, RTSP):", "URL (HLS .m3u8, RTMP, HTTP, RTSP):", "URL (HLS .m3u8, RTMP, HTTP, RTSP):", "URL (HLS .m3u8, RTMP, HTTP, RTSP) :", "URL (HLS .m3u8, RTMP, HTTP, RTSP):" },

        // Messaggi di stato / errori
        ["MsgLayoutReset"] = new[] { "Disposizione pannelli ripristinata.", "Panel layout reset.", "Disposición de paneles restablecida.", "Disposition des panneaux réinitialisée.", "Panel-Anordnung zurückgesetzt." },
        ["MsgNoProps"] = new[] { "Nessuna proprietà per questa sorgente.", "No properties for this source.", "Ninguna propiedad para esta fuente.", "Aucune propriété pour cette source.", "Keine Eigenschaften für diese Quelle." },
        ["MsgHexInvalid"] = new[] { "Esadecimale non valido (es. 204060).", "Invalid hex (e.g. 204060).", "Hexadecimal no válido (ej. 204060).", "Hexadécimal non valide (ex. 204060).", "Ungültiger Hex-Wert (z.B. 204060)." },
        ["MsgNoEditProps"] = new[] { "Questa sorgente non ha proprietà modificabili.", "This source has no editable properties.", "Esta fuente no tiene propiedades editables.", "Cette source n'a pas de propriétés modifiables.", "Diese Quelle hat keine bearbeitbaren Eigenschaften." },
        ["ErrProps"] = new[] { "Errore proprietà: ", "Properties error: ", "Error de propiedades: ", "Erreur de propriétés : ", "Eigenschaftsfehler: " },
        ["ErrNdiOut"] = new[] { "Errore uscita NDI: ", "NDI output error: ", "Error de salida NDI: ", "Erreur de sortie NDI : ", "NDI-Ausgangsfehler: " },
        ["MsgNdiOutOn"] = new[] { "Uscita NDI attiva: \"Nexus Streamer\" (Program)", "NDI output active: \"Nexus Streamer\" (Program)", "Salida NDI activa: \"Nexus Streamer\" (Program)", "Sortie NDI active : \"Nexus Streamer\" (Program)", "NDI-Ausgang aktiv: \"Nexus Streamer\" (Program)" },
        ["MsgNdiOutOff"] = new[] { "Uscita NDI fermata.", "NDI output stopped.", "Salida NDI detenida.", "Sortie NDI arrêtée.", "NDI-Ausgang gestoppt." },
        ["MsgGpuOn"] = new[] { "Anteprima GPU (D3DImage) attiva.", "GPU preview (D3DImage) active.", "Vista previa GPU (D3DImage) activa.", "Aperçu GPU (D3DImage) actif.", "GPU-Vorschau (D3DImage) aktiv." },
        ["MsgGpuFail"] = new[] { "D3DImage non disponibile: ", "D3DImage unavailable: ", "D3DImage no disponible: ", "D3DImage indisponible : ", "D3DImage nicht verfügbar: " },
        ["MsgCpuPreview"] = new[] { "Anteprima CPU (WriteableBitmap).", "CPU preview (WriteableBitmap).", "Vista previa CPU (WriteableBitmap).", "Aperçu CPU (WriteableBitmap).", "CPU-Vorschau (WriteableBitmap)." },
        ["MsgRenderReady"] = new[] { "Render attivo. Aggiungi sorgenti.", "Render active. Add sources.", "Render activo. Añade fuentes.", "Rendu actif. Ajoutez des sources.", "Render aktiv. Quellen hinzufügen." },
        ["MsgNewProject"] = new[] { "Nuovo progetto", "New project", "Nuevo proyecto", "Nouveau projet", "Neues Projekt" },
        ["ErrAdd"] = new[] { "Errore aggiunta: ", "Add error: ", "Error al añadir: ", "Erreur d'ajout : ", "Hinzufügen-Fehler: " },
        ["MsgSourceAdded"] = new[] { "Sorgente aggiunta: ", "Source added: ", "Fuente añadida: ", "Source ajoutée : ", "Quelle hinzugefügt: " },
        ["ErrRecStart"] = new[] { "Errore avvio REC: ", "REC start error: ", "Error al iniciar REC: ", "Erreur de démarrage REC : ", "REC-Startfehler: " },
        ["MsgNoStreamUrl"] = new[] { "URL server di streaming mancante.", "Streaming server URL missing.", "Falta la URL del servidor de transmisión.", "URL du serveur de streaming manquante.", "Streaming-Server-URL fehlt." },
        ["MsgConnecting"] = new[] { "Connessione al server di streaming…", "Connecting to streaming server…", "Conectando al servidor de transmisión…", "Connexion au serveur de streaming…", "Verbinde mit Streaming-Server…" },
        ["MsgStreamFail"] = new[] { "Streaming non avviato (server non raggiungibile).", "Streaming not started (server unreachable).", "Transmisión no iniciada (servidor inaccesible).", "Streaming non démarré (serveur injoignable).", "Streaming nicht gestartet (Server nicht erreichbar)." },
        ["MsgStreamEnded"] = new[] { "Streaming terminato.", "Streaming ended.", "Transmisión finalizada.", "Streaming terminé.", "Streaming beendet." },
        ["MsgStreamEndedErr"] = new[] { "Streaming terminato CON ERRORI.", "Streaming ended WITH ERRORS.", "Transmisión finalizada CON ERRORES.", "Streaming terminé AVEC ERREURS.", "Streaming MIT FEHLERN beendet." },
        ["MsgRecSaved"] = new[] { "Registrazione salvata: ", "Recording saved: ", "Grabación guardada: ", "Enregistrement sauvegardé : ", "Aufnahme gespeichert: " },
        ["MsgRecEndedErr"] = new[] { "Registrazione terminata CON ERRORI.", "Recording ended WITH ERRORS.", "Grabación finalizada CON ERRORES.", "Enregistrement terminé AVEC ERREURS.", "Aufnahme MIT FEHLERN beendet." },
        ["MsgSaved"] = new[] { "Progetto salvato: ", "Project saved: ", "Proyecto guardado: ", "Projet enregistré : ", "Projekt gespeichert: " },
        ["ErrSave"] = new[] { "Errore salvataggio: ", "Save error: ", "Error al guardar: ", "Erreur d'enregistrement : ", "Speicherfehler: " },
        ["ErrLoad"] = new[] { "Errore caricamento: ", "Load error: ", "Error al cargar: ", "Erreur de chargement : ", "Ladefehler: " },
        ["MsgNoProject"] = new[] { "Nessun progetto: ", "No project: ", "Ningún proyecto: ", "Aucun projet : ", "Kein Projekt: " },
        ["MsgSourceSkipped"] = new[] { "Sorgente saltata: ", "Source skipped: ", "Fuente omitida: ", "Source ignorée : ", "Quelle übersprungen: " },
        ["MsgLoaded"] = new[] { "Progetto caricato: ", "Project loaded: ", "Proyecto cargado: ", "Projet chargé : ", "Projekt geladen: " },
        ["MsgVCamStopped"] = new[] { "Camera virtuale fermata.", "Virtual camera stopped.", "Cámara virtual detenida.", "Caméra virtuelle arrêtée.", "Virtuelle Kamera gestoppt." },
        ["MsgVCamComing"] = new[] { "Camera virtuale: funzione in arrivo.", "Virtual camera: feature coming soon.", "Cámara virtual: función próximamente.", "Caméra virtuelle : fonctionnalité à venir.", "Virtuelle Kamera: Funktion folgt bald." },

        // Avvisi streaming (MessageBox)
        ["WarnNoServerTitle"] = new[] { "Streaming non avviato", "Streaming not started", "Transmisión no iniciada", "Streaming non démarré", "Streaming nicht gestartet" },
        ["WarnNoServerMsg"] = new[]
        {
            "Nessun server di streaming impostato.\n\nApri Impostazioni → Stream e inserisci l'URL del server (es. rtmp://live.twitch.tv/app) e la chiave.",
            "No streaming server set.\n\nOpen Settings → Stream and enter the server URL (e.g. rtmp://live.twitch.tv/app) and the key.",
            "No hay servidor de transmisión configurado.\n\nAbre Configuración → Stream e introduce la URL del servidor (ej. rtmp://live.twitch.tv/app) y la clave.",
            "Aucun serveur de streaming défini.\n\nOuvrez Paramètres → Stream et saisissez l'URL du serveur (ex. rtmp://live.twitch.tv/app) et la clé.",
            "Kein Streaming-Server festgelegt.\n\nÖffnen Sie Einstellungen → Stream und geben Sie die Server-URL (z.B. rtmp://live.twitch.tv/app) und den Schlüssel ein.",
        },
        ["WarnConnFailTitle"] = new[] { "Connessione fallita", "Connection failed", "Conexión fallida", "Échec de la connexion", "Verbindung fehlgeschlagen" },
        ["WarnConnFailMsg"] = new[]
        {
            "Impossibile connettersi al server di streaming:\n{0}\n\nVerifica che il server sia avviato e che URL/chiave siano corretti.",
            "Cannot connect to the streaming server:\n{0}\n\nMake sure the server is running and the URL/key are correct.",
            "No se puede conectar al servidor de transmisión:\n{0}\n\nComprueba que el servidor esté en marcha y que la URL/clave sean correctas.",
            "Impossible de se connecter au serveur de streaming :\n{0}\n\nVérifiez que le serveur est démarré et que l'URL/la clé sont correctes.",
            "Verbindung zum Streaming-Server nicht möglich:\n{0}\n\nStellen Sie sicher, dass der Server läuft und URL/Schlüssel korrekt sind.",
        },
        ["WarnNdiTitle"] = new[] { "NDI non disponibile", "NDI unavailable", "NDI no disponible", "NDI indisponible", "NDI nicht verfügbar" },
        ["WarnNdiMsg"] = new[]
        {
            "Runtime NDI non installata.\n\nInstalla \"NDI Tools\" (o NDI Runtime) da ndi.video, riavvia il programma e riprova.",
            "NDI runtime not installed.\n\nInstall \"NDI Tools\" (or NDI Runtime) from ndi.video, restart the app and try again.",
            "Runtime NDI no instalado.\n\nInstala \"NDI Tools\" (o NDI Runtime) desde ndi.video, reinicia el programa e inténtalo de nuevo.",
            "Runtime NDI non installé.\n\nInstallez \"NDI Tools\" (ou NDI Runtime) depuis ndi.video, redémarrez le programme et réessayez.",
            "NDI-Runtime nicht installiert.\n\nInstallieren Sie \"NDI Tools\" (oder NDI Runtime) von ndi.video, starten Sie das Programm neu und versuchen Sie es erneut.",
        },
    };
}
