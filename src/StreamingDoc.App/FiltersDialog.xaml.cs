using System.Windows;
using System.Windows.Media;
using StreamingDoc.App.Model;

namespace StreamingDoc.App;

public partial class FiltersDialog : Window
{
    private readonly SceneItem _item;

    public FiltersDialog(SceneItem item)
    {
        InitializeComponent();
        _item = item;
        HeaderText.Text = "Filtri — " + item.Name;

        OpacitySlider.Value = item.Opacity;
        ChromaCheck.IsChecked = item.ChromaKey;
        SimSlider.Value = item.ChromaSimilarity;
        SmoothSlider.Value = item.ChromaSmoothness;
        UpdateSwatch();
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        OpacityVal.Text = $"{OpacitySlider.Value * 100:0}%";
        SimVal.Text = $"{SimSlider.Value:0.00}";
        SmoothVal.Text = $"{SmoothSlider.Value:0.00}";
    }

    private void UpdateSwatch()
    {
        KeySwatch.Background = new SolidColorBrush(Color.FromRgb(
            (byte)(_item.ChromaR * 255), (byte)(_item.ChromaG * 255), (byte)(_item.ChromaB * 255)));
    }

    private void OnOpacity(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _item.Opacity = OpacitySlider.Value;
        UpdateLabels();
    }

    private void OnChroma(object sender, RoutedEventArgs e) => _item.ChromaKey = ChromaCheck.IsChecked == true;
    private void OnSim(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) { _item.ChromaSimilarity = SimSlider.Value; UpdateLabels(); } }
    private void OnSmooth(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) { _item.ChromaSmoothness = SmoothSlider.Value; UpdateLabels(); } }

    private void OnKeyGreen(object sender, RoutedEventArgs e) { _item.ChromaR = 0; _item.ChromaG = 1; _item.ChromaB = 0; UpdateSwatch(); }
    private void OnKeyBlue(object sender, RoutedEventArgs e) { _item.ChromaR = 0; _item.ChromaG = 0; _item.ChromaB = 1; UpdateSwatch(); }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
