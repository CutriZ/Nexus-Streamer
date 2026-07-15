using System.Windows;
using System.Windows.Controls;
using StreamingDoc.App.Localization;

namespace StreamingDoc.App;

public partial class SettingsWindow : Window
{
    private bool _langInit;

    // Valori in uscita (validi se DialogResult == true)
    public int CanvasW { get; private set; }
    public int CanvasH { get; private set; }
    public int Fps { get; private set; }
    public long RecBitrate { get; private set; }
    public long StreamBitrate { get; private set; }
    public string RecFolder { get; private set; } = "";
    public string RecFormat { get; private set; } = "mp4";
    public string Encoder { get; private set; } = "auto";
    public string StreamProtocol { get; private set; } = "rtmp";
    public string ServerUrl { get; private set; } = "";
    public string StreamKey { get; private set; } = "";
    public string StreamUser { get; private set; } = "";
    public string StreamPass { get; private set; } = "";
    public bool StreamUseAuth { get; private set; }
    public string SrtHost { get; private set; } = "";
    public int SrtPort { get; private set; } = 9000;
    public string SrtMode { get; private set; } = "caller";
    public int SrtLatencyMs { get; private set; } = 200;
    public string SrtUrl { get; private set; } = "";
    public bool ScteEnabled { get; private set; }
    public string SctePlayoutFile { get; private set; } = "";

    public SettingsWindow(int cw, int ch, int fps, long recBitrate, long streamBitrate,
        string recFolder, string recFormat, string encoder, string serverUrl, string streamKey,
        string streamUser, string streamPass, bool streamUseAuth,
        string streamProtocol = "rtmp", string srtHost = "", int srtPort = 9000,
        string srtMode = "caller", int srtLatencyMs = 200,
        bool scteEnabled = false, string sctePlayoutFile = "", string srtUrl = "")
    {
        InitializeComponent();

        LangCombo.ItemsSource = Loc.Languages;
        LangCombo.SelectedValue = Loc.CurrentCode;
        _langInit = true;

        Nav.SelectedIndex = 0;

        CanvasCombo.Text = $"{cw}x{ch}";
        FpsCombo.Text = fps.ToString();
        FolderBox.Text = recFolder;
        RecBitrateBox.Text = (recBitrate / 1000).ToString();
        StreamBitrateBox.Text = (streamBitrate / 1000).ToString();
        SelectByContent(FormatCombo, recFormat);
        SelectByTag(EncoderCombo, encoder);
        ServerBox.Text = serverUrl;
        KeyPwd.Password = streamKey;
        UserBox.Text = streamUser;
        PassPwd.Password = streamPass;
        UseAuth.IsChecked = streamUseAuth;
        KeyShowBtn.Content = Loc.T("Show");
        PassShowBtn.Content = Loc.T("Show");
        UpdateAuthEnabled();

        SrtHostBox.Text = srtHost;
        SrtPortBox.Text = srtPort.ToString();
        SelectByTag(SrtModeCombo, srtMode);
        SrtLatencyBox.Text = srtLatencyMs.ToString();
        SrtUrlBox.Text = srtUrl;
        ScteEnable.IsChecked = scteEnabled;
        ScteFileBox.Text = sctePlayoutFile;
        SelectByTag(ProtoCombo, streamProtocol);  // scatena OnProtoChanged -> visibilità gruppi
        UpdateProtoGroups();

        SampleRateCombo.SelectedIndex = 0;
    }

    private void OnProtoChanged(object sender, SelectionChangedEventArgs e) => UpdateProtoGroups();

