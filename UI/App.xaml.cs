using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Styx;
using Styx.Helpers;
using Styx.Localization;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// Port of HB 6.2.3 App — global exception handlers so crashes are always logged.
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Globalization.ApplyLanguage(StyxSettings.Instance?.Language);

            base.OnStartup(e);

            // Background thread exceptions (non-Dispatcher)
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // Task exceptions that were never observed
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>
        /// HB 6.2.3 App_DispatcherUnhandledException — catches WPF/Dispatcher thread exceptions.
        /// Wired via App.xaml DispatcherUnhandledException attribute.
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            // HB 6.2.3: silently ignore stale-pointer and thread-abort noise
            if (e.Exception is InvalidObjectPointerException) return;
            if (e.Exception is ThreadAbortException) return;

            if (e.Exception != null)
                Logging.WriteException(e.Exception);
        }

        /// <summary>
        /// Logs unhandled exceptions thrown on background threads.
        /// isTerminating=true means the CLR will kill the process after this handler returns.
        /// </summary>
        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                Logging.WriteException(ex);
        }

        /// <summary>
        /// Logs Task exceptions that were faulted but never observed (await or .Exception read).
        /// Sets Observed=true to prevent the CLR from re-throwing on finalization.
        /// </summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Logging.WriteException(e.Exception);
        }
    }
}

