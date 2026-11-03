using System.Windows;

namespace PdfViewer;

public partial class App : Application
{
    private bool _isSecondInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppSettings.Load();

        // Single-instance mode: if another instance is already running, hand the files over
        // to it and quit right away (before any log or window is created).
        if (AppSettings.SingleInstance && !SingleInstance.TryBecomeFirst())
        {
            _isSecondInstance = true;
            SingleInstance.SendToFirst(e.Args);
            Shutdown();
            return;
        }

        AppLog.Start();

        // Write every kind of unhandled error to the log before the program goes down.
        // (Errors inside the native PDFium library cannot be caught; for those, the last
        // line of the log shows what the program was doing.)
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("Unhandled UI exception", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error($"Unhandled exception (terminating: {args.IsTerminating})",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));

        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("Unobserved task exception", args.Exception);

        base.OnStartup(e);

        // Create the main window here (instead of via StartupUri) so that files passed on the
        // command line can be opened: Explorer's "Open with" and file associations hand the
        // path of the PDF to the program as an argument.
        AppLog.Write($"Command line arguments: {(e.Args.Length == 0 ? "(none)" : string.Join(" | ", e.Args))}");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // First the files of the last session (if enabled), then the file(s) that were passed
        // on the command line, so the clicked file ends up as the selected tab. A file that is
        // already part of the session is not opened twice, its tab is just selected.
        if (AppSettings.ReopenLastFiles)
            window.OpenFiles(AppSettings.LastSession, addToRecent: false);
        if (e.Args.Length > 0)
            window.OpenFiles(e.Args);

        if (AppSettings.SingleInstance)
            SingleInstance.StartServer(paths => window.OpenFromOtherInstance(paths));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isSecondInstance) AppLog.Stop();
        base.OnExit(e);
    }
}