    private void UpdateProtoGroups()
    {
        if (RtmpGroup == null) return; // durante InitializeComponent
        string p = (ProtoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        RtmpGroup.Visibility = p == "rtmp" ? Visibility.Visible : Visibility.Collapsed;
        SrtGroup.Visibility = p == "srt" ? Visibility.Visible : Visibility.Collapsed;
    }

    // Mostra/nasconde la chiave (PasswordBox <-> TextBox) mantenendo il valore.
    private void OnToggleKey(object sender, RoutedEventArgs e) => Reveal(KeyPwd, KeyTxt, KeyShowBtn);
    private void OnTogglePass(object sender, RoutedEventArgs e) => Reveal(PassPwd, PassTxt, PassShowBtn);

    private void Reveal(PasswordBox pwd, TextBox txt, Button btn)
    {
        if (txt.Visibility == Visibility.Visible) // nascondi
        {
            pwd.Password = txt.Text;
            txt.Visibility = Visibility.Collapsed;
            pwd.Visibility = Visibility.Visible;
            btn.Content = Loc.T("Show");
        }
        else // mostra
        {
            txt.Text = pwd.Password;
            pwd.Visibility = Visibility.Collapsed;
            txt.Visibility = Visibility.Visible;
            btn.Content = Loc.T("Hide");
        }
    }

    private void OnToggleAuth(object sender, RoutedEventArgs e) => UpdateAuthEnabled();

    private void UpdateAuthEnabled()
    {
        bool on = UseAuth.IsChecked == true;
        UserBox.IsEnabled = on;
        PassPwd.IsEnabled = on;
        PassTxt.IsEnabled = on;
        PassShowBtn.IsEnabled = on;
    }

    private static string KeyOf(PasswordBox pwd, TextBox txt)
        => txt.Visibility == Visibility.Visible ? txt.Text : pwd.Password;

    private static void SelectByContent(ComboBox c, string val)
    {
        foreach (ComboBoxItem i in c.Items)
            if (string.Equals(i.Content?.ToString(), val, StringComparison.OrdinalIgnoreCase)) { c.SelectedItem = i; return; }
        c.SelectedIndex = 0;
    }

    private static void SelectByTag(ComboBox c, string tag)
    {
        foreach (ComboBoxItem i in c.Items)
            if (string.Equals(i.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) { c.SelectedItem = i; return; }
        c.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelGeneral == null) return;
        PanelGeneral.Visibility = Nav.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelVideo.Visibility = Nav.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelOutput.Visibility = Nav.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelStream.Visibility = Nav.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelAudio.Visibility = Nav.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_langInit) return;
        if (LangCombo.SelectedValue is string code)
        {
            Loc.Set(code);                  // aggiorna tutta l'app live
            AppSettings.SaveLanguage(code); // persiste la scelta
        }
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Cartella registrazioni" };
        if (!string.IsNullOrEmpty(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
    }

    private void OnBrowseScte(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "File trigger playout (SCTE-35)",
            Filter = "XML|*.xml|Tutti i file|*.*",
        };
        if (!string.IsNullOrEmpty(ScteFileBox.Text)) { try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(ScteFileBox.Text); } catch { } }
        if (dlg.ShowDialog() == true) ScteFileBox.Text = dlg.FileName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var parts = (CanvasCombo.Text ?? "").Split('x', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int cw) && int.TryParse(parts[1].Trim(), out int ch)
            && cw >= 320 && ch >= 240)
        {
            // arrotonda a pari (richiesto da yuv420)
            CanvasW = cw - (cw % 2);
            CanvasH = ch - (ch % 2);
        }
        else { MessageBox.Show("Risoluzione canvas non valida (es. 1920x1080)."); return; }

        Fps = int.TryParse(FpsCombo.Text?.Trim(), out int fps) && fps is >= 1 and <= 240 ? fps : 30;
        RecBitrate = (long.TryParse(RecBitrateBox.Text?.Trim(), out long rk) ? rk : 10000) * 1000;
        StreamBitrate = (long.TryParse(StreamBitrateBox.Text?.Trim(), out long sk) ? sk : 6000) * 1000;
        RecFormat = (FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "mp4";
        Encoder = (EncoderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        RecFolder = FolderBox.Text?.Trim() ?? "";
        StreamProtocol = (ProtoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        ServerUrl = ServerBox.Text?.Trim() ?? "";
        StreamKey = KeyOf(KeyPwd, KeyTxt).Trim();
        StreamUseAuth = UseAuth.IsChecked == true;
        StreamUser = UserBox.Text?.Trim() ?? "";
        StreamPass = KeyOf(PassPwd, PassTxt);

        SrtHost = SrtHostBox.Text?.Trim() ?? "";
        SrtPort = int.TryParse(SrtPortBox.Text?.Trim(), out int sp) && sp is > 0 and <= 65535 ? sp : 9000;
        SrtMode = (SrtModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "caller";
        SrtLatencyMs = int.TryParse(SrtLatencyBox.Text?.Trim(), out int sl) && sl is >= 0 and <= 8000 ? sl : 200;
        SrtUrl = SrtUrlBox.Text?.Trim() ?? "";
        ScteEnabled = ScteEnable.IsChecked == true;
        SctePlayoutFile = ScteFileBox.Text?.Trim() ?? "";

        DialogResult = true;
    }
}
