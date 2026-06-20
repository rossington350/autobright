using System;
using Microsoft.UI.Xaml;

namespace AutoBright;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CommandLineOptions options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
        _window = new MainWindow(options);
        _window.Activate();
    }
}
