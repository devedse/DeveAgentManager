using MudBlazor;

namespace DeveAgentManager.Components.Layout;

/// <summary>The Portal's look: a dark, slightly blue-tinted theme with a violet accent.</summary>
public static class AppTheme
{
    public const string MonoFont = "'JetBrains Mono', 'Cascadia Code', 'Fira Code', Menlo, Consolas, 'DejaVu Sans Mono', monospace";

    private static readonly string[] SansFont = ["Inter", "-apple-system", "BlinkMacSystemFont", "Segoe UI", "Roboto", "Helvetica", "Arial", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#8b7dff",
            PrimaryContrastText = "#0d1017",
            Secondary = "#2dd4bf",
            Tertiary = "#f472b6",
            Info = "#60a5fa",
            Success = "#34d399",
            Warning = "#fbbf24",
            Error = "#f87171",
            Black = "#07090d",
            Background = "#0d1017",
            BackgroundGray = "#11151e",
            Surface = "#141923",
            DrawerBackground = "#0f131b",
            DrawerText = "#c9d1e1",
            DrawerIcon = "#8e98ad",
            AppbarBackground = "rgba(13,16,23,0.85)",
            AppbarText = "#e6ebf5",
            TextPrimary = "#e6ebf5",
            TextSecondary = "#8e98ad",
            TextDisabled = "#4b5467",
            ActionDefault = "#aab3c5",
            LinesDefault = "#232a38",
            LinesInputs = "#2e3647",
            TableLines = "#1e2430",
            TableHover = "#1a2030",
            Divider = "#1e2430",
            DividerLight = "#1a1f2a",
            Skeleton = "#1c2230",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = SansFont, LetterSpacing = "normal" },
            H4 = new H4Typography { FontFamily = SansFont, FontWeight = "700", LetterSpacing = "-0.02em" },
            H5 = new H5Typography { FontFamily = SansFont, FontWeight = "700", LetterSpacing = "-0.01em" },
            H6 = new H6Typography { FontFamily = SansFont, FontWeight = "600" },
            Button = new ButtonTypography { FontFamily = SansFont, FontWeight = "600", TextTransform = "none", LetterSpacing = "normal" },
            Overline = new OverlineTypography { FontFamily = SansFont, FontWeight = "600", LetterSpacing = "0.08em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "248px",
        },
    };
}
