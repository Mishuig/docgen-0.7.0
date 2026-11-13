using System.Diagnostics;
using System.IO;
using System.Windows;
using DocumentGenerator.Infrastructure;

namespace DocumentGenerator;

public partial class App : Application
{
    static App()
    {
        // Extremely early: runs when the App type is first touched.
        try { AppLog.Bootstrap("[static App()] type initialized"); } catch { }
    }

    private static readonly Stopwatch StartupClock = Stopwatch.StartNew();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Exception handlers FIRST — so any later failure is logged.
        HookExceptionHandlers();

        try
        {
            // Minimal path init (no verbose diagnostics on warm starts).
            AppPaths.Initialize();
            AppLog.Bootstrap($"OnStartup begin (+{StartupClock.ElapsedMilliseconds} ms)");

            base.OnStartup(e); // creates MainWindow via StartupUri

            // DB init AFTER the window is constructed so the shell appears sooner.
            // EnsureCreated is cheap when the file already exists; skip the round-trip when possible.
            EnsureDatabase();

            AppLog.Bootstrap($"OnStartup complete (+{StartupClock.ElapsedMilliseconds} ms)");
            AppLog.Write($"Application started in {StartupClock.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            AppLog.Bootstrap("OnStartup FAILED", ex);
            AppLog.Write("Startup failed", ex);

            try
            {
                MessageBox.Show(
                    $"Не вдалося запустити Docugen.\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Лог: {AppLog.StartupLogPath}",
                    "Помилка запуску Docugen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }

            Shutdown(1);
        }
    }

    private static void EnsureDatabase()
    {
        // Fast path: DB file already present → no schema work on the UI-critical path.
        // First run (or deleted DB) still needs EnsureCreated.
        if (File.Exists(AppPaths.Database))
        {
            AppLog.Bootstrap($"SQLite exists, skip EnsureCreated (+{StartupClock.ElapsedMilliseconds} ms)");
            return;
        }

        AppLog.Bootstrap($"SQLite EnsureCreated... (+{StartupClock.ElapsedMilliseconds} ms)");
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        AppLog.Bootstrap($"SQLite created (+{StartupClock.ElapsedMilliseconds} ms)");
    }

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("Unhandled UI exception", args.Exception);
            try
            {
                MessageBox.Show(
                    $"Сталася неочікувана помилка.\n\n{args.Exception.Message}\n\n" +
                    $"Деталі: {AppLog.StartupLogPath}",
                    "Docugen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* ignore */ }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write("Fatal AppDomain exception", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
