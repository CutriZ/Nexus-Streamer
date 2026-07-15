using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamingDoc.App.Audio;
using StreamingDoc.App.Graphics;
using StreamingDoc.App.Interop;
using StreamingDoc.App.Interop.Ndi;
using StreamingDoc.App.Localization;
using StreamingDoc.App.Model;
using StreamingDoc.App.Output;
using StreamingDoc.App.Playout;
using StreamingDoc.App.Recording;
using StreamingDoc.App.Sources;

namespace StreamingDoc.App;

public partial class MainWindow : Window
{
    private GraphicsDevice? _gd;
    private Compositor? _compositor;
    private WriteableBitmap? _wb;
    private AudioMixer? _mixer;
    private System.Windows.Threading.DispatcherTimer? _vuTimer;
    private VideoRecorder? _recorder;
    private VideoRecorder? _streamer;
    private NdiOutput? _ndiOut;
    private readonly CompositeSink _sink = new();
    private readonly ObservableCollection<Scene> _scenes = new();
    private Scene? _currentScene;
    private int _sceneCounter;

    private bool _studio;
    private string _collectionName = Loc.T("Untitled");
    private readonly System.Diagnostics.Stopwatch _statsWatch = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Diagnostics.Process _proc = System.Diagnostics.Process.GetCurrentProcess();
    private long _lastFrameCount;
    private double _lastStatsT;
    private TimeSpan _lastCpuTime;
    private DateTime? _recStart;
    private DateTime? _streamStart;

    // Impostazioni progetto (persistite)
    private int _canvasW = 1920, _canvasH = 1080, _fps = 30;
    private long _recBitrate = 10_000_000, _streamBitrate = 6_000_000;
    private string _recFolder = "", _recFormat = "mp4", _encoder = "auto";
    private string _streamProtocol = "rtmp"; // none | rtmp | srt
    private string _streamUrl = "rtmp://localhost/live", _streamKey = "";
    private string _streamUser = "", _streamPass = "";
    private bool _streamUseAuth;
    private string _srtHost = "", _srtMode = "caller", _srtUrl = "";
    private int _srtPort = 9000, _srtLatencyMs = 200;
    private bool _scteEnabled;
    private string _sctePlayoutFile = "";
    private PlayoutWatcher? _playoutWatcher;
    private readonly ObservableCollection<string> _scteLog = new(); // log eventi SCTE-35 (per la finestra Log)
    private Window? _scteLogWindow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nexus Streamer");

    private string DefaultProjectPath => Path.Combine(DataDir, "project.json");

