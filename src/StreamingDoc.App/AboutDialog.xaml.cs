using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace StreamingDoc.App;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionText.Text = $"{Localization.Loc.T("Version")} {v.Major}.{v.Minor}.{v.Build}";
    }

    public static void Show(Window owner)
        => new AboutDialog { Owner = owner }.ShowDialog();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
