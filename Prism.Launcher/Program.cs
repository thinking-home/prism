using System.IO.Pipes;
using System.Text;
using System.Windows.Forms;

namespace Prism.Launcher;

internal static class Program
{
    // Single-instance: живёт один процесс в трее, повторные запуски из пункта
    // «Отправить» передают ему путь к файлу через именованный канал и выходят —
    // так используется уже установленное MQTT-подключение с готовым списком плееров.
    private const string MutexName = @"Local\Prism.Launcher.SingleInstance";
    private const string PipeName = "Prism.Launcher.Pipe";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            if (args.Length > 0) ForwardToRunningInstance(args[0]);
            return;
        }

        ApplicationConfiguration.Initialize();
        var context = new LauncherContext();
        StartPipeServer(context);

        if (args.Length > 0) context.HandleFile(args[0]); // запущены сразу с файлом

        Application.Run(context);
        GC.KeepAlive(mutex);
    }

    private static void ForwardToRunningInstance(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(path);
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch { /* живой экземпляр не ответил — тихо выходим */ }
    }

    private static void StartPipeServer(LauncherContext context)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var path = reader.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(path))
                        context.HandleFile(path);
                }
                catch
                {
                    Thread.Sleep(500); // ошибка канала — не крутим цикл на полной скорости
                }
            }
        })
        {
            IsBackground = true,
            Name = "PrismLauncherPipe",
        };
        thread.Start();
    }
}