    // Migra la vecchia cartella dati "StreamingDoc" -> "Nexus Streamer" senza perdere i progetti.
    private static void MigrateDataFolder()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var oldDir = Path.Combine(docs, "StreamingDoc");
            if (Directory.Exists(oldDir) && !Directory.Exists(DataDir))
                Directory.Move(oldDir, DataDir);
        }
        catch { /* best effort: se fallisce si riparte da progetto vuoto */ }
    }

    public MainWindow()
    {
        InitializeComponent();
        Controls.WindowMaximizeHelper.Enable(this);
        InitDocking();
        ScenesList.ItemsSource = _scenes;
        SceneTabs.ItemsSource = _scenes;
        Closed += OnClosed;
        Loaded += OnLoaded;
        PreviewImage.MouseLeftButtonDown += OnPreviewMouseDown;
        PreviewImage.MouseMove += OnPreviewMouseMove;
        PreviewImage.MouseLeftButtonUp += OnPreviewMouseUp;
    }

    // ---- Docking (AvalonDock) ----

    private readonly Dictionary<string, object> _paneContent = new();
    private string? _defaultLayout;

    private string LayoutPath => Path.Combine(DataDir, "layout.xml");

    private void InitDocking()
    {
        // Mappa ContentId -> contenuto, per riconnettere i pannelli quando si (de)serializza il layout.
        foreach (var c in Docker.Layout.Descendents().OfType<LayoutContent>())
            if (c.ContentId is { } id && c.Content is { } content)
                _paneContent[id] = content;

        var sb = new StringBuilder();
        using (var w = new StringWriter(sb)) new XmlLayoutSerializer(Docker).Serialize(w);
        _defaultLayout = sb.ToString(); // layout di default (per il reset)
    }

    private void ApplyLayout(string xml)
    {
        var ser = new XmlLayoutSerializer(Docker);
        ser.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is { } id && _paneContent.TryGetValue(id, out var c)) e.Content = c;
            else e.Cancel = true;
        };
        using var r = new StringReader(xml);
        ser.Deserialize(r);
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        if (_defaultLayout is not null) ApplyLayout(_defaultLayout);
        StatusText.Text = Loc.T("MsgLayoutReset");
    }

    private void TryLoadLayout()
    {
        try { if (File.Exists(LayoutPath)) ApplyLayout(File.ReadAllText(LayoutPath)); }
        catch { /* layout corrotto: resta quello di default */ }
    }

    private void TrySaveLayout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) new XmlLayoutSerializer(Docker).Serialize(w);
            File.WriteAllText(LayoutPath, sb.ToString());
        }
        catch { /* best effort */ }
    }

    // ---- Editing trasformazione nel preview ----

    private enum DragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }

    private DragMode _drag = DragMode.None;
    private SceneItem? _dragItem;
    private double _dragStartCx, _dragStartCy, _origX, _origY, _origW, _origH;

    private bool ClientToCanvas(Point p, out double cx, out double cy)
    {
        cx = cy = 0;
        if (_compositor is null) return false;
        double aw = PreviewImage.ActualWidth, ah = PreviewImage.ActualHeight;
        int pw = _compositor.PreviewWidth, ph = _compositor.PreviewHeight;
        if (aw <= 0 || ah <= 0 || pw <= 0 || ph <= 0) return false;

        // Image (DIP) -> texture preview (px): Stretch=Uniform letterbox dentro l'Image.
        double imgScale = Math.Min(aw / pw, ah / ph);
        double ox = (aw - pw * imgScale) / 2, oy = (ah - ph * imgScale) / 2;
        double texX = (p.X - ox) / imgScale, texY = (p.Y - oy) / imgScale;

        double s = _compositor.EditScale;
        if (s <= 0) return false;
        cx = (texX - _compositor.EditOffsetX) / s;
        cy = (texY - _compositor.EditOffsetY) / s;
        return true;
    }

    private double CanvasTolerance()
    {
        double aw = PreviewImage.ActualWidth;
        int pw = _compositor!.PreviewWidth, ph = _compositor.PreviewHeight;
        double imgScale = Math.Min(aw / Math.Max(1, pw), PreviewImage.ActualHeight / Math.Max(1, ph));
        double combined = _compositor.EditScale * Math.Max(0.0001, imgScale); // canvas px -> Image DIP
        return 9.0 / Math.Max(0.0001, combined);
    }

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_compositor is null || _currentScene is null) return;
        PreviewImage.CaptureMouse();
        if (!ClientToCanvas(e.GetPosition(PreviewImage), out double cx, out double cy)) return;

        double tol = CanvasTolerance();

        // Se c'e' gia' una selezione, controlla prima i suoi handle/corpo.
        var sel = SourcesList.SelectedItem as SceneItem;
        if (sel is not null && TryBeginOnItem(sel, cx, cy, tol)) return;

        // Altrimenti seleziona l'item piu' in primo piano sotto il cursore (indice 0 = davanti).
        SceneItem[] items;
        lock (_compositor.SceneLock) items = _currentScene.Items.ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (cx >= it.X && cx <= it.X + it.Width && cy >= it.Y && cy <= it.Y + it.Height)
            {
                SourcesList.SelectedItem = it;
                TryBeginOnItem(it, cx, cy, tol);
                return;
            }
        }
        _drag = DragMode.None;
    }

    private bool TryBeginOnItem(SceneItem it, double cx, double cy, double tol)
    {
        if (it.Locked) return false; // bloccata: niente drag/resize
        bool left = Math.Abs(cx - it.X) <= tol;
        bool right = Math.Abs(cx - (it.X + it.Width)) <= tol;
        bool top = Math.Abs(cy - it.Y) <= tol;
        bool bottom = Math.Abs(cy - (it.Y + it.Height)) <= tol;

        DragMode mode = DragMode.None;
        if (top && left) mode = DragMode.ResizeTL;
        else if (top && right) mode = DragMode.ResizeTR;
        else if (bottom && left) mode = DragMode.ResizeBL;
        else if (bottom && right) mode = DragMode.ResizeBR;
        else if (cx >= it.X && cx <= it.X + it.Width && cy >= it.Y && cy <= it.Y + it.Height)
            mode = DragMode.Move;

        if (mode == DragMode.None) return false;

        _drag = mode;
        _dragItem = it;
        _dragStartCx = cx; _dragStartCy = cy;
        _origX = it.X; _origY = it.Y; _origW = it.Width; _origH = it.Height;
        return true;
    }

    private void OnPreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _drag = DragMode.None;
        PreviewImage.ReleaseMouseCapture();
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag == DragMode.None || _dragItem is null || _compositor is null) return;
        if (!ClientToCanvas(e.GetPosition(PreviewImage), out double cx, out double cy)) return;

        double dx = cx - _dragStartCx, dy = cy - _dragStartCy;
        const double min = 16;
        double x = _origX, y = _origY, w = _origW, h = _origH;

        switch (_drag)
        {
            case DragMode.Move: x = _origX + dx; y = _origY + dy; break;
            case DragMode.ResizeTL: x = _origX + dx; y = _origY + dy; w = _origW - dx; h = _origH - dy; break;
            case DragMode.ResizeTR: y = _origY + dy; w = _origW + dx; h = _origH - dy; break;
            case DragMode.ResizeBL: x = _origX + dx; w = _origW - dx; h = _origH + dy; break;
            case DragMode.ResizeBR: w = _origW + dx; h = _origH + dy; break;
        }
        if (w < min) { w = min; if (_drag is DragMode.ResizeTL or DragMode.ResizeBL) x = _origX + _origW - min; }
        if (h < min) { h = min; if (_drag is DragMode.ResizeTL or DragMode.ResizeTR) y = _origY + _origH - min; }

        // Snapping ai bordi/centro del canvas (stile OBS).
        ApplySnap(ref x, ref y, ref w, ref h);

        lock (_compositor.SceneLock)
        {
            _dragItem.X = x; _dragItem.Y = y; _dragItem.Width = w; _dragItem.Height = h;
        }
    }

    // Aggancio a 0 / centro / fine canvas, su entrambi gli assi.
    private void ApplySnap(ref double x, ref double y, ref double w, ref double h)
    {
        double cw = _compositor!.CanvasWidth, ch = _compositor.CanvasHeight;
        double snap = SnapThreshold();
        double[] gx = { 0, cw / 2, cw };
        double[] gy = { 0, ch / 2, ch };

        if (_drag == DragMode.Move)
        {
            x += BestSnapDelta(new[] { x, x + w / 2, x + w }, gx, snap);
            y += BestSnapDelta(new[] { y, y + h / 2, y + h }, gy, snap);
            return;
        }

        bool left = _drag is DragMode.ResizeTL or DragMode.ResizeBL;
        bool right = _drag is DragMode.ResizeTR or DragMode.ResizeBR;
        bool top = _drag is DragMode.ResizeTL or DragMode.ResizeTR;
        bool bottom = _drag is DragMode.ResizeBL or DragMode.ResizeBR;
        if (left) { double e = SnapValue(x, gx, snap); w += x - e; x = e; }
        if (right) { double e = SnapValue(x + w, gx, snap); w = e - x; }
        if (top) { double e = SnapValue(y, gy, snap); h += y - e; y = e; }
        if (bottom) { double e = SnapValue(y + h, gy, snap); h = e - y; }
    }

    private static double SnapValue(double v, double[] guides, double snap)
    {
        double best = v, bestD = snap;
        foreach (var g in guides) { double d = Math.Abs(g - v); if (d <= bestD) { bestD = d; best = g; } }
        return best;
    }

    private static double BestSnapDelta(double[] edges, double[] guides, double snap)
    {
        double best = 0, bestD = snap;
        foreach (var e in edges)
            foreach (var g in guides)
            { double d = g - e; if (Math.Abs(d) <= bestD) { bestD = Math.Abs(d); best = d; } }
        return best;
    }

    private double SnapThreshold()
    {
        // ~10px schermo -> unita' canvas.
        double aw = PreviewImage.ActualWidth, ah = PreviewImage.ActualHeight;
        int pw = _compositor!.PreviewWidth, ph = _compositor.PreviewHeight;
        double imgScale = Math.Min(aw / Math.Max(1, pw), ah / Math.Max(1, ph));
        double combined = _compositor.EditScale * Math.Max(0.0001, imgScale);
        return 10.0 / Math.Max(0.0001, combined);
    }

    private void OnSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        var it = SourcesList.SelectedItem as SceneItem;
        _compositor?.SetSelected(it);
        if (SelSourceText != null) SelSourceText.Text = it?.Name ?? Loc.T("NoSourceSelected");
        UpdatePropsPanel(it);
    }

    private bool _propUpdating;
    private void UpdatePropsPanel(SceneItem? it)
    {
        if (PropName is null) return;
        _propUpdating = true;
        PropName.Text = it?.Name ?? "";
        PropOpacity.Value = it?.Opacity ?? 1;
        PropVisible.IsChecked = it?.Visible ?? false;
        PropLocked.IsChecked = it?.Locked ?? false;
        PropOpacity.IsEnabled = PropVisible.IsEnabled = PropLocked.IsEnabled = it is not null;
        _propUpdating = false;
    }
    private void OnPropOpacity(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_propUpdating && Selected is { } s) s.Opacity = PropOpacity.Value; }
    private void OnPropVisible(object sender, RoutedEventArgs e) { if (!_propUpdating && Selected is { } s) s.Visible = PropVisible.IsChecked == true; }
    private void OnPropLocked(object sender, RoutedEventArgs e) { if (!_propUpdating && Selected is { } s) s.Locked = PropLocked.IsChecked == true; }

    // ---- Title bar / rail ----
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseWin(object sender, RoutedEventArgs e) => Close();
    private void OnVirtualCam(object sender, RoutedEventArgs e)
    {
        bool active = (string?)VirtualCamButton.Tag == "active";
        VirtualCamButton.Tag = active ? null : "active";
        VirtualCamButton.Content = active ? Loc.T("StartVCam") : Loc.T("StopVCam");
        StatusText.Text = active ? Loc.T("MsgVCamStopped") : Loc.T("MsgVCamComing");
    }

    // ---- Tab scena (sincronizzate con la lista) ----
    private bool _syncTabs;
    private void OnSceneTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncTabs) return;
        if (SceneTabs.SelectedItem is Scene sc) ScenesList.SelectedItem = sc;
    }

    private SceneItem? Selected => SourcesList.SelectedItem as SceneItem;

    private void UpdateStats()
    {
        long fc = _compositor?.FrameCount ?? 0;
        double now = _statsWatch.Elapsed.TotalSeconds;
        double dt = now - _lastStatsT;
        if (dt < 0.5) return;

        double fps = (fc - _lastFrameCount) / Math.Max(0.001, dt);
        _lastFrameCount = fc; _lastStatsT = now;

        // CPU% del processo
        var cpuNow = _proc.TotalProcessorTime;
        double cpu = (cpuNow - _lastCpuTime).TotalSeconds / (dt * Environment.ProcessorCount) * 100;
        _lastCpuTime = cpuNow;

        StFps.Text = $"{fps:0}";
        StCpu.Text = $"{Math.Clamp(cpu, 0, 100):0}%";
        StLive.Text = _streamStart is { } ss ? (DateTime.Now - ss).ToString(@"hh\:mm\:ss") : "00:00:00";
        StRec.Text = _recStart is { } rs ? (DateTime.Now - rs).ToString(@"hh\:mm\:ss") : "00:00:00";
        // bitrate target in kilobit/s (stessa unità delle Impostazioni): 1100 kbps -> 1100, non 137.
        long kbps = (_streamer != null ? _streamBitrate : _recorder != null ? _recBitrate : 0) / 1000;
        StKbs.Text = kbps.ToString();
    }

    private void OnFilters(SceneItem it) => new FiltersDialog(it) { Owner = this }.Show();
    private void OnPropsSelected(object sender, RoutedEventArgs e) { if (Selected is { } s) EditProperties(s); }
    private void OnFiltersSelected(object sender, RoutedEventArgs e) { if (Selected is { } s) OnFilters(s); }

    // Toggle occhio/lucchetto dalla lista fonti (DataContext = SceneItem)
    private void OnToggleVisible(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneItem it) it.Visible = !it.Visible;
    }
    private void OnToggleLocked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneItem it) it.Locked = !it.Locked;
    }

    // ---- Transform sorgente ----

    private void CenterItem(SceneItem it)
    {
        if (_compositor is null) return;
        lock (_compositor.SceneLock) { it.X = (_compositor.CanvasWidth - it.Width) / 2; it.Y = (_compositor.CanvasHeight - it.Height) / 2; }
    }
    private void StretchItem(SceneItem it)
    {
        if (_compositor is null) return;
        lock (_compositor.SceneLock) { it.X = 0; it.Y = 0; it.Width = _compositor.CanvasWidth; it.Height = _compositor.CanvasHeight; }
    }
    private void FitItem(SceneItem it)
    {
        if (_compositor is null) return;
        int cw = _compositor.CanvasWidth, ch = _compositor.CanvasHeight;
        int sw = Math.Max(1, it.Source.Width), sh = Math.Max(1, it.Source.Height);
        double s = Math.Min((double)cw / sw, (double)ch / sh);
        lock (_compositor.SceneLock) { it.Width = sw * s; it.Height = sh * s; it.X = (cw - it.Width) / 2; it.Y = (ch - it.Height) / 2; }
    }
    private void ResetItem(SceneItem it)
    {
        if (_compositor is null) return;
        int sw = Math.Max(1, it.Source.Width), sh = Math.Max(1, it.Source.Height);
        lock (_compositor.SceneLock) { it.X = 0; it.Y = 0; it.Width = sw; it.Height = sh; }
    }

    // ---- Proprietà sorgente ----

    private void EditProperties(SceneItem it)
    {
        if (_gd is null || it.Descriptor is null) { StatusText.Text = Loc.T("MsgNoProps"); return; }
        var d = it.Descriptor;
        switch (d.Kind)
        {
            case SourceKind.Text:
                var t = InputDialog.Ask(this, "Proprietà testo", "Contenuto:", d.Name ?? "");
                if (t is null) return;
                d.Name = t; RecreateItemSource(it);
                break;
            case SourceKind.Color:
                var hex = InputDialog.Ask(this, "Proprietà colore", "Esadecimale RRGGBB:", $"{d.R:X2}{d.G:X2}{d.B:X2}");
                if (hex is null) return;
                if (TryParseHex(hex, out byte r, out byte g, out byte b)) { d.R = r; d.G = g; d.B = b; RecreateItemSource(it); }
                else StatusText.Text = Loc.T("MsgHexInvalid");
                break;
            case SourceKind.Image:
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tutti|*.*" };
                if (dlg.ShowDialog() == true) { d.Name = dlg.FileName; RecreateItemSource(it); }
                break;
            case SourceKind.Webcam:
            {
                var audioDevs = Interop.DirectShowDevices.AudioInputNames();
                var wdlg = new WebcamPropsDialog(audioDevs, d.UseCustomAudio, d.AudioDeviceName) { Owner = this };
                if (wdlg.ShowDialog() != true) return;
                d.UseCustomAudio = wdlg.UseCustom;
                d.AudioDeviceName = wdlg.UseCustom ? wdlg.SelectedDevice : d.AudioDeviceName;
                RecreateItemSource(it);
                break;
            }
            default:
                StatusText.Text = Loc.T("MsgNoEditProps");
                break;
        }
    }

    private void RecreateItemSource(SceneItem it)
    {
        try
        {
            var newSrc = CreateSource(it.Descriptor!);
            var old = it.Source;
            lock (_compositor!.SceneLock) it.ReplaceSource(newSrc);
            DisposeSource(old);
            UpdateAudioRouting();   // l'eventuale nuovo canale audio si sente solo se in onda
        }
        catch (Exception ex) { StatusText.Text = Loc.T("ErrProps") + ex.Message; }
    }

    private static bool TryParseHex(string s, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try { r = Convert.ToByte(s[..2], 16); g = Convert.ToByte(s.Substring(2, 2), 16); b = Convert.ToByte(s.Substring(4, 2), 16); return true; }
        catch { return false; }
    }

    // ---- Context menu ----

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = System.Windows.Media.VisualTreeHelper.GetParent(d); }
        return null;
    }

    private static void AddMenu(ItemsControl parent, string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        parent.Items.Add(mi);
    }

    private void OnSourceRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var li = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (li?.DataContext is not SceneItem it) return;
        SourcesList.SelectedItem = it;

        var menu = new ContextMenu();
        var prop = new MenuItem { Header = Loc.T("PropertiesDots") };
        prop.Click += (_, _) => EditProperties(it);
        prop.IsEnabled = it.Descriptor?.Kind is SourceKind.Text or SourceKind.Color or SourceKind.Image or SourceKind.Webcam;
        menu.Items.Add(prop);

        var filt = new MenuItem { Header = Loc.T("FiltersDots") };
        filt.Click += (_, _) => OnFilters(it);
        menu.Items.Add(filt);

        var tr = new MenuItem { Header = Loc.T("Transform") };
        AddMenu(tr, Loc.T("TrCenter"), () => CenterItem(it));
        AddMenu(tr, Loc.T("MenuFitScreen"), () => FitItem(it));
        AddMenu(tr, Loc.T("TrStretch"), () => StretchItem(it));
        AddMenu(tr, Loc.T("TrReset"), () => ResetItem(it));
        menu.Items.Add(tr);

        menu.Items.Add(new Separator());
        AddMenu(menu, "Rimuovi", () => OnRemoveSource(this, new RoutedEventArgs()));

        menu.PlacementTarget = SourcesList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnSceneRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var li = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (li?.DataContext is not Scene sc) return;
        ScenesList.SelectedItem = sc;

        var menu = new ContextMenu();
        AddMenu(menu, "Rinomina", () => RenameScene(sc));
        AddMenu(menu, "Duplica", () => DuplicateScene(sc));
        menu.Items.Add(new Separator());
        AddMenu(menu, "Rimuovi", () => OnRemoveScene(this, new RoutedEventArgs()));
        menu.PlacementTarget = ScenesList;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RenameScene(Scene sc)
    {
        var n = InputDialog.Ask(this, "Rinomina scena", "Nome:", sc.Name);
        if (string.IsNullOrWhiteSpace(n)) return;
        sc.Name = n;
        ScenesList.Items.Refresh();
    }

    private void DuplicateScene(Scene sc)
    {
        if (_compositor is null) return;
        var copy = new Scene(sc.Name + " copia");
        SceneItem[] items;
        lock (_compositor.SceneLock) items = sc.Items.ToArray();
        foreach (var it in items)
        {
            if (it.Descriptor is null) continue;
            try
            {
                var desc = it.Descriptor.Clone();
                copy.Items.Add(new SceneItem(CreateSource(desc))
                {
                    Descriptor = desc, X = it.X, Y = it.Y, Width = it.Width, Height = it.Height, Visible = it.Visible,
                });
            }
            catch { }
        }
        _scenes.Add(copy);
        ScenesList.SelectedItem = copy;
    }

    // ---- Menu in alto ----

    private void OnOpenRecFolder(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrEmpty(_recFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : _recFolder;
        try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); } catch { }
    }
    private void OnMenuExit(object sender, RoutedEventArgs e) => Close();
    private void OnInfo(object sender, RoutedEventArgs e) => AboutDialog.Show(this);

    private static MenuItem Mi(string header, RoutedEventHandler? click = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (click is not null) mi.Click += click;
        return mi;
    }

    private void OnMainMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };

        var file = new MenuItem { Header = Loc.T("MenuFile") };
        file.Items.Add(Mi(Loc.T("MenuNewProject"), (_, _) => NewProject()));
        file.Items.Add(Mi(Loc.T("MenuSaveProject"), OnSaveProject));
        file.Items.Add(Mi(Loc.T("MenuLoadProject"), OnLoadProject));
        file.Items.Add(new Separator());
        file.Items.Add(Mi(Loc.T("MenuOpenRecFolder"), OnOpenRecFolder));
        file.Items.Add(new Separator());
        file.Items.Add(Mi(Loc.T("Exit"), OnMenuExit));
        menu.Items.Add(file);

        var edit = new MenuItem { Header = Loc.T("MenuEdit") };
        edit.Items.Add(Mi(Loc.T("MenuCenterSource"), OnMenuCenter, Selected is not null));
        edit.Items.Add(Mi(Loc.T("MenuFitScreen"), OnMenuFit, Selected is not null));
        edit.Items.Add(Mi(Loc.T("MenuResetTransform"), OnMenuReset, Selected is not null));
        menu.Items.Add(edit);

        var view = new MenuItem { Header = Loc.T("MenuView") };
        view.Items.Add(Mi(_studio ? Loc.T("MenuStudioOff") : Loc.T("MenuStudioOn"), OnMenuStudio));
        var d3d = new MenuItem { Header = Loc.T("MenuGpuPreview"), IsCheckable = true, IsChecked = _useD3DImage };
        d3d.Click += (_, _) => SetD3DImageMode(_d3dPreview is null);
        view.Items.Add(d3d);
        menu.Items.Add(view);

        var profile = new MenuItem { Header = Loc.T("Profile") };
        profile.Items.Add(Mi(Loc.T("MenuProfileDefault"), null, false));
        profile.Items.Add(Mi(Loc.T("MenuProfileSettings"), OnSettings));
        menu.Items.Add(profile);

        var coll = new MenuItem { Header = Loc.T("MenuSceneColl") };
        coll.Items.Add(Mi(Loc.T("MenuCurrent") + _collectionName, null, false));
        coll.Items.Add(new Separator());
        coll.Items.Add(Mi(Loc.T("NewScene"), OnAddScene));
        coll.Items.Add(Mi(Loc.T("MenuSaveColl"), OnSaveProject));
        coll.Items.Add(Mi(Loc.T("MenuLoadColl"), OnLoadProject));
        menu.Items.Add(coll);

        var tools = new MenuItem { Header = Loc.T("MenuTools") };
        tools.Items.Add(Mi(Loc.T("MenuSettingsDots"), OnSettings));
        tools.Items.Add(new Separator());
        tools.Items.Add(Mi(_ndiOut is null ? Loc.T("StartNdiOut") : Loc.T("StopNdiOut"), OnToggleNdiOutput));
        tools.Items.Add(new Separator());
        tools.Items.Add(Mi(Loc.T("Info"), OnInfo));
        menu.Items.Add(tools);

        menu.IsOpen = true;
    }

    // ---- Uscita NDI (Program out) ----
    private void OnToggleNdiOutput(object sender, RoutedEventArgs e)
    {
        if (_compositor is null) return;

        if (_ndiOut is null)
        {
            if (!NdiLib.IsAvailable)
            {
                MessageBox.Show(this, Loc.T("WarnNdiMsg"),
                    Loc.T("WarnNdiTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                var o = new NdiOutput("Nexus Streamer", _compositor.CanvasWidth, _compositor.CanvasHeight, _fps, _mixer);
                _ndiOut = o;
                AttachSink(o);
                StatusText.Text = Loc.T("MsgNdiOutOn");
            }
            catch (Exception ex) { StatusText.Text = Loc.T("ErrNdiOut") + ex.Message; }
        }
        else
        {
            StopNdiOutput();
            StatusText.Text = Loc.T("MsgNdiOutOff");
        }
    }

    private void StopNdiOutput()
    {
        if (_ndiOut is null) return;
        var o = _ndiOut; _ndiOut = null;
        DetachSink(o);
        o.Dispose();
    }

    private void SetLive(bool live)
    {
        if (LiveLabel is null) return;
        LiveLabel.Text = live ? Loc.T("Live") : Loc.T("Offline");
        LiveDot.Fill = (System.Windows.Media.Brush)FindResource(live ? "B.Rec" : "B.TextDim");
        LiveBadge.Background = (System.Windows.Media.Brush)FindResource(live ? "B.Rec" : "B.Btn");
        LiveLabel.Foreground = live ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("B.Text");
    }

    private void UpdateSessionHeader()
    {
        if (SessionText is not null) SessionText.Text = _collectionName;
    }
    private void OnMenuCenter(object sender, RoutedEventArgs e) { if (Selected is { } s) CenterItem(s); }
    private void OnMenuFit(object sender, RoutedEventArgs e) { if (Selected is { } s) FitItem(s); }
    private void OnMenuReset(object sender, RoutedEventArgs e) { if (Selected is { } s) ResetItem(s); }
    private void OnMenuStudio(object sender, RoutedEventArgs e)
    {
        StudioCheck.IsChecked = !(StudioCheck.IsChecked == true);
        OnStudioToggle(StudioCheck, new RoutedEventArgs());
    }

    private void SetupPreviewBitmap()
    {
        if (_compositor is null) return;
        _wb = new WriteableBitmap(_compositor.PreviewWidth, _compositor.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
        PreviewImage.Source = _wb;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_compositor is null) return;

        // Modalità zero-copy: la texture condivisa è già in VRAM, basta invalidare il D3DImage.
        if (_d3dPreview is not null)
        {
            if (_compositor.TakeSharedReady()) _d3dPreview.Invalidate();
            return;
        }

        if (_wb is null) return;
        if (!_compositor.TakeFrameReady()) return;
        _wb.Lock();
        try
        {
            _compositor.CopyPreviewPixels(_wb.BackBuffer, _wb.BackBufferStride);
            _wb.AddDirtyRect(new Int32Rect(0, 0, _wb.PixelWidth, _wb.PixelHeight));
        }
        finally { _wb.Unlock(); }
    }

    private Graphics.D3DImagePreview? _d3dPreview;
    private bool _useD3DImage;

    /// <summary>Attiva/disattiva l'anteprima GPU zero-copy (D3DImage). Fallback automatico su WriteableBitmap.</summary>
    private void SetD3DImageMode(bool on)
    {
        if (_gd is null || _compositor is null) return;
        if (on == (_d3dPreview is not null)) return;

        if (on)
        {
            try
            {
                var p = new Graphics.D3DImagePreview(_gd, _compositor.PreviewWidth, _compositor.PreviewHeight);
                _compositor.SetSharedPreview(p.SharedTexture);
                PreviewImage.Source = p.Image;
                _d3dPreview = p;
                _useD3DImage = true;
                StatusText.Text = Loc.T("MsgGpuOn");
            }
            catch (Exception ex)
            {
                _useD3DImage = false;
                StatusText.Text = Loc.T("MsgGpuFail") + ex.Message;
            }
        }
        else
        {
            _compositor.SetSharedPreview(null);
            PreviewImage.Source = _wb;
            var old = _d3dPreview; _d3dPreview = null; _useD3DImage = false;
            old?.Dispose();
            StatusText.Text = Loc.T("MsgCpuPreview");
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_gd is not null) return; // una sola volta
        MigrateDataFolder();         // sposta i dati dalla vecchia cartella, se presente
        _gd = new GraphicsDevice();
        _compositor = new Compositor(_gd, _canvasW, _canvasH);
        _compositor.RenderFps = Math.Max(60, _fps); // render >= fps output per fluidita'

        SetupPreviewBitmap();
        CompositionTarget.Rendering += OnRendering;

        _compositor.Start();

        _mixer = new AudioMixer();
        MixerPanel.ItemsSource = _mixer.Channels;
        _vuTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _vuTimer.Tick += (_, _) =>
        {
            foreach (var ch in _mixer.Channels) ch.UpdateDisplay();
            UpdateStats();
        };
        _vuTimer.Start();

        var args = Environment.GetCommandLineArgs();
        int li = Array.IndexOf(args, "--lang");
        if (li >= 0 && li + 1 < args.Length) Loc.Set(args[li + 1]); // test/override lingua da riga di comando

        if (args.Contains("--selftest")) { SeedDefaultScene(); RunSelfTest("mp4", null); return; }
        if (args.Contains("--selftest-flv")) { SeedDefaultScene(); RunSelfTest("flv", "flv"); return; }
        if (args.Contains("--selftest-cam")) { RunCamTest(); return; }
        if (args.Contains("--selftest-text")) { SeedDefaultScene(); RunTextTest(); return; }
        if (args.Contains("--selftest-project")) { SeedDefaultScene(); RunProjectTest(); return; }
        if (args.Contains("--selftest-preview")) { SeedDefaultScene(); RunPreviewTest(); return; }
        if (args.Contains("--selftest-ui")) { SeedDefaultScene(); RunUiTest(); return; }
        if (args.Contains("--selftest-about")) { RunAboutTest(); return; }
        if (args.Contains("--selftest-studio")) { RunStudioTest(); return; }
        if (args.Contains("--selftest-webcam")) { RunWebcamPropsTest(); return; }
        if (args.Contains("--selftest-settings")) { RunSettingsTest(); return; }
        if (args.Contains("--selftest-media")) { SeedDefaultScene(); RunMediaTest(); return; }
        if (args.Contains("--selftest-del")) { SeedDefaultScene(); RunDelTest(); return; }
        if (args.Contains("--selftest-z")) { RunZTest(); return; }
        if (args.Contains("--dumptheme")) { RunDumpTheme(); return; }
        if (args.Contains("--selftest-stream")) { RunStreamTest(); return; }
        if (args.Contains("--selftest-edit")) { RunEditTest(); return; }
        if (args.Contains("--selftest-scte")) { RunScteTest(); return; }

        // Carica l'ultimo progetto se esiste, altrimenti scena di default.
        if (File.Exists(DefaultProjectPath)) LoadProject(DefaultProjectPath);
        else SeedDefaultScene();

        SetLive(false);
        UpdateSessionHeader();
        if (args.Contains("--d3dimage")) SetD3DImageMode(true);
        TryLoadLayout(); // ripristina la disposizione pannelli dell'ultima sessione

        Loc.LanguageChanged += ApplyLanguage; // aggiorna titoli pannelli e testi dinamici al cambio lingua
        ApplyLanguage();

        // Autosave periodico: il progetto resta salvato anche se l'app si chiude male.
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _autoSaveTimer.Tick += (_, _) => { try { SaveProject(DefaultProjectPath); } catch { } };
        _autoSaveTimer.Start();

        StatusText.Text = Loc.T("MsgRenderReady");
    }

    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

    private void NewProject()
    {
        ClearAllScenes();
        _collectionName = Loc.T("Untitled");
        SeedDefaultScene();
        UpdateSessionHeader();
        StatusText.Text = Loc.T("MsgNewProject");
    }

    private void SeedDefaultScene()
    {
        NewScene();
        AddFromDescriptor(new SourceDescriptor
        {
            Kind = SourceKind.Color,
            R = 32, G = 32, B = 32,
            ColorW = _compositor!.CanvasWidth,
            ColorH = _compositor.CanvasHeight,
        });
    }

    private void RunPreviewTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_preview.png");
        var mons = Monitors.Enumerate();
        if (mons.Count > 0)
            AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Monitor, Name = mons[0].Name });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromSeconds(2)) return;
            timer.Stop();
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(_wb!));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    // Verifica end-to-end SCTE-35: registra un breve MPEG-TS con uno splice iniettato, poi lo
    // riapre e conta i pacchetti sullo stream dati SCTE-35. Nessuna GPU: frame NV12 sintetici.
    private void RunScteTest()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "streamingdoc_scte.ts");
            var log = path + ".log";
            try
            {
                FfmpegLoader.EnsureLoaded();
                int w = 640, h = 360, fps = 30;
                byte[] section = Scte35.BuildSpliceInsert(1234, true, 30);
                string codec;
                using (var rec = new VideoRecorder(path, w, h, fps, 2_000_000, "mpegts", null, "auto", enableScte: true))
                {
                    codec = rec.CodecName;
                    if (!rec.ScteReady) throw new InvalidOperationException("stream SCTE-35 non creato dal muxer");
                    IntPtr y = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h);
                    IntPtr uv = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h / 2);
                    try
                    {
                        FillUnmanaged(y, w * h, 0x40);
                        FillUnmanaged(uv, w * h / 2, 0x80);
                        long tick = 10_000_000L / fps;
                        for (int i = 0; i < 45; i++)
                        {
                            rec.WriteFrameNv12(y, w, uv, w, w, h, i * tick);
                            if (i == 22) { Thread.Sleep(150); rec.WriteSpliceInsert(section); } // splice a metà
                            Thread.Sleep(5);
                        }
                        Thread.Sleep(250);
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(y);
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(uv);
                    }
                }
                File.WriteAllText(log, $"codec={codec} sectionLen={section.Length} :: {VerifyScte(path)}");
            }
            catch (Exception ex) { try { File.WriteAllText(log, "ERR: " + ex.Message); } catch { } }
            finally { try { Dispatcher.Invoke(() => Application.Current.Shutdown()); } catch { } }
        });
    }

    private static unsafe void FillUnmanaged(IntPtr p, int len, byte val)
        => new Span<byte>((void*)p, len).Fill(val);

    // Verifica demuxer-indipendente: scansione dei byte MPEG-TS. Conferma (1) il PMT con
    // stream_type 0x86 + registration descriptor "CUEI", (2) i pacchetti sul PID SCTE con la
    // splice_info_section (table_id 0xFC). Prova la conformità reale del flusso.
    private static string VerifyScte(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        const int sctePid = 0x01F0;

        bool pmtScte = false;
        for (int i = 0; i + 6 <= b.Length && !pmtScte; i++)
            if (b[i] == 0x05 && b[i + 1] == 0x04 && b[i + 2] == (byte)'C' && b[i + 3] == (byte)'U' && b[i + 4] == (byte)'E' && b[i + 5] == (byte)'I')
                for (int j = Math.Max(0, i - 8); j < i; j++)
                    if (b[j] == 0x86) { pmtScte = true; break; }

        int sctePkts = 0; bool section = false;
        for (int o = 0; o + 188 <= b.Length; o += 188)
        {
            if (b[o] != 0x47) return $"TS non allineato @ {o}";
            int pid = ((b[o + 1] & 0x1F) << 8) | b[o + 2];
            if (pid == sctePid)
            {
                sctePkts++;
                bool pusi = (b[o + 1] & 0x40) != 0;
                if (pusi && b[o + 4] == 0x00 && b[o + 5] == 0xFC) section = true; // splice_info_section
            }
        }
        return $"pmtScte={pmtScte} sctePID_pkts={sctePkts} spliceSection={section} pkts={b.Length / 188}";
    }

    private void RunDumpTheme()
    {
        var sb = new StringBuilder();
        void Walk(ResourceDictionary d, int depth)
        {
            foreach (var k in d.Keys)
            {
                object? v = null;
                try { v = d[k]; } catch { }
                if (v is System.Windows.Media.SolidColorBrush b) sb.AppendLine($"{k}\tBrush\t{b.Color}");
                else if (v is System.Windows.Media.Color c) sb.AppendLine($"{k}\tColor\t{c}");
                else if (v is System.Windows.Media.LinearGradientBrush) sb.AppendLine($"{k}\tGradient");
            }
            foreach (var md in d.MergedDictionaries) Walk(md, depth + 1);
        }
        try
        {
            var uri = new AvalonDock.Themes.Vs2013DarkTheme().GetResourceUri();
            var dict = new ResourceDictionary { Source = uri };
            Walk(dict, 0);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "theme.txt"), sb.ToString());
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "theme.txt"), ex.ToString()); }
        Application.Current.Shutdown();
    }

    private unsafe void RunZTest()
    {
        var log = Path.Combine(Path.GetTempPath(), "streamingdoc_z.log");
        NewScene();
        int cw = _compositor!.CanvasWidth, chh = _compositor.CanvasHeight;
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 255, G = 0, B = 0, ColorW = cw, ColorH = chh }); // 1° = cima = davanti
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 0, G = 0, B = 255, ColorW = cw, ColorH = chh }); // 2° = sotto = dietro

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(1200)) return;
            timer.Stop();
            try
            {
                _wb!.Lock();
                int x = _wb.PixelWidth / 2, y = _wb.PixelHeight / 2;
                byte* p = (byte*)_wb.BackBuffer + y * _wb.BackBufferStride + x * 4;
                File.WriteAllText(log, $"centro BGRA = {p[0]},{p[1]},{p[2]} (rosso atteso = 0,0,255 davanti)");
                _wb.Unlock();
            }
            catch (Exception ex) { File.WriteAllText(log, "FAIL: " + ex); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunDelTest()
    {
        var log = Path.Combine(Path.GetTempPath(), "streamingdoc_del.log");
        var path = Path.Combine(Path.GetTempPath(), "streamingdoc_selftest.mp4");
        if (File.Exists(path))
            AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Media, Name = path });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(1500)) return;
            timer.Stop();
            try
            {
                // Elimina TUTTE le sorgenti correnti (come fa il tasto destro -> Elimina).
                var items = _currentScene!.Items.ToArray();
                foreach (var it in items)
                {
                    lock (_compositor!.SceneLock) _currentScene.Items.Remove(it);
                    DisposeSource(it.Source);
                }
                File.WriteAllText(log, $"OK rimosse={items.Length} canaliMixer={_mixer?.Channels.Count}");
            }
            catch (Exception ex) { File.WriteAllText(log, "FAIL: " + ex); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunEditTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_edit.png");
        NewScene();
        int cw = _compositor!.CanvasWidth, ch = _compositor.CanvasHeight;
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 30, G = 30, B = 30, ColorW = cw, ColorH = ch }); // sfondo
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 40, G = 200, B = 120, ColorW = cw, ColorH = ch }); // sorgente che sborda
        var it = _currentScene!.Items[0]; // ultima aggiunta = in cima
        lock (_compositor.SceneLock) { it.X = -cw * 0.30; it.Y = ch * 0.12; it.Width = cw * 0.8; it.Height = ch * 0.8; }
        SourcesList.SelectedItem = it;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(1200)) return;
            timer.Stop();
            try
            {
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                if (w <= 0) { w = 1560; h = 900; }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunStreamTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_stream.png");
        NewScene();
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Media, Name = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromSeconds(8)) return;
            timer.Stop();
            try
            {
                var ms = _currentScene?.Items.Select(i => i.Source).OfType<MediaSource>().FirstOrDefault();
                int chans = _mixer?.Channels.Count ?? 0;
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                if (w <= 0) { w = 1560; h = 900; }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
                File.WriteAllText(png + ".log", $"mediaVideo={ms?.Width}x{ms?.Height} canaliMixer={chans} frames={_compositor?.FrameCount}");
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunMediaTest()
    {
        var path = Path.Combine(Path.GetTempPath(), "streamingdoc_selftest.mp4");
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_media.png");
        if (File.Exists(path))
            AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Media, Name = path });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(1500)) return;
            timer.Stop();
            try
            {
                int chans = _mixer?.Channels.Count ?? 0;
                bool mediaCh = _mixer?.Channels.Any(c => c.Removable) ?? false;
                double pk = _mixer?.Channels.FirstOrDefault(c => c.Removable)?.Peak ?? -1;
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                if (w <= 0) w = 1560; if (h <= 0) h = 900;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
                File.WriteAllText(png + ".log", $"channels={chans} mediaAudio={mediaCh} mediaPeak={pk:F3}");
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunSettingsTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_settings.png");
        var dlg = new SettingsWindow(1920, 1080, 30, 10_000_000, 6_000_000, "", "mp4", "auto",
            "rtmp://live.example.com/app", "abc123streamkey", "broadcaster", "s3cret", true)
        { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        dlg.Show();
        dlg.Nav.SelectedIndex = 3; // tab Stream
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(600)) return;
            timer.Stop();
            try
            {
                dlg.UpdateLayout();
                int w = (int)Math.Round(dlg.ActualWidth), h = (int)Math.Round(dlg.ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dlg);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            dlg.Close();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunWebcamPropsTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_webcam.png");
        var dlg = new WebcamPropsDialog(Interop.DirectShowDevices.AudioInputNames(), true, null) { Owner = this };
        dlg.Show();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(600)) return;
            timer.Stop();
            try
            {
                dlg.UpdateLayout();
                int w = (int)Math.Round(dlg.ActualWidth), h = (int)Math.Round(dlg.ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dlg);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            dlg.Close();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunStudioTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_studio.png");
        int cw = _compositor!.CanvasWidth, ch = _compositor.CanvasHeight;
        var s1 = NewScene();   // Scena 1 (Program) = rosso
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 200, G = 40, B = 40, ColorW = cw, ColorH = ch });
        var s2 = NewScene();   // Scena 2 (Preview) = blu
        AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Color, R = 40, G = 90, B = 220, ColorW = cw, ColorH = ch });
        ScenesList.SelectedItem = s1;                        // Program = scena 1 (rosso)
        StudioCheck.IsChecked = true;
        OnStudioToggle(StudioCheck, new RoutedEventArgs());
        ScenesList.SelectedItem = s2;                        // Preview = scena 2 (blu)
        WindowState = WindowState.Maximized;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(1300)) return;
            timer.Stop();
            try
            {
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunAboutTest()
    {
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_about.png");
        var dlg = new AboutDialog { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        dlg.Show();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(700)) return;
            timer.Stop();
            try
            {
                dlg.UpdateLayout();
                int w = (int)Math.Round(dlg.ActualWidth), h = (int)Math.Round(dlg.ActualHeight);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dlg);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            dlg.Close();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunUiTest()
    {
        ApplyLanguage(); // localizza titoli pannelli + pulsanti dinamici anche nel selftest
        // Cattura affidabile dell'albero visuale reale (no screen capture/DWM).
        if (Environment.GetCommandLineArgs().Contains("--max")) WindowState = WindowState.Maximized;
        var png = Path.Combine(Path.GetTempPath(), "streamingdoc_ui.png");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromMilliseconds(900)) return;
            timer.Stop();
            try
            {
                UpdateLayout();
                int w = (int)Math.Round(ActualWidth);
                int h = (int)Math.Round(ActualHeight);
                if (w <= 0) w = 1560;
                if (h <= 0) h = 900;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(png);
                enc.Save(fs);
                File.WriteAllText(png + ".log", $"actual={ActualWidth}x{ActualHeight} rendered={w}x{h}");
            }
            catch (Exception ex) { File.WriteAllText(png + ".log", ex.ToString()); }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunTextTest()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "streamingdoc_text.log");
        try
        {
            var t = new TextSource(_gd!, "Nexus Streamer", 96);
            File.WriteAllText(logPath, $"text w={t.Width} h={t.Height} srv={t.GetSrv() is not null}");
            AddItem(t); // lascialo in scena per verifica visiva
        }
        catch (Exception ex) { File.WriteAllText(logPath, "fail: " + ex.Message); }
        Application.Current.Shutdown();
    }

    private void RunCamTest()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "streamingdoc_cam.log");
        var cams = Devices.VideoCaptureNames();
        if (cams.Count == 0) { File.WriteAllText(logPath, "no-cam"); Application.Current.Shutdown(); return; }

        WebcamSource src;
        try { src = new WebcamSource(_gd!, cams[0]); }
        catch (Exception ex) { File.WriteAllText(logPath, "open-fail: " + ex.Message); Application.Current.Shutdown(); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromSeconds(1.5)) return;
            timer.Stop();
            bool hasFrame = src.GetSrv() is not null;
            File.WriteAllText(logPath, $"cam={cams[0]} w={src.Width} h={src.Height} frame={hasFrame}");
            src.Dispose();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RunSelfTest(string ext, string? format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"streamingdoc_selftest.{ext}");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        VideoRecorder rec;
        try { rec = new VideoRecorder(path, _compositor!.CanvasWidth, _compositor.CanvasHeight, 30, 8_000_000, format, _mixer); }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".log", "INIT_FAIL: " + ex);
            Application.Current.Shutdown();
            return;
        }

        _compositor.SetSink(rec);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (sw.Elapsed < TimeSpan.FromSeconds(2)) return;
            timer.Stop();
            _compositor!.SetSink(null);
            rec.Dispose();
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            File.WriteAllText(path + ".log", $"codec={rec.CodecName} failed={rec.Failed} size={size} audioErr={rec.AudioError ?? "none"}");
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    // ---- Scene ----

    private Scene NewScene()
    {
        var scene = new Scene($"Scena {++_sceneCounter}");
        _scenes.Add(scene);
        ScenesList.SelectedItem = scene;
        return scene;
    }

    private void OnAddScene(object sender, RoutedEventArgs e)
    {
        if (_compositor is null) return;
        NewScene();
    }

    private void OnRemoveScene(object sender, RoutedEventArgs e)
    {
        if (_currentScene is null || _scenes.Count <= 1) return;
        var scene = _currentScene;
        _scenes.Remove(scene);
        ScenesList.SelectedIndex = 0;
        foreach (var item in scene.Items) DisposeSource(item.Source);
    }

    private void OnSceneSelected(object sender, SelectionChangedEventArgs e)
    {
        _currentScene = ScenesList.SelectedItem as Scene;
        SourcesList.ItemsSource = _currentScene?.Items;

        if (SceneTabs != null && !_syncTabs) { _syncTabs = true; SceneTabs.SelectedItem = _currentScene; _syncTabs = false; }

        if (_compositor is null) return;
        _compositor.SetEditScene(_currentScene);
        if (_studio)
        {
            _previewScene = _currentScene;          // in studio la scena scelta è il Preview; il Program resta
        }
        else
        {
            _programScene = _currentScene;          // fuori studio la selezione va in onda
            _compositor.TransitionTo(_currentScene);
        }
        UpdateAudioRouting();
    }

    // ---- Localizzazione ----

    private void ApplyLanguage()
    {
        // Titoli pannelli (AvalonDock: Title non è bindabile -> via codice).
        SetPaneTitle("scenes", Loc.T("Scene"));
        SetPaneTitle("sources", Loc.T("Sources"));
        SetPaneTitle("preview", Loc.T("Preview"));
        SetPaneTitle("status", Loc.T("Status"));
        SetPaneTitle("controls", Loc.T("Controls"));
        SetPaneTitle("mixer", Loc.T("AudioMixer"));
        SetPaneTitle("transitions", Loc.T("Transitions"));
        SetPaneTitle("props", Loc.T("SourceProps"));

        // Pulsanti dinamici: riflettono lo stato corrente nella lingua scelta.
        GoLiveButton.Content = _streamer is null ? Loc.T("StartStreaming") : Loc.T("StopStreaming");
        RecordButton.Content = _recorder is null ? Loc.T("StartRecording") : Loc.T("StopRecording");
        VirtualCamButton.Content = (string?)VirtualCamButton.Tag == "active" ? Loc.T("StopVCam") : Loc.T("StartVCam");

        if (SourcesList.SelectedItem is not SceneItem) SelSourceText.Text = Loc.T("NoSourceSelected");
        SetLive(_streamer is not null);
    }

    private void SetPaneTitle(string contentId, string title)
    {
        foreach (var c in Docker.Layout.Descendents().OfType<AvalonDock.Layout.LayoutContent>())
            if (c.ContentId == contentId) { c.Title = title; return; }
    }

    // ---- Studio mode + transizioni ----
    // Studio: Preview (sx, ciò che prepari) | Program (dx, ciò che è in onda). Si sente solo il Program.

    private Scene? _programScene;  // scena in onda
    private Scene? _previewScene;  // scena in preparazione

    private void OnStudioToggle(object sender, RoutedEventArgs e)
    {
        _studio = StudioCheck.IsChecked == true;
        _compositor?.SetStudio(_studio);
        if (_studio)
        {
            _programScene = _currentScene;
            _previewScene = _currentScene;          // si parte uguali; scegliendo un'altra scena diventa il Preview
            _compositor?.SetScene(_programScene);
            _compositor?.SetEditScene(_previewScene);
            if (StudioLabels != null) StudioLabels.Visibility = Visibility.Visible;
        }
        else
        {
            _programScene = _currentScene;
            _compositor?.SetScene(_currentScene);    // uscendo: l'edit diventa Program
            if (StudioLabels != null) StudioLabels.Visibility = Visibility.Collapsed;
        }
        TransitionButton.IsEnabled = _studio;
        UpdateAudioRouting();
    }

    private void OnTransition(object sender, RoutedEventArgs e)
    {
        if (_compositor is null || !_studio) return;

        // Scambio: il Preview va in onda, il vecchio Program torna in Preview.
        var old = _programScene;
        _programScene = _previewScene;
        _previewScene = old;

        _compositor.TransitionTo(_programScene);     // anima il Program verso la (ex) Preview
        _compositor.SetEditScene(_previewScene);     // il Preview ora mostra l'ex Program
        _currentScene = _previewScene;
        SourcesList.ItemsSource = _previewScene?.Items;
        if (_previewScene is not null && !ReferenceEquals(ScenesList.SelectedItem, _previewScene))
            ScenesList.SelectedItem = _previewScene; // la selezione segue il Preview
        UpdateAudioRouting();
    }

    /// <summary>In onda (Program) si sente solo l'audio delle sorgenti della scena Program; le altre a 0.</summary>
    private void UpdateAudioRouting()
    {
        Scene? live = _programScene ?? _currentScene;
        foreach (var scene in _scenes)
        {
            bool on = ReferenceEquals(scene, live);
            foreach (var item in scene.Items)
                if (item.Source is IAudioSource a && a.AudioChannel is not null)
                    a.AudioChannel.Audible = on;
        }
        // Desktop/Microfono non appartengono a scene: restano sempre udibili.
    }

    private void OnTransitionSettingsChanged(object sender, RoutedEventArgs e) => ApplyTransitionSettings();

    // ---- Audio mixer ---- (gain/mute via binding TwoWay sulla strip; niente handler dedicati)

    private void ApplyTransitionSettings()
    {
        if (_compositor is null) return;
        var type = (TransitionCombo?.SelectedIndex ?? 1) switch
        {
            0 => TransitionType.Cut,
            2 => TransitionType.Slide,
            _ => TransitionType.Fade,
        };
        double ms = double.TryParse(TransitionMs?.Text, out var m) ? m : 350;
        _compositor.SetTransition(type, ms);
    }

    // ---- Sorgenti ----

    private void OnAddSource(object sender, RoutedEventArgs e)
    {
        if (_gd is null || _currentScene is null) return;

        var menu = new ContextMenu { PlacementTarget = AddSourceButton };

        var monitorRoot = new MenuItem { Header = "Monitor" };
        foreach (var m in Monitors.Enumerate())
        {
            var mi = new MenuItem { Header = $"{m.Name} ({m.Width}x{m.Height})" };
            var name = m.Name;
            mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Monitor, Name = name });
            monitorRoot.Items.Add(mi);
        }
        menu.Items.Add(monitorRoot);

        var windowRoot = new MenuItem { Header = Loc.T("AddWindow") };
        var self = new WindowInteropHelper(this).Handle;
        foreach (var win in WindowEnum.Enumerate(self))
        {
            var mi = new MenuItem { Header = win.Title };
            var title = win.Title;
            mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Window, Name = title });
            windowRoot.Items.Add(mi);
        }
        menu.Items.Add(windowRoot);

        var webcamRoot = new MenuItem { Header = "Webcam" };
        foreach (var cam in Devices.VideoCaptureNames())
        {
            var mi = new MenuItem { Header = cam };
            var name = cam;
            mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Webcam, Name = name });
            webcamRoot.Items.Add(mi);
        }
        if (webcamRoot.Items.Count == 0)
            webcamRoot.Items.Add(new MenuItem { Header = Loc.T("NoneF"), IsEnabled = false });
        menu.Items.Add(webcamRoot);

        // Cattura audio: device WASAPI di ingresso + uscita (loopback) — copre i device virtuali dei playout.
        var audioRoot = new MenuItem { Header = Loc.T("AddAudioCapture") };
        var capDevs = AudioDevices.CaptureDevices();
        var renDevs = AudioDevices.RenderDevices();
        foreach (var dev in capDevs)
        {
            var mi = new MenuItem { Header = dev.Name };
            var nm = dev.Name; var id = dev.Id;
            mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.AudioDevice, Name = nm, DeviceId = id, Loopback = false });
            audioRoot.Items.Add(mi);
        }
        if (capDevs.Count > 0 && renDevs.Count > 0) audioRoot.Items.Add(new Separator());
        foreach (var dev in renDevs)
        {
            var mi = new MenuItem { Header = Loc.T("OutputPrefix") + dev.Name };
            var nm = dev.Name; var id = dev.Id;
            mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.AudioDevice, Name = nm, DeviceId = id, Loopback = true });
            audioRoot.Items.Add(mi);
        }
        if (audioRoot.Items.Count == 0)
            audioRoot.Items.Add(new MenuItem { Header = Loc.T("NoneM"), IsEnabled = false });
        menu.Items.Add(audioRoot);

        // NDI: sorgenti video/audio in rete (altre regie, playout, telecamere NDI).
        var ndiRoot = new MenuItem { Header = Loc.T("AddNdiNet") };
        if (!NdiLib.IsAvailable)
            ndiRoot.Items.Add(new MenuItem { Header = Loc.T("NdiNotInstalled"), IsEnabled = false });
        else
        {
            foreach (var s in NdiFinder.ListSources(700))
            {
                var mi = new MenuItem { Header = s };
                var nm = s;
                mi.Click += (_, _) => AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Ndi, Name = nm });
                ndiRoot.Items.Add(mi);
            }
            if (ndiRoot.Items.Count == 0)
                ndiRoot.Items.Add(new MenuItem { Header = Loc.T("NoNdiSources"), IsEnabled = false });
            ndiRoot.Items.Add(new Separator());
            ndiRoot.Items.Add(Mi(Loc.T("RefreshList"), (_, _) => OnAddSource(AddSourceButton, new RoutedEventArgs())));
        }
        menu.Items.Add(ndiRoot);

        var media = new MenuItem { Header = Loc.T("AddMedia") };
        media.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Media|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.mp3;*.wav;*.flac;*.m4a;*.aac|Tutti|*.*"
            };
            if (dlg.ShowDialog() == true)
                AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Media, Name = dlg.FileName });
        };
        menu.Items.Add(media);

        var stream = new MenuItem { Header = Loc.T("AddNetStream") };
        stream.Click += (_, _) =>
        {
            var url = InputDialog.Ask(this, Loc.T("NetStreamTitle"),
                Loc.T("NetStreamPrompt"), "https://");
            if (!string.IsNullOrWhiteSpace(url) && url.Contains("://"))
                AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Media, Name = url.Trim() });
        };
        menu.Items.Add(stream);

        var image = new MenuItem { Header = Loc.T("AddImage") };
        image.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tutti|*.*"
            };
            if (dlg.ShowDialog() == true)
                AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Image, Name = dlg.FileName });
        };
        menu.Items.Add(image);

        var text = new MenuItem { Header = Loc.T("AddText") };
        text.Click += (_, _) =>
            AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Text, Name = "Nexus Streamer", FontSize = 96 });
        menu.Items.Add(text);

        var color = new MenuItem { Header = Loc.T("AddColor") };
        color.Click += (_, _) => AddFromDescriptor(new SourceDescriptor
        {
            Kind = SourceKind.Color, R = 32, G = 32, B = 32,
            ColorW = _compositor!.CanvasWidth, ColorH = _compositor.CanvasHeight,
        });
        menu.Items.Add(color);

        menu.IsOpen = true;
    }

    private ISource CreateSource(SourceDescriptor d)
    {
        ISource src;
        switch (d.Kind)
        {
            case SourceKind.Monitor:
            {
                var mons = Monitors.Enumerate();
                var m = mons.FirstOrDefault(x => x.Name == d.Name);
                if (m.Handle == IntPtr.Zero) m = mons.FirstOrDefault(x => x.Primary);
                if (m.Handle == IntPtr.Zero && mons.Count > 0) m = mons[0];
                src = new MonitorSource(_gd!, m); break;
            }
            case SourceKind.Window:
            {
                var self = new WindowInteropHelper(this).Handle;
                var win = WindowEnum.Enumerate(self).FirstOrDefault(x => x.Title == d.Name);
                if (win.Handle == IntPtr.Zero) throw new InvalidOperationException("Finestra non trovata: " + d.Name);
                src = new WindowSource(_gd!, win); break;
            }
            case SourceKind.Webcam:
                src = new WebcamSource(_gd!, d.Name ?? "", d.UseCustomAudio ? d.AudioDeviceName : null); break;
            case SourceKind.Media: src = new MediaSource(_gd!, d.Name ?? ""); break;
            case SourceKind.Image: src = new Sources.ImageSource(_gd!, d.Name ?? ""); break;
            case SourceKind.Text: src = new TextSource(_gd!, d.Name ?? "", d.FontSize); break;
            case SourceKind.Color: src = new ColorSource(_gd!, (byte)d.R, (byte)d.G, (byte)d.B, d.ColorW, d.ColorH); break;
            case SourceKind.AudioDevice: src = new AudioCaptureSource(d.Name ?? "Audio", d.DeviceId ?? "", d.Loopback); break;
            case SourceKind.Ndi: src = new NdiSource(_gd!, d.Name ?? ""); break;
            default: throw new InvalidOperationException("Tipo sorgente sconosciuto.");
        }
        // Sorgenti con audio (Media/AudioDevice/NDI): aggiunge il canale al mixer.
        if (src is IAudioSource aus && aus.AudioChannel is not null) _mixer?.AddChannel(aus.AudioChannel);
        return src;
    }

    private void AddFromDescriptor(SourceDescriptor d)
    {
        try { AddItem(CreateSource(d), d); }
        catch (Exception ex) { StatusText.Text = Loc.T("ErrAdd") + ex.Message; }
    }

    private void AddItem(ISource source, SourceDescriptor? desc = null)
    {
        if (_compositor is null || _currentScene is null) { source.Dispose(); return; }

        int cw = _compositor.CanvasWidth, ch = _compositor.CanvasHeight;
        double w, h;
        if (source.Width <= 0 || source.Height <= 0)
        {
            w = cw; h = ch;
        }
        else
        {
            double s = Math.Min(Math.Min((double)cw / source.Width, (double)ch / source.Height), 1.0);
            w = source.Width * s; h = source.Height * s;
        }

        var item = new SceneItem(source)
        {
            Descriptor = desc,
            X = (cw - w) / 2,
            Y = (ch - h) / 2,
            Width = w,
            Height = h,
        };

        lock (_compositor.SceneLock) _currentScene.Items.Add(item);
        SourcesList.SelectedItem = item;
        UpdateAudioRouting();   // il canale si sente solo se la sua scena è in onda
        StatusText.Text = Loc.T("MsgSourceAdded") + source.Name;
    }

    private void OnRemoveSource(object sender, RoutedEventArgs e)
    {
        if (_compositor is null || _currentScene is null) return;
        if (SourcesList.SelectedItem is not SceneItem item) return;
        lock (_compositor.SceneLock) _currentScene.Items.Remove(item);
        DisposeSource(item.Source);
        UpdateAudioRouting();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (_compositor is null || _currentScene is null) return;
        if (SourcesList.SelectedItem is not SceneItem item) return;
        var items = _currentScene.Items;
        int i = items.IndexOf(item);
        int j = i + delta;
        if (j < 0 || j >= items.Count) return;
        lock (_compositor.SceneLock) items.Move(i, j);
        SourcesList.SelectedItem = item;
    }

    private void DisposeSource(ISource source)
    {
        // Niente ContextLock qui: ogni ISource.Dispose ferma il proprio thread (Join) PRIMA di
        // bloccare il ContextLock per le free D3D. Tenere il lock qui causerebbe deadlock con il
        // thread di decodifica (Media/Webcam) bloccato su ContextLock dentro Upload.
        var channel = (source as IAudioSource)?.AudioChannel;
        source.Dispose();
        if (channel is not null) _mixer?.RemoveChannel(channel);
    }

    // ---- Sink (registrazione/streaming) ----

    private void AttachSink(IFrameSink sink)
    {
        _sink.Add(sink);
        if (_sink.Count == 1)
        {
            _compositor!.SinkFps = _fps;      // readback al fps target, non a 60
            _compositor.SetSink(_sink);       // attiva il readback solo se serve
        }
    }

    private void DetachSink(IFrameSink sink)
    {
        _sink.Remove(sink);
        if (_sink.Count == 0) _compositor!.SetSink(null);
    }

    // ---- Registrazione ----

    private void OnToggleRecord(object sender, RoutedEventArgs e)
    {
        if (_compositor is null) return;

        if (_recorder is null)
        {
            try
            {
                var dir = string.IsNullOrEmpty(_recFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : _recFolder;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"Nexus Streamer {DateTime.Now:yyyyMMdd_HHmmss}.{_recFormat}");
                var rec = new VideoRecorder(path, _compositor.CanvasWidth, _compositor.CanvasHeight, _fps,
                    _recBitrate, null, AudioCheck.IsChecked == true ? _mixer : null, _encoder);
                _recorder = rec;
                _recStart = DateTime.Now;
                AttachSink(rec);
                RecordButton.Content = Loc.T("StopRecording");
                RecordButton.Tag = "active";
                StatusText.Text = $"● REC [{rec.CodecName}] → {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText.Text = Loc.T("ErrRecStart") + ex.Message;
            }
        }
        else
        {
            StopRecording();
        }
    }

    private void StopRecording()
    {
        if (_recorder is null) return;
        var rec = _recorder;
        _recorder = null;
        _recStart = null;
        DetachSink(rec);
        rec.Dispose();
        RecordButton.Content = Loc.T("StartRecording");
        RecordButton.Tag = null;
        StatusText.Text = rec.Failed
            ? Loc.T("MsgRecEndedErr")
            : Loc.T("MsgRecSaved") + rec.OutputPath;
    }

    // ---- Streaming RTMP ----

    private void OnToggleStream(object sender, RoutedEventArgs e)
    {
        if (_compositor is null) return;

        if (_streamer is null)
        {
            string url, format, target;

            if (string.Equals(_streamProtocol, "srt", StringComparison.OrdinalIgnoreCase))
            {
                // SRT porta MPEG-TS. Se l'utente ha inserito un URL SRT completo (con streamid/transtype/
                // token, es. per Restreamer/piattaforme), lo usiamo tale e quale. Altrimenti lo costruiamo
                // da host/porta/modo/latency. Caller = si connette a host:porta remoti; Listener = ascolta.
                format = "mpegts";
                if (!string.IsNullOrWhiteSpace(_srtUrl))
                {
                    url = _srtUrl.Trim();
                    target = url;
                }
                else
                {
                    var host = _srtHost.Trim();
                    if (string.Equals(_srtMode, "listener", StringComparison.OrdinalIgnoreCase) && host.Length == 0)
                        host = "0.0.0.0";
                    if (host.Length == 0)
                    {
                        StatusText.Text = Loc.T("MsgNoStreamUrl");
                        MessageBox.Show(this, Loc.T("WarnNoServerMsg"),
                            Loc.T("WarnNoServerTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    long latUs = (long)_srtLatencyMs * 1000;
                    url = $"srt://{host}:{_srtPort}?mode={_srtMode}&latency={latUs}&pkt_size=1316";
                    target = $"srt://{host}:{_srtPort} ({_srtMode})";
                }
            }
            else if (string.Equals(_streamProtocol, "rtmp", StringComparison.OrdinalIgnoreCase))
            {
                var server = _streamUrl.Trim().TrimEnd('/');
                var key = _streamKey.Trim();
                if (string.IsNullOrEmpty(server))
                {
                    StatusText.Text = Loc.T("MsgNoStreamUrl");
                    MessageBox.Show(this, Loc.T("WarnNoServerMsg"),
                        Loc.T("WarnNoServerTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Autenticazione RTMP: incorpora user:pass nell'authority (rtmp://user:pass@host/app)
                // — formato accettato dai server RTMP che richiedono credenziali.
                if (_streamUseAuth && !string.IsNullOrEmpty(_streamUser))
                {
                    int si = server.IndexOf("://", StringComparison.Ordinal);
                    if (si >= 0)
                    {
                        string scheme = server.Substring(0, si + 3);
                        string rest = server.Substring(si + 3);
                        server = scheme + Uri.EscapeDataString(_streamUser) + ":" + Uri.EscapeDataString(_streamPass) + "@" + rest;
                    }
                }

                url = string.IsNullOrEmpty(key) ? server : $"{server}/{key}";
                format = "flv";
                target = _streamUrl.Trim().TrimEnd('/'); // mostra il server SENZA credenziali
            }
            else // "none" / non impostato
            {
                StatusText.Text = Loc.T("MsgNoProtocol");
                MessageBox.Show(this, Loc.T("WarnNoProtocolMsg"),
                    Loc.T("WarnNoProtocolTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // SCTE-35 (ad marker): solo su MPEG-TS/SRT e se è impostato un file di playout.
            bool enableScte = format == "mpegts" && _scteEnabled && !string.IsNullOrWhiteSpace(_sctePlayoutFile);

            StatusText.Text = Loc.T("MsgConnecting");
            try
            {
                var s = new VideoRecorder(url, _compositor.CanvasWidth, _compositor.CanvasHeight, _fps,
                    _streamBitrate, format, AudioCheck.IsChecked == true ? _mixer : null, _encoder, enableScte);
                _streamer = s;
                _streamStart = DateTime.Now;
                AttachSink(s);
                GoLiveButton.Content = Loc.T("StopStreaming");
                GoLiveButton.Tag = "active";
                SetLive(true);
                StatusText.Text = $"LIVE [{s.CodecName}] → {target}";
                if (s.ScteReady) StartPlayoutWatcher(s);
            }
            catch (Exception ex)
            {
                // Server non raggiungibile / rifiutato: avviso e bottone resta grigio (non attivo).
                GoLiveButton.Tag = null;
                SetLive(false);
                StatusText.Text = Loc.T("MsgStreamFail");
                _ = ex;
                MessageBox.Show(this, string.Format(Loc.T("WarnConnFailMsg"), target),
                    Loc.T("WarnConnFailTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            var s = _streamer;
            _streamer = null;
            _streamStart = null;
            StopPlayoutWatcher();
            DetachSink(s);
            s.Dispose();
            GoLiveButton.Content = Loc.T("StartStreaming");
            GoLiveButton.Tag = null;
            SetLive(false);
            StatusText.Text = s.Failed ? Loc.T("MsgStreamEndedErr") : Loc.T("MsgStreamEnded");
        }
    }

    // --- SCTE-35: osservatore del playout ---
    // Avviato con lo stream SRT; a ogni break rilevato inietta uno splice_insert nel MPEG-TS.
    private void StartPlayoutWatcher(VideoRecorder streamer)
    {
        StopPlayoutWatcher();
        try
        {
            var w = new PlayoutWatcher(_sctePlayoutFile);
            w.SpliceRequested += ev =>
            {
                var s = _streamer;
                if (s is null || !ReferenceEquals(s, streamer)) return;
                // Pre-roll + ridondanza: l'iniettore calcola il PTS futuro dal PTS video corrente.
                s.WriteScte(ev.EventId, ev.OutOfNetwork, ev.DurationSeconds, ev.Immediate, ev.PrerollSeconds);
                Dispatcher.BeginInvoke(() =>
                {
                    string cue = ev.OutOfNetwork ? "OUT" : "IN";
                    string when = ev.Immediate ? "now" : $"T-{ev.PrerollSeconds:F0}s";
                    if (_streamer is not null)
                        StatusText.Text = $"SCTE-35 {cue} ({when}) id={ev.EventId} dur={(int)Math.Round(ev.DurationSeconds)}s";
                    _scteLog.Add($"{DateTime.Now:HH:mm:ss}  {cue,-3}  id={ev.EventId}  {when,-6}  dur={(int)Math.Round(ev.DurationSeconds)}s");
                    while (_scteLog.Count > 1000) _scteLog.RemoveAt(0);
                });
            };
            w.Error += msg => Dispatcher.BeginInvoke(() => StatusText.Text = "SCTE: " + msg);
            w.Start();
            _playoutWatcher = w;
            ScteLogButton.IsEnabled = true; // SCTE attivo sul flusso SRT -> bottone log utilizzabile
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Loc.T("MsgScteWatchErr"), ex.Message);
        }
    }

    private void StopPlayoutWatcher()
    {
        ScteLogButton.IsEnabled = false;
        var w = _playoutWatcher;
        _playoutWatcher = null;
        if (w != null) { try { w.Dispose(); } catch { } }
    }

    // Finestra (non modale) col log live degli eventi SCTE-35 iniettati. Attiva solo con SRT+SCTE.
    private void OnScteLog(object sender, RoutedEventArgs e)
    {
        if (_scteLogWindow != null) { _scteLogWindow.Activate(); return; }

        var dark  = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1F));
        var panel = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x2A));
        var light = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xF0));

        var list = new ListBox
        {
            ItemsSource = _scteLog,
            Background = panel, Foreground = light, BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
        };

        var clearBtn = new Button { Content = "Pulisci", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        clearBtn.Click += (_, _) => _scteLog.Clear();
        var closeBtn = new Button { Content = "Chiudi", Width = 90 };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        bar.Children.Add(clearBtn);
        bar.Children.Add(closeBtn);

        var root = new DockPanel { Background = dark };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(list);

        var win = new Window
        {
            Title = "Log SCTE-35", Width = 560, Height = 420, Owner = this,
            Background = dark, Content = root,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        closeBtn.Click += (_, _) => win.Close();
        win.Closed += (_, _) => _scteLogWindow = null;
        _scteLogWindow = win;
        win.Show();
    }

    // ---- Persistenza progetto ----

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        try { SaveProject(DefaultProjectPath); StatusText.Text = Loc.T("MsgSaved") + DefaultProjectPath; }
        catch (Exception ex) { StatusText.Text = Loc.T("ErrSave") + ex.Message; }
    }

    private void OnLoadProject(object sender, RoutedEventArgs e)
    {
        try { LoadProject(DefaultProjectPath); }
        catch (Exception ex) { StatusText.Text = Loc.T("ErrLoad") + ex.Message; }
    }

    private void SaveProject(string path)
    {
        if (_compositor is null) return;
        var dto = new ProjectDto
        {
            CanvasWidth = _canvasW, CanvasHeight = _canvasH, Fps = _fps,
            RecordBitrate = _recBitrate, StreamBitrate = _streamBitrate,
            RecordingFolder = _recFolder, RecordingFormat = _recFormat, Encoder = _encoder,
            StreamProtocol = _streamProtocol,
            StreamUrl = _streamUrl, StreamKey = _streamKey,
            StreamUser = _streamUser, StreamPass = _streamPass, StreamUseAuth = _streamUseAuth,
            SrtHost = _srtHost, SrtPort = _srtPort, SrtMode = _srtMode, SrtLatencyMs = _srtLatencyMs, SrtUrl = _srtUrl,
            ScteEnabled = _scteEnabled, SctePlayoutFile = _sctePlayoutFile,
        };
        foreach (var scene in _scenes)
        {
            var sd = new SceneDto { Name = scene.Name };
            SceneItem[] items;
            lock (_compositor.SceneLock) items = scene.Items.ToArray();
            foreach (var it in items)
            {
                if (it.Descriptor is null) continue; // non persistibile
                sd.Items.Add(new ItemDto
                {
                    Source = it.Descriptor,
                    X = it.X, Y = it.Y, Width = it.Width, Height = it.Height,
                    Visible = it.Visible, Locked = it.Locked, Opacity = it.Opacity,
                    ChromaKey = it.ChromaKey, ChromaR = it.ChromaR, ChromaG = it.ChromaG, ChromaB = it.ChromaB,
                    ChromaSimilarity = it.ChromaSimilarity, ChromaSmoothness = it.ChromaSmoothness,
                });
            }
            dto.Scenes.Add(sd);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    private void LoadProject(string path)
    {
        if (!File.Exists(path)) { StatusText.Text = Loc.T("MsgNoProject") + path; return; }
        var dto = JsonSerializer.Deserialize<ProjectDto>(File.ReadAllText(path), JsonOpts);
        if (dto is null) return;

        StopRecording();
        if (_streamer is not null) { var s = _streamer; _streamer = null; _streamStart = null; StopPlayoutWatcher(); DetachSink(s); s.Dispose(); GoLiveButton.Content = Loc.T("StartStreaming"); GoLiveButton.Tag = null; SetLive(false); }
        StopNdiOutput();

        ClearAllScenes();

        _fps = dto.Fps; _recBitrate = dto.RecordBitrate; _streamBitrate = dto.StreamBitrate;
        _recFolder = dto.RecordingFolder; _recFormat = string.IsNullOrEmpty(dto.RecordingFormat) ? "mp4" : dto.RecordingFormat;
        _encoder = string.IsNullOrEmpty(dto.Encoder) ? "auto" : dto.Encoder;
        if (!string.IsNullOrEmpty(dto.StreamUrl)) _streamUrl = dto.StreamUrl;
        _streamKey = dto.StreamKey ?? "";
        _streamUser = dto.StreamUser ?? "";
        _streamPass = dto.StreamPass ?? "";
        _streamUseAuth = dto.StreamUseAuth;
        // Progetti vecchi non hanno StreamProtocol: deduci dallo schema URL (rtmp di default).
        _streamProtocol = string.IsNullOrEmpty(dto.StreamProtocol)
            ? (_streamUrl.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ? "srt" : "rtmp")
            : dto.StreamProtocol;
        _srtHost = dto.SrtHost ?? "";
        _srtPort = dto.SrtPort is > 0 and <= 65535 ? dto.SrtPort : 9000;
        _srtMode = string.IsNullOrEmpty(dto.SrtMode) ? "caller" : dto.SrtMode;
        _srtLatencyMs = dto.SrtLatencyMs is >= 0 and <= 8000 ? dto.SrtLatencyMs : 200;
        _srtUrl = dto.SrtUrl ?? "";
        _scteEnabled = dto.ScteEnabled;
        _sctePlayoutFile = dto.SctePlayoutFile ?? "";
        if (dto.CanvasWidth != _canvasW || dto.CanvasHeight != _canvasH)
        {
            _canvasW = dto.CanvasWidth; _canvasH = dto.CanvasHeight;
            RecreateCompositor();
        }

        foreach (var sd in dto.Scenes)
        {
            var scene = new Scene(sd.Name);
            _scenes.Add(scene);
            foreach (var it in sd.Items)
            {
                try
                {
                    var src = CreateSource(it.Source);
                    var item = new SceneItem(src)
                    {
                        Descriptor = it.Source,
                        X = it.X, Y = it.Y, Width = it.Width, Height = it.Height,
                        Visible = it.Visible, Locked = it.Locked, Opacity = it.Opacity,
                        ChromaKey = it.ChromaKey, ChromaR = it.ChromaR, ChromaG = it.ChromaG, ChromaB = it.ChromaB,
                        ChromaSimilarity = it.ChromaSimilarity, ChromaSmoothness = it.ChromaSmoothness,
                    };
                    lock (_compositor!.SceneLock) scene.Items.Add(item);
                }
                catch (Exception ex) { StatusText.Text = Loc.T("MsgSourceSkipped") + ex.Message; }
            }
        }
        _sceneCounter = _scenes.Count;
        if (_scenes.Count > 0) ScenesList.SelectedItem = _scenes[0];
        _programScene = _currentScene;
        UpdateAudioRouting();
        _collectionName = Path.GetFileNameWithoutExtension(path);
        UpdateSessionHeader();
        StatusText.Text = Loc.T("MsgLoaded") + path;
    }

    private void ClearAllScenes()
    {
        foreach (var scene in _scenes)
            foreach (var item in scene.Items)
                DisposeSource(item.Source);
        _scenes.Clear();
        _currentScene = null;
        _compositor?.SetScene(null);
        _compositor?.SetSelected(null);
        SourcesList.ItemsSource = null;
    }

    private void RecreateCompositor()
    {
        bool wasD3D = _useD3DImage;
        if (wasD3D) SetD3DImageMode(false); // stacca il preview condiviso dal vecchio compositor
        var old = _compositor;
        old?.Stop();
        _compositor = new Compositor(_gd!, _canvasW, _canvasH);
        _compositor.RenderFps = Math.Max(60, _fps);
        SetupPreviewBitmap();
        _compositor.SetScene(_currentScene);
        _compositor.SetEditScene(_currentScene);
        _compositor.SetStudio(_studio);
        _compositor.SetSelected(SourcesList.SelectedItem as SceneItem);
        ApplyTransitionSettings();
        _compositor.Start();
        old?.Dispose();
        if (wasD3D) SetD3DImageMode(true); // ricrea per la nuova risoluzione preview
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_compositor is null) return;
        var dlg = new SettingsWindow(_canvasW, _canvasH, _fps, _recBitrate, _streamBitrate,
            _recFolder, _recFormat, _encoder, _streamUrl, _streamKey,
            _streamUser, _streamPass, _streamUseAuth,
            _streamProtocol, _srtHost, _srtPort, _srtMode, _srtLatencyMs,
            _scteEnabled, _sctePlayoutFile, _srtUrl) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        bool canvasChanged = dlg.CanvasW != _canvasW || dlg.CanvasH != _canvasH;

        _canvasW = dlg.CanvasW; _canvasH = dlg.CanvasH; _fps = dlg.Fps;
        _recBitrate = dlg.RecBitrate; _streamBitrate = dlg.StreamBitrate;
        _recFolder = dlg.RecFolder; _recFormat = dlg.RecFormat; _encoder = dlg.Encoder;
        _streamProtocol = dlg.StreamProtocol;
        _streamUrl = dlg.ServerUrl; _streamKey = dlg.StreamKey;
        _streamUser = dlg.StreamUser; _streamPass = dlg.StreamPass; _streamUseAuth = dlg.StreamUseAuth;
        _srtHost = dlg.SrtHost; _srtPort = dlg.SrtPort; _srtMode = dlg.SrtMode; _srtLatencyMs = dlg.SrtLatencyMs;
        _srtUrl = dlg.SrtUrl;
        _scteEnabled = dlg.ScteEnabled; _sctePlayoutFile = dlg.SctePlayoutFile;

        if (canvasChanged)
        {
            StopRecording();
            if (_streamer is not null) { var s = _streamer; _streamer = null; _streamStart = null; StopPlayoutWatcher(); DetachSink(s); s.Dispose(); GoLiveButton.Content = Loc.T("StartStreaming"); GoLiveButton.Tag = null; SetLive(false); }
            StopNdiOutput(); // l'uscita NDI è legata alle dimensioni del canvas
            RecreateCompositor();
        }
        StatusText.Text = $"Impostazioni: {_canvasW}x{_canvasH} @ {_fps}fps · {_recFormat} · {_encoder}";
    }

    private void RunProjectTest()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "streamingdoc_project.log");
        try
        {
            AddFromDescriptor(new SourceDescriptor { Kind = SourceKind.Text, Name = "Hello", FontSize = 64 });
            int beforeScenes = _scenes.Count;
            int beforeItems = _scenes.Count > 0 ? _scenes[0].Items.Count : 0;

            var savePath = Path.Combine(Path.GetTempPath(), "sd_project_test.json");
            SaveProject(savePath);
            LoadProject(savePath);

            int afterScenes = _scenes.Count;
            int afterItems = _scenes.Count > 0 ? _scenes[0].Items.Count : 0;
            bool srvOk = _scenes.Count > 0 && _scenes[0].Items.All(i => i.Source.GetSrv() is not null);
            File.WriteAllText(logPath,
                $"beforeScenes={beforeScenes} beforeItems={beforeItems} afterScenes={afterScenes} afterItems={afterItems} srvOk={srvOk}");
        }
        catch (Exception ex) { File.WriteAllText(logPath, "fail: " + ex); }
        Application.Current.Shutdown();
    }

    private static bool IsSelfTestRun()
        => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--selftest") || a == "--dumptheme");

    private void OnClosed(object? sender, EventArgs e)
    {
        // Watchdog: se persino il flush della registrazione si blocca, garantisce l'uscita entro 2.5s.
        new Thread(() => { Thread.Sleep(2500); KillSelf(); }) { IsBackground = true, Name = "ExitWatchdog" }.Start();

        if (!IsSelfTestRun()) // i selftest non devono sovrascrivere il progetto reale
        {
            TrySaveLayout();
            try { SaveProject(DefaultProjectPath); } catch { /* best effort */ }
        }

        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;

        // Flush della SOLA registrazione su file: il trailer rende il .mp4/.mkv leggibile.
        // Tutto il resto (streaming, NDI in/out, D3D, mixer) NON va disposto gentilmente:
        // i thread NATIVI di NDI altrimenti restano vivi e il processo non muore (zombie).
        try { StopRecording(); } catch { }

        // Terminazione netta: il SO libera all'istante D3D, FFmpeg e i thread nativi NDI.
        KillSelf();
    }

    // Termina subito il processo (e tutti i suoi thread, anche quelli nativi delle librerie).
    private static void KillSelf()
    {
        try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
    }
}
