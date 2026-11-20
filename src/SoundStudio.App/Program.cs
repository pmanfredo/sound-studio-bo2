using Velopack;

namespace SoundStudio.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles install/update/uninstall hooks and exits early when the installer calls us. Must run first.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
