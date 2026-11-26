namespace AuraMusic.Mobile
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .UseAuraMusicFonts()
                .UseAuraMusicLogging();

            return builder.Build();
        }
    }
}
