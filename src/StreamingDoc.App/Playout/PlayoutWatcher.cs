using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace StreamingDoc.App.Playout;

/// <summary>Richiesta di splice ricavata dal palinsesto del playout.</summary>
public sealed class SpliceEvent
{
    public uint EventId;
    public double DurationSeconds;
    public bool OutOfNetwork = true;   // true = Cue-Out (inizio pubblicita'), false = Cue-In (rientro)
    public bool Immediate;             // true = splice_immediate (no PTS); false = pre-roll temporizzato
    public double PrerollSeconds;      // anticipo residuo verso il punto di splice (per il PTS futuro)
    public string Title = "";
}

/// <summary>Un break pubblicitario del palinsesto.</summary>
internal sealed class ScheduledBreak
{
    public DateTime StartLocal;      // DateTime.MinValue = trigger immediato (senza orario)
    public double DurationSeconds;
    public uint EventId;
    public string Title = "";
}

/// <summary>Una singola iniezione SCTE-35 programmata (una delle ripetizioni pre-roll di un cue).</summary>
internal sealed class ScheduledInjection
{
    public DateTime TriggerLocal;    // quando iniettare
    public DateTime TargetLocal;     // istante del punto di splice (per il pre-roll residuo)
    public uint EventId;
    public bool OutOfNetwork;        // true = Cue-Out, false = Cue-In
    public double DurationSeconds;   // durata break (solo Cue-Out)
    public string Key = "";
}

/// <summary>
/// Legge il palinsesto del playout e programma i marker SCTE-35 agli orari dei break pubblicitari.
/// - Punta a un file (es. <c>scte_default.xml</c>): ne estrae TUTTI i break Commercial con orario e durata
///   e alza <see cref="SpliceRequested"/> all'ora esatta di ognuno (schedule-driven).
/// - Un <see cref="FileSystemWatcher"/> ricarica il palinsesto quando il file cambia (rigenerazione giornaliera).
/// - Se il file è invece un trigger minimale (Duration/EventID, senza orario) lo spara subito ad ogni scrittura.
/// Formati letti: XMLTV &lt;tv&gt;, &lt;ProgramGuide&gt;, schedule.json, trigger Duration/EventID.
/// Break = category PROMO / rating Commercial / category_sys_id=1 / h_ty=Commercial.
/// </summary>
public sealed class PlayoutWatcher : IDisposable
{
    private readonly string _path;
    private FileSystemWatcher? _fsw;
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private List<ScheduledInjection> _injections = new(); // iniezioni programmate (Cue-Out/In x3 pre-roll)
    private readonly HashSet<string> _sent = new();       // chiavi gia' iniettate (anti-ripetizione/loop)
    private DateTime _lastReload = DateTime.MinValue;
    private uint _counter;

    // Pre-roll: il marker viene iniettato a T-8, T-5, T-2 secondi dal punto di splice (ridondanza).
    private static readonly int[] Preroll = { 8, 5, 2 };

    public event Action<SpliceEvent>? SpliceRequested;
    public event Action<string>? Error;

    public PlayoutWatcher(string filePath) => _path = filePath;

    public void Start()
    {
        var full = Path.GetFullPath(_path);
        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            throw new ArgumentException("Percorso file playout non valido.");
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Cartella playout inesistente: {dir}");
        if (!File.Exists(full))
            throw new FileNotFoundException($"File playout inesistente: {full}");

        Reload();

        _fsw = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += OnChanged;
        _fsw.Created += OnChanged;
        _fsw.Renamed += OnChanged;

        _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
    }

