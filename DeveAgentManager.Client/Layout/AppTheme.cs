using MudBlazor;

namespace DeveAgentManager.Client.Layout;

/// <summary>The Portal's look: dark and slightly blue-tinted, or light, with a violet accent (the same colors as app.css).</summary>
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
        PaletteLight = new PaletteLight
        {
            Primary = "#6a5af0",
            PrimaryContrastText = "#ffffff",
            Secondary = "#0d9488",
            Tertiary = "#db2777",
            Info = "#2563eb",
            Success = "#059669",
            Warning = "#b45309",
            Error = "#dc2626",
            Black = "#0d1017",
            Background = "#f5f6fa",
            BackgroundGray = "#eceef4",
            Surface = "#ffffff",
            DrawerBackground = "#fbfbfd",
            DrawerText = "#2a3142",
            DrawerIcon = "#5b6477",
            AppbarBackground = "rgba(255,255,255,0.85)",
            AppbarText = "#161b26",
            TextPrimary = "#161b26",
            TextSecondary = "#5b6477",
            TextDisabled = "#a3aaba",
            ActionDefault = "#4b5467",
            LinesDefault = "#e2e5ed",
            LinesInputs = "#cfd4df",
            TableLines = "#e8ebf1",
            TableHover = "#f3f4f8",
            Divider = "#e2e5ed",
            DividerLight = "#eef0f5",
            Skeleton = "#e8ebf1",
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
