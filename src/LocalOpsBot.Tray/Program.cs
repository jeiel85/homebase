using System.Windows;

namespace LocalOpsBot.Tray;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Application app = new();
        app.Run();
    }
}
