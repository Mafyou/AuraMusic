namespace AuraMusic.Mobile.Controls;

/// <summary>
/// Matrix rain background made of pre-rendered, vertically seamless glyph tiles that scroll down forever.
/// Drawing the glyphs live cost ~0.2 ms each and saturated the UI thread; moving an image is nearly free.
/// </summary>
/// <remarks>
/// Moved by a timer at <see cref="FramesPerSecond"/>, not by a MAUI animation: an animation ticks at the display's
/// own rate, and the idle home screen redrew ~63 times a second (1,267 frames in 20 s) for a decoration. The rain
/// also stops while nobody can see it: page covered (<see cref="IsPaused"/>), app in the background, view unloaded.
/// </remarks>
public sealed class MatrixRainView : ContentView
{
    const double TileAspect = 1600.0 / 720; // height / width of rain_*.png

    /// <summary>The glyphs move ~3 dp a frame at this rate: still a smooth fall, a third of the redraws.</summary>
    public const int FramesPerSecond = 20;

    // Two layers at different speeds give depth: the dim, small glyphs fall slower.
    readonly Layer far = new("rain_far.png", TimeSpan.FromSeconds(28));
    readonly Layer near = new("rain_near.png", TimeSpan.FromSeconds(16));

    // Time the rain has fallen: it stands still while paused and picks up where it stopped.
    readonly Stopwatch fallen = new();
    IDispatcherTimer? timer;
    Window? window;
    bool windowStopped;
    double tileHeight;

    public MatrixRainView()
    {
        // Decoration only: it lies under the whole page and must never take a tap.
        InputTransparent = true;
        IsClippedToBounds = true;
        Content = new Grid { Children = { far.Tiles, near.Tiles } };
        SizeChanged += (_, _) => Resize();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Set by the page while it is covered by another (the tutorial, the diagnostics screen).</summary>
    public bool IsPaused
    {
        get => field;
        set
        {
            field = value;
            UpdateRunning();
        }
    }

    void Resize()
    {
        if (Width <= 0)
            return;
        tileHeight = Width * TileAspect;
        far.Resize(Width, tileHeight);
        near.Resize(Width, tileHeight);
        Draw();
        UpdateRunning();
    }

    void Attach()
    {
        window = Window;
        if (window is not null)
        {
            window.Stopped += OnWindowStopped;
            window.Resumed += OnWindowResumed;
        }
        windowStopped = false;
        UpdateRunning();
    }

    void Detach()
    {
        if (window is not null)
        {
            window.Stopped -= OnWindowStopped;
            window.Resumed -= OnWindowResumed;
            window = null;
        }
        UpdateRunning();
    }

    void OnWindowStopped(object? sender, EventArgs e)
    {
        windowStopped = true;
        UpdateRunning();
    }

    void OnWindowResumed(object? sender, EventArgs e)
    {
        windowStopped = false;
        UpdateRunning();
    }

    void UpdateRunning()
    {
        bool run = IsLoaded && window is not null && !windowStopped && !IsPaused && tileHeight > 0;
        if (run == fallen.IsRunning)
            return;

        if (run)
        {
            if (timer is null)
            {
                timer = Dispatcher.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
                timer.IsRepeating = true;
                timer.Tick += (_, _) => Draw();
            }
            fallen.Start();
            timer.Start();
        }
        else
        {
            fallen.Stop();
            timer?.Stop();
        }
    }

    void Draw()
    {
        if (tileHeight <= 0)
            return;
        far.MoveTo(fallen.Elapsed, tileHeight);
        near.MoveTo(fallen.Elapsed, tileHeight);
    }

    sealed class Layer(string source, TimeSpan perTile)
    {
        // Two copies stacked: while one scrolls out at the bottom, the other fills the top.
        public VerticalStackLayout Tiles { get; } = new()
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Start,
            Children = { new Image { Source = source, Aspect = Aspect.Fill }, new Image { Source = source, Aspect = Aspect.Fill } },
        };

        public void Resize(double width, double tileHeight)
        {
            foreach (var image in Tiles.Children.Cast<Image>())
                (image.WidthRequest, image.HeightRequest) = (width, tileHeight);
        }

        /// <summary>From one tile above the view down to 0, once per <c>perTile</c>, over and over.</summary>
        public void MoveTo(TimeSpan fallen, double tileHeight)
        {
            double progress = fallen.TotalSeconds % perTile.TotalSeconds / perTile.TotalSeconds;
            Tiles.TranslationY = -tileHeight + progress * tileHeight;
        }
    }
}
