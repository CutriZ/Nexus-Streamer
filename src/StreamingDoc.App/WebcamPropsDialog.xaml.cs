using System.Collections.Generic;
using System.Windows;

namespace StreamingDoc.App;

public partial class WebcamPropsDialog : Window
{
    public bool UseCustom => CustomAudio.IsChecked == true;
    public string? SelectedDevice => AudioCombo.SelectedItem as string;

    public WebcamPropsDialog(IEnumerable<string> audioDevices, bool useCustom, string? selected)
    {
        InitializeComponent();

        foreach (var d in audioDevices) AudioCombo.Items.Add(d);
        if (AudioCombo.Items.Count == 0)
            AudioCombo.Items.Add(Localization.Loc.T("NoAudioDevice"));

        CustomAudio.IsChecked = useCustom;
        AudioCombo.IsEnabled = useCustom;
        if (!string.IsNullOrEmpty(selected) && AudioCombo.Items.Contains(selected))
            AudioCombo.SelectedItem = selected;
        else if (AudioCombo.Items.Count > 0)
            AudioCombo.SelectedIndex = 0;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
        => AudioCombo.IsEnabled = CustomAudio.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
