namespace AuraMusic.Mobile
{
    public partial class AppShell : Shell
    {
        // Where Back would leave the app, a first press only asks: see OnBackButtonPressed.
        readonly DoubleBackToExit exitGuard = new(TimeProvider.System);

        public AppShell()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Android's Back button arrives here when no dialog or modal page (tutorial, diagnostics) is open.
        /// On the home page, where Back would leave the app, the first press shows a toast and only a second
        /// press within two seconds leaves. A broadcast or a listening goes on in its service either way.
        /// </summary>
        protected override bool OnBackButtonPressed()
        {
            if (base.OnBackButtonPressed())
                return true;

            // Confirmed: the default happens, Android leaves the app.
            if (exitGuard.ConfirmsExit())
                return false;

            _ = CommunityToolkit.Maui.Alerts.Toast
                .Make(AppStrings.BackAgainToQuit, CommunityToolkit.Maui.Core.ToastDuration.Short)
                .Show();
            return true;
        }
    }
}
