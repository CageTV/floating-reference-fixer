using System.Windows;

namespace FloatingObjectFixer.UI;

// Custom warning dialog, added 2026-09-13 - a plain WPF MessageBox cannot
// render a large bold header, and the user specifically asked for a LARGE
// bold "WARNING" at the top of the risky-setting confirmation, not just bold
// body text. Returns true only if the user clicks "Continue Anyway" -
// closing the window any other way (Cancel, Alt+F4, Esc via IsCancel) counts
// as declining, same as MessageBoxResult.No/None did for the callers this
// replaces.
public partial class WarningDialog : Window
{
    public bool Confirmed { get; private set; }

    public WarningDialog(string message, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        MessageText.Text = message;
    }

    void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    public static bool Show(Window owner, string message)
    {
        var dlg = new WarningDialog(message, owner);
        dlg.ShowDialog();
        return dlg.Confirmed;
    }
}
