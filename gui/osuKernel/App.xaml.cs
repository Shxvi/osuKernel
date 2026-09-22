using System;
using System.Linq;
using System.Threading;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace osuKernel
{
    public partial class App : WpfApplication
    {
        private const string AppMutexName = "Global\\osuKernel_Configurator_AppMutex";
        public const string ShowWindowEventName = "Local\\osuKernel_ShowWindowEvent";

        private Mutex? _appMutex;
        private EventWaitHandle? _showWindowEvent;
        private Thread? _eventListenerThread;
        private volatile bool _isShuttingDown;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _appMutex = new Mutex(true, AppMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running; signal it to show its window and exit
                try
                {
                    using var existingEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                    existingEvent.Set();
                }
                catch { }

                Shutdown();
                return;
            }

            // Create show window event for future invocations
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

            bool startMinimized = e.Args.Any(arg =>
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

            MaterialColorPalette.ApplyDynamicPalette();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            if (!startMinimized)
            {
                mainWindow.Show();
            }

            // Background listener for show event
            _eventListenerThread = new Thread(() =>
            {
                while (!_isShuttingDown)
                {
                    if (_showWindowEvent.WaitOne(1000))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow != null)
                            {
                                if (!MainWindow.IsVisible)
                                {
                                    MainWindow.Show();
                                }
                                MainWindow.WindowState = WindowState.Normal;
                                MainWindow.Activate();
                            }
                        });
                    }
                }
            })
            {
                IsBackground = true,
                Name = "osuKernel_ShowListener"
            };
            _eventListenerThread.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _isShuttingDown = true;
            _showWindowEvent?.Dispose();
            _appMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
