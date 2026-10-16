namespace AuraMusic.Mobile
{
    public sealed record TutorialStep(string Emoji, string Title, string Text);

    public partial class TutorialPage : ContentPage
    {
        const string SeenKey = "tutorialSeen";

        // Per page, not static: the texts are looked up in the language of the moment, which can be switched.
        readonly ImmutableArray<TutorialStep> all =
        [
            new("⚡", AppStrings.TutorialWelcomeTitle, AppStrings.TutorialWelcomeText),
            new("🔗", AppStrings.TutorialPairTitle, AppStrings.TutorialPairText),
            new("📡", AppStrings.TutorialBroadcastTitle, AppStrings.TutorialBroadcastText),
            new("🎧", AppStrings.TutorialListenTitle, AppStrings.TutorialListenText),
            new("📶", AppStrings.TutorialAnywhereTitle, AppStrings.TutorialAnywhereText),
            new("💡", AppStrings.TutorialTipsTitle, AppUpdater.SelfUpdates ? $"{AppStrings.TutorialTipsText} {AppStrings.TutorialTipsUpdate}" : AppStrings.TutorialTipsText),
        ];

        public TutorialPage()
        {
            InitializeComponent();
            Steps.ItemsSource = all;
        }

        readonly TaskCompletionSource closed = new();

        public static bool HasBeenSeen => Preferences.Get(SeenKey, false);

        /// <summary>Completes once the tutorial has been dismissed.</summary>
        public Task Closed => closed.Task;

        void OnPositionChanged(object? sender, PositionChangedEventArgs e) =>
            NextButton.Text = e.CurrentPosition == all.Length - 1 ? AppStrings.TutorialDone : AppStrings.TutorialNext;

        async void OnNextClicked(object? sender, EventArgs e)
        {
            if (Steps.Position < all.Length - 1)
                Steps.Position++;
            else
                await CloseAsync();
        }

        async void OnDoneClicked(object? sender, EventArgs e) => await CloseAsync();

        /// <summary>
        /// Back (the key or the edge swipe) closes the tutorial like "Skip". Left to MAUI, Back popped the page
        /// without marking it seen, and the home page, appearing again, opened the first-launch tutorial anew:
        /// Back seemed to do nothing and the swipe slid the tutorial away only for it to come back.
        /// </summary>
        protected override bool OnBackButtonPressed()
        {
            _ = CloseAsync();
            return true;
        }

        bool closing;

        async Task CloseAsync()
        {
            // Skip, the last Next and Back can come together: the page is popped once.
            if (closing)
                return;
            closing = true;
            Preferences.Set(SeenKey, true);
            try
            {
                await Navigation.PopModalAsync();
            }
            finally
            {
                closed.TrySetResult();
            }
        }
    }
}