    /// <summary>Iniezioni programmate non ancora scattate (per la UI di stato).</summary>
    public int Pending { get { lock (_lock) return _injections.Count; } }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_lock) { if ((DateTime.UtcNow - _lastReload).TotalMilliseconds < 400) return; }
        try { Thread.Sleep(150); Reload(); }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }

    private void Reload()
    {
        lock (_lock) { _lastReload = DateTime.UtcNow; }

        List<ScheduledBreak> list;
        try { list = LoadBreaks(_path); }
        catch (Exception ex) { Error?.Invoke(ex.Message); return; }

        var now = DateTime.Now;
        var immediate = new List<ScheduledBreak>();
        var inj = new List<ScheduledInjection>();
        foreach (var b in list)
        {
            if (b.StartLocal == DateTime.MinValue) { immediate.Add(b); continue; } // trigger senza orario
            DateTime t = b.StartLocal;                                // inizio pubblicita' (Cue-Out)
            DateTime r = b.StartLocal.AddSeconds(b.DurationSeconds);   // rientro programma (Cue-In)
            foreach (int off in Preroll)
            {
                inj.Add(new ScheduledInjection
                {
                    TriggerLocal = t.AddSeconds(-off), TargetLocal = t, EventId = b.EventId,
                    OutOfNetwork = true, DurationSeconds = b.DurationSeconds, Key = $"{b.EventId}_O_{off}",
                });
                if (b.DurationSeconds > 0)
                    inj.Add(new ScheduledInjection
                    {
                        TriggerLocal = r.AddSeconds(-off), TargetLocal = r, EventId = b.EventId,
                        OutOfNetwork = false, DurationSeconds = 0, Key = $"{b.EventId}_I_{off}",
                    });
            }
        }
        lock (_lock)
        {
            // tieni solo le iniezioni non ancora inviate e col punto di splice ancora futuro
            _injections = inj.Where(x => !_sent.Contains(x.Key) && x.TargetLocal > now)
                             .OrderBy(x => x.TriggerLocal).ToList();
        }
        foreach (var b in immediate) FireImmediate(b);
    }

    private void Tick(object? _)
    {
        var now = DateTime.Now;
        var due = new List<ScheduledInjection>();
        lock (_lock)
        {
            for (int i = _injections.Count - 1; i >= 0; i--)
            {
                var x = _injections[i];
                if (_sent.Contains(x.Key)) { _injections.RemoveAt(i); continue; }
                if (now >= x.TargetLocal) { _sent.Add(x.Key); _injections.RemoveAt(i); continue; } // scaduta
                if (now >= x.TriggerLocal) { due.Add(x); _sent.Add(x.Key); _injections.RemoveAt(i); }
            }
        }
        foreach (var x in due) FireInjection(x, now);
    }

    // Trigger senza orario (file trigger minimale): splice immediato, auto_return gestisce il rientro.
    private void FireImmediate(ScheduledBreak b)
    {
        try
        {
            SpliceRequested?.Invoke(new SpliceEvent
            {
                EventId = b.EventId, DurationSeconds = b.DurationSeconds,
                OutOfNetwork = true, Immediate = true, PrerollSeconds = 0, Title = b.Title,
            });
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }

    // Iniezione pre-roll: pre-roll residuo = quanto manca al punto di splice, per il PTS futuro.
    private void FireInjection(ScheduledInjection x, DateTime now)
    {
        try
        {
            double preroll = (x.TargetLocal - now).TotalSeconds;
            if (preroll < 0) preroll = 0;
            SpliceRequested?.Invoke(new SpliceEvent
            {
                EventId = x.EventId, DurationSeconds = x.DurationSeconds,
                OutOfNetwork = x.OutOfNetwork, Immediate = false, PrerollSeconds = preroll,
            });
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }

    // ---- caricamento palinsesto ----
    private List<ScheduledBreak> LoadBreaks(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            text = sr.ReadToEnd();

        string t = text.TrimStart();
        if (t.StartsWith("{") || t.StartsWith("[")) return LoadJson(text);
        return LoadXml(text);
    }

    // schedule.json: eventi con h_ty=Commercial, start_at (UTC), duration (ms)
    private List<ScheduledBreak> LoadJson(string text)
    {
        var list = new List<ScheduledBreak>();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var ev in events.EnumerateArray())
        {
            string ty = Str(ev, "h_ty");
            string title = Str(ev, "h_ti");
            if (!IsCommercialName(ty, title)) continue;
            var start = ParseIso(Str(ev, "start_at"));
            double secs = ev.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() / 1000.0 : 0;
            if (secs <= 0) continue;
            list.Add(new ScheduledBreak { StartLocal = start, DurationSeconds = secs, EventId = StableId(title + (start == DateTime.MinValue ? "" : start.Ticks.ToString())), Title = title });
        }
        return list;
    }

    private List<ScheduledBreak> LoadXml(string text)
    {
        var list = new List<ScheduledBreak>();
        var doc = XDocument.Parse(text);
        var root = doc.Root;
        if (root == null) return list;
        string rn = root.Name.LocalName;

        // trigger minimale Duration/EventID (senza orario -> immediato)
        var durRaw = FindValue(root, "duration");
        if (durRaw != null && !rn.Equals("tv", StringComparison.OrdinalIgnoreCase) && !rn.Equals("ProgramGuide", StringComparison.OrdinalIgnoreCase))
        {
            double s = ParseSeconds(durRaw);
            if (s > 0) list.Add(new ScheduledBreak { StartLocal = DateTime.MinValue, DurationSeconds = s, EventId = ParseId(FindValue(root, "eventid")) });
            return list;
        }

        if (rn.Equals("tv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in root.Elements().Where(x => x.Name.LocalName == "programme"))
            {
                if (!IsCommercialXmltv(p)) continue;
                var start = ParseXmltvTime(Attr(p, "start")) ?? DateTime.MinValue;
                double secs = LenghtSeconds(p);
                if (secs <= 0) continue;
                list.Add(new ScheduledBreak { StartLocal = start, DurationSeconds = secs, EventId = StableId((Val(p, "episode-num") ?? "") + (start == DateTime.MinValue ? "" : start.Ticks.ToString())), Title = Val(p, "title") ?? "" });
            }
            return list;
        }

        if (rn.Equals("ProgramGuide", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in root.Descendants().Where(x => x.Name.LocalName == "Event"))
            {
                if (!IsCommercialGuide(p)) continue;
                var start = ParseGuideTime(Val(p, "StartDate"), Val(p, "StartTime")) ?? DateTime.MinValue;
                double secs = GuideDuration(p);
                if (secs <= 0) continue;
                list.Add(new ScheduledBreak { StartLocal = start, DurationSeconds = secs, EventId = StableId((Val(p, "IdName") ?? "") + (start == DateTime.MinValue ? "" : start.Ticks.ToString())), Title = Val(p, "Descrizione") ?? "" });
            }
            return list;
        }
        return list;
    }

    // ---- riconoscimento break ----
    private static bool IsCommercialXmltv(XElement p)
    {
        if (Val(p, "category")?.Trim().Equals("PROMO", StringComparison.OrdinalIgnoreCase) == true) return true;
        var rating = p.Descendants().FirstOrDefault(x => x.Name.LocalName == "value")?.Value;
        if (rating?.Trim().Equals("Commercial", StringComparison.OrdinalIgnoreCase) == true) return true;
        return Val(p, "category_sys_id")?.Trim() == "1";
    }

    private static bool IsCommercialGuide(XElement p)
    {
        var v = p.Descendants().FirstOrDefault(x => x.Name.LocalName == "value")?.Value;
        if (v?.Trim().Equals("Commercial", StringComparison.OrdinalIgnoreCase) == true) return true;
        return IsCommercialName(null, Val(p, "Descrizione") ?? Val(p, "IdName") ?? "");
    }

    private static bool IsCommercialName(string? type, string title)
    {
        if (type?.Trim().Equals("Commercial", StringComparison.OrdinalIgnoreCase) == true) return true;
        var n = (title ?? "").ToUpperInvariant();
        return n.Contains("PROMO") || n.Contains("ADNPLAY");
    }

    private static double LenghtSeconds(XElement p)
    {
        var l = Val(p, "lenght") ?? Val(p, "length");
        if (l != null && double.TryParse(l.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) && s > 0) return s;
        var a = ParseXmltvTime(Attr(p, "start")); var b = ParseXmltvTime(Attr(p, "stop"));
        return (a != null && b != null) ? (b.Value - a.Value).TotalSeconds : 0;
    }

    private static double GuideDuration(XElement p)
    {
        var a = ParseGuideTime(Val(p, "StartDate"), Val(p, "StartTime"));
        var b = ParseGuideTime(Val(p, "EndDate"), Val(p, "EndTime"));
        return (a != null && b != null) ? (b.Value - a.Value).TotalSeconds : 0;
    }

    // ---- helper ----
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? Val(XElement root, string localName)
        => root.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Attr(XElement el, string localName)
        => el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? FindValue(XElement root, string localName)
    {
        var el = root.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (el != null && !string.IsNullOrWhiteSpace(el.Value)) return el.Value;
        return root.DescendantsAndSelf().SelectMany(x => x.Attributes())
            .FirstOrDefault(a => a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static double ParseSeconds(string raw)
    {
        if (!double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return 0;
        return v > 3600 ? v / 1000.0 : v;
    }

    private static DateTime ParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.LocalDateTime : DateTime.MinValue;
    }

    private static DateTime? ParseXmltvTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto.LocalDateTime;
        var t = s.Trim();
        int sp = t.IndexOf(' ');
        var core = sp > 0 ? t[..sp] : t;
        return DateTime.TryParseExact(core, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static DateTime? ParseGuideTime(string? date, string? time)
    {
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time)) return null;
        return DateTime.TryParse($"{date.Trim()}T{time.Trim()}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    }

    private uint ParseId(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) && uint.TryParse(s.Trim(), out var v)) return v;
        return StableId(s);
    }

    private uint StableId(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return ++_counter == 0 ? ++_counter : _counter;
        uint h = 2166136261;
        foreach (char c in key) { h ^= c; h *= 16777619; }
        return h == 0 ? 1u : h;
    }

    public void Dispose()
    {
        var t = _timer; _timer = null;
        t?.Dispose();
        var w = _fsw; _fsw = null;
        if (w != null)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Changed -= OnChanged; w.Created -= OnChanged; w.Renamed -= OnChanged;
            w.Dispose();
        }
    }
}
