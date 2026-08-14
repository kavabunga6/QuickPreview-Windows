using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace QuickPreview;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            StartupToggle.IsChecked = StartupService.IsEnabled();
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"QuickPreview {version?.Major}.{version?.Minor}.{version?.Build}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        try
        {
            StartupService.SetEnabled(StartupToggle.IsChecked == true);
            StatusText.Text = StartupToggle.IsChecked == true
                ? "Автозапуск включён."
                : "Автозапуск выключен.";
        }
        catch (Exception exception)
        {
            _loading = true;
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
            _loading = false;
            StatusText.Text = $"Не удалось изменить автозапуск: {exception.Message}";
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Close();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
