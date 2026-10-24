using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Cais
{
    public partial class App : Application
    {
        Mutex mutex;
        DockController ctl;

        protected override void OnStartup(StartupEventArgs e)
        {
            int snap = Array.IndexOf(e.Args, "--snapshot");
            if (snap >= 0)
            {
                base.OnStartup(e);
                string dir = snap + 1 < e.Args.Length && !e.Args[snap + 1].StartsWith("--") ? e.Args[snap + 1] : "capturas";
                try { new DockController().Snapshot(dir, e.Args.Contains("--demo")); }
                catch (Exception ex) { Log(ex); }
                Shutdown();
                return;
            }

            bool exit = e.Args.Contains("--sair");
            mutex = new Mutex(true, "Cais.SingleInstance", out bool first);
            if (!first)
            {
                // Já está rodando: abre as configurações (ou fecha) na instância existente
                try
                {
                    Native.AllowSetForegroundWindow(-1);
                    EventWaitHandle.OpenExisting(exit ? "Cais.Exit" : "Cais.ShowSettings").Set();
                }
                catch { }
                Shutdown();
                return;
            }
            if (exit) { Shutdown(); return; }

            DispatcherUnhandledException += (s, a) => { Log(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                Log(a.ExceptionObject as Exception);
                try { Taskbar.SetVisible(true); } catch { }
            };

            base.OnStartup(e);
            ctl = new DockController();
            ctl.Start(e.Args);

            Listen("Cais.ShowSettings", () => ctl.ShowSettings());
            Listen("Cais.Exit", () => ctl.Exit());
        }

        void Listen(string name, Action action)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(action);
            }) { IsBackground = true }.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ctl?.Cleanup();
            base.OnExit(e);
        }

        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(DockConfig.Folder);
                File.AppendAllText(Path.Combine(DockConfig.Folder, "erros.log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
