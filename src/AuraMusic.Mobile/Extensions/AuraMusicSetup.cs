namespace AuraMusic.Mobile.Extensions;

/// <summary>What AuraMusic takes from its builder, one domain per member: MauiProgram only puts them together.</summary>
public static class AuraMusicSetup
{
    extension(MauiAppBuilder builder)
    {
        /// <summary>Open Sans, regular and semibold.</summary>
        public MauiAppBuilder UseAuraMusicFonts() => builder.ConfigureFonts(fonts =>
        {
            fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
        });

        /// <summary>The debug output, in Debug only.</summary>
        public MauiAppBuilder UseAuraMusicLogging()
        {
#if DEBUG
            builder.Logging.AddDebug();
#endif
            return builder;
        }
    }
}
