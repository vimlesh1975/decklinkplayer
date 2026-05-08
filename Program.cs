using System.Runtime.InteropServices;
using System.Text;

namespace ffmpegplayer;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            NativeConsole.AttachToParent();
            return await Cli.RunAsync(args);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}

internal static partial class NativeConsole
{
    private const int AttachParentProcess = -1;

    public static void AttachToParent()
    {
        _ = AttachConsole(AttachParentProcess);
        Console.OutputEncoding = Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
