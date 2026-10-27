namespace AuraMusic.Mobile
{
    public partial class MainPage : ContentPage
    {
        // Only on launch, not when the page is rebuilt after a language switch.
        static bool splashShown, tutorialOffered, updateOffered, updating;

        // Below this the logo is a smudge: better none (a 360 x 640 screen while listening).
        const double MinArtworkHeight = 96;

        public MainPage()
        {
            InitializeComponent();
            // Inside the pull-to-update ScrollView the layout would collapse: give it the visible height.
            HomeScroll.SizeChanged += (_, _) => FitHomeLayout();
            ScreenFit.FollowWindow(this, FitHomeLayout);
            // The slot's height does not depend on the artwork (a star row of a layout sized to the page), so
            // hiding it cannot make room that would bring it back.
            ArtworkSlot.SizeChanged += (_, _) => Artwork.IsVisible = ArtworkSlot.Height >= MinArtworkHeight;
            LanguageButton.Text = $"🌐 {AppLanguages.Next(LanguageSettings.Current).ToUpperInvariant()}";
            VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
            // Nothing to pull for where the store updates the app. IsRefreshEnabled, never IsEnabled: MAUI passes
            // IsEnabled down to every child, and the RefreshView holds the whole home screen, so a disabled one
            // left every button of the Play Store build dead to taps (seen on Android 17).
            UpdateRefresh.IsRefreshEnabled = AppUpdater.SelfUpdates;
#if DEBUG
            // A test build shows the version being worked on, not a published one, and never updates itself.
            VersionLabel.Text += " · dev";
#endif
            // Maximum first: a slider refuses a minimum above its current maximum of 1.
            SyncSlider.Maximum = PlayoutTuning.MaxLatencyMs;
            SyncSlider.Minimum = PlayoutTuning.MinLatencyMs;
            SyncSlider.Value = PlayoutTuning.TargetLatencyMs;
            ShowSync(PlayoutTuning.TargetLatencyMs);
            Render(AuraHub.Current);
        }

        /// <summary>
        /// The layout takes the visible height, its artwork shrinking to fit. Not on a short window (the outer screen
        /// of a foldable, 480 x 410): there what it holds is taller than the window, and a layout of that height cut
        /// off its own bottom, the Stop button of the listening panel out of reach. The artwork gives way and the
        /// layout takes the height of what it holds, which scrolls.
        /// </summary>
        void FitHomeLayout()
        {
            bool isShort = ScreenFit.IsShort();
            ArtworkSlot.IsVisible = !isShort;
            HomeLayout.HeightRequest = isShort ? -1 : HomeScroll.Height;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            Rain.IsPaused = false;
            AuraHub.StateChanged += OnStateChanged;
            SpectrumHub.Updated += OnSpectrum;
            Render(AuraHub.Current);

            try
            {
                if (!splashShown)
                {
                    splashShown = true;
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await SplashOverlay.FadeToAsync(0, 400);
                }
            }
            finally
            {
                // The splash covers the whole page, even faded to nothing: it must leave the layout whatever
                // became of the fade, and on every later appearance (back from the tutorial, the diagnostics).
                SplashOverlay.IsVisible = false;
            }

            // Once per launch: this page appears again when the tutorial closes, and must not reopen it then
            // even if it was dismissed in a way that did not mark it seen.
            if (!TutorialPage.HasBeenSeen && !tutorialOffered && Navigation.ModalStack.Count == 0)
            {
                tutorialOffered = true;
                var tutorial = new TutorialPage();
                await Navigation.PushModalAsync(tutorial);
                await tutorial.Closed; // a first launch must be offered updates too, once the tutorial is done
            }

            if (!updateOffered)
            {
                updateOffered = true;
                await OfferUpdateAsync(onDemand: false);
            }
        }

        protected override void OnDisappearing()
        {
            Rain.IsPaused = true; // covered by the tutorial or the diagnostics screen, which have their own rain
            AuraHub.StateChanged -= OnStateChanged;
            SpectrumHub.Updated -= OnSpectrum;
            base.OnDisappearing();
        }

        void OnStateChanged(AuraState state) => MainThread.BeginInvokeOnMainThread(() => Render(state));

        void OnSpectrum(float[] levels) => Spectrum.Post(levels);

        void Render(AuraState state)
        {
            (StatusLabel.Text, HintLabel.Text) = state switch
            {
                Idle => (AppStrings.StatusIdle, AppStrings.HintIdle),
                Advertising => (AppStrings.StatusAdvertising, AppStrings.HintAdvertising),
                Streaming(var names) => (names.Length == 1 ? AppStrings.StatusStreamingOne : Format(AppStrings.StatusStreamingMany, names.Length),
                    Format(AppStrings.HintStreamingWith, string.Join(", ", names))),
                Searching(var problem) => (AppStrings.StatusSearching, problem ?? AppStrings.HintSearching),
                Listening(var master, var links) => (Format(AppStrings.StatusListening, master), Format(AppStrings.HintListeningVia, DiagnosticsText.Describe(links))),
                Failed(var reason) => (AppStrings.StatusFailed, reason),
            };

            bool active = state is Advertising or Streaming or Searching or Listening;
            ModeButtons.IsVisible = !active;
            ActivePanel.IsVisible = active;
            SyncPanel.IsVisible = state is Searching or Listening;
            if (!active)
                Spectrum.Clear();
        }

        void OnSyncChanged(object? sender, ValueChangedEventArgs e)
        {
            // Whole packets only: the jitter buffer counts in 20 ms steps.
            int latency = (int)Math.Round(e.NewValue / PlayoutTuning.FrameMs) * PlayoutTuning.FrameMs;
            if (latency == PlayoutTuning.TargetLatencyMs)
                return;
            PlayoutTuning.TargetLatencyMs = latency; // picked up by the playback thread on its next packet
            Preferences.Set(App.SyncLatencyKey, PlayoutTuning.TargetLatencyMs);
            ShowSync(PlayoutTuning.TargetLatencyMs);
        }

        void ShowSync(int latency) => SyncLabel.Text = Format(AppStrings.SyncDelay, latency);

        async void OnArtworkDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (Navigation.ModalStack.Count == 0)
                await Navigation.PushModalAsync(new DiagnosticsPage());
        }

        async void OnBroadcastClicked(object? sender, EventArgs e) => await RunAsync(AuraController.StartBroadcastAsync);

        async void OnListenClicked(object? sender, EventArgs e) => await RunAsync(AuraController.StartListeningAsync);

        void OnStopClicked(object? sender, EventArgs e) => AuraController.Stop();

        static string Format(string format, object value) => string.Format(CultureInfo.CurrentCulture, format, value);

        async void OnPullToUpdate(object? sender, EventArgs e)
        {
            try
            {
                if (!updating)
                    await OfferUpdateAsync(onDemand: true);
            }
            finally
            {
                UpdateRefresh.IsRefreshing = false;
            }
        }

        /// <param name="onDemand">Pulled by the user: say so when there is nothing new, instead of staying silent.</param>
        async Task OfferUpdateAsync(bool onDemand)
        {
            updating = true;
            try
            {
                await CheckAndInstallAsync(onDemand);
            }
            finally
            {
                updating = false;
            }
        }

        async Task CheckAndInstallAsync(bool onDemand)
        {
            var check = await AppUpdater.CheckAsync(CancellationToken.None);
            if (check is not UpdateAvailable(var release))
            {
                if (onDemand)
                {
                    var message = check is CheckFailed(var reason)
                        ? Format(AppStrings.UpdateCheckFailed, reason)
                        : Format(AppStrings.UpdateUpToDate, AppInfo.Current.VersionString);
                    await MatrixDialog.ShowAsync(this, AppStrings.UpdateCheckTitle, message, "OK");
                }
                return; // at launch, stay silent: up to date, or offline and we will look again next time
            }
            if (!await MatrixDialog.ShowAsync(this, AppStrings.UpdateTitle, Format(AppStrings.UpdateMessage, release.Version.ToString(3)),
                    AppStrings.UpdateNow, AppStrings.UpdateLater))
                return;

            var progress = new Progress<double>(done => StatusLabel.Text = Format(AppStrings.UpdateDownloading, done));
            try
            {
                var apk = await AppUpdater.DownloadAsync(release, progress, CancellationToken.None);
                if (AppUpdater.CanInstall)
                    AppUpdater.Install(apk);
                else if (await MatrixDialog.ShowAsync(this, AppStrings.UpdateTitle, AppStrings.UpdateAllowInstall, AppStrings.UpdateOpenSettings))
                    AppUpdater.InstallWhenAllowed(apk); // resumed by App.OnResume when the user comes back
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.OperationCanceledException or Java.Lang.Exception)
            {
                await MatrixDialog.ShowAsync(this, AppStrings.UpdateTitle, Format(AppStrings.UpdateFailed, ex.Message), "OK");
            }
            finally
            {
                Render(AuraHub.Current);
            }
        }

        void OnLanguageClicked(object? sender, EventArgs e)
        {
            LanguageSettings.SwitchToNext();
            // Rebuild the UI so every text is looked up again in the new language.
            Window.Page = new AppShell();
        }

        async void OnHelpClicked(object? sender, EventArgs e) => await Navigation.PushModalAsync(new TutorialPage());

        static async Task RunAsync(Func<Task> start)
        {
            try
            {
                await start();
            }
            catch (Exception ex)
            {
                AuraHub.Publish(new Failed(ex.Message));
            }
        }
    }
}
