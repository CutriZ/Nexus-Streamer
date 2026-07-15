using System.Windows;

namespace StreamingDoc.App;

public partial class InputDialog : Window
{
    public string Value => Input.Text;

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.SelectAll(); Input.Focus(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var d = new InputDialog(title, prompt, initial) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
