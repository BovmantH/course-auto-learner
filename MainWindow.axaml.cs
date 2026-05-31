using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CourseAutoLearner.Models;
using CourseAutoLearner.ViewModels;

namespace CourseAutoLearner;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    internal readonly AppSettings Settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        _vm = new MainViewModel(Settings);
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText))
                Dispatcher.UIThread.InvokeAsync(() => LogScrollViewer.ScrollToEnd());
        };
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
        => _vm.Cleanup();

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
        => _vm.LogText = string.Empty;

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(Settings);
        win.Closed += (_, _) =>
        {
            if (win.Result == true)
                _vm.OnSettingsChanged(Settings);
        };
        win.ShowDialog(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var screen = Screens.Primary;
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;

        var left = (workingArea.Width / scale) - Width;
        var top = (workingArea.Height / scale) - Height;

        Position = new PixelPoint((int)left, (int)top);
    }
}
