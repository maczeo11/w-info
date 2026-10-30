namespace TempFanWidget.Cards;

/// <summary>
/// Everything an Adaptive Card template can reference via <c>${{...}}</c>
/// placeholders. Add a property here + a placeholder in <see cref="CardTemplates"/>
/// to introduce a new visual, without touching the widget plumbing.
/// </summary>
/// <remarks>
/// Serialised via <see cref="System.Text.Json.JsonSerializer"/> with
/// camelCase; property names deliberately match template placeholder names.
/// </remarks>
public sealed record CardViewModel(
    string CpuTempDisplay,
    string GpuTempDisplay,
    string CpuFanDisplay,
    string GpuFanDisplay,
    string CpuTempColor,
    string GpuTempColor,
    string CpuLoadDisplay,
    string SourceDisplay,
    string UpdatedDisplay,
    string DownloadDisplay,
    string UploadDisplay,
    string LogoDataUri,
    string CpuNote);

/// <summary>Formatting rules for each UI fill-in.</summary>
public static class CardViewModelFactory
{
    /// <summary>Render a temperature for display. Uses °C only for accuracy;
    /// localisation will swap this at runtime in future cards.</summary>
    public static string Temp(double? value) =>
        value.HasValue ? $"{value.Value:0.#} °C" : "—";

    /// <summary>Render a fan RPM.</summary>
    public static string Fan(int? rpm) =>
        rpm.HasValue ? $"{rpm.Value} RPM" : "—";

    /// <summary>Render CPU load %.</summary>
    public static string Load(double? percent) =>
        percent.HasValue ? $"{percent.Value:0}%" : "—";

    /// <summary>Render a transfer rate in the unit that keeps it short
    /// (B/s, KB/s, MB/s, GB/s; bytes, not bits, like a download manager).</summary>
    public static string Rate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate)
            return "—";

        return rate switch
        {
            < 1024 => $"{rate:0} B/s",
            < 1024 * 1024 => $"{rate / 1024:0} KB/s",
            < 1024.0 * 1024 * 1024 => $"{rate / (1024 * 1024):0.0} MB/s",
            _ => $"{rate / (1024.0 * 1024 * 1024):0.00} GB/s",
        };
    }

    /// <summary>Adaptive Card colour keyword chosen from a threshold.
    /// 	&lt;60 °C Normal (green), 60–79 Warning (amber), 80+ Attention (red).</summary>
    public static string TempColor(double? celsius)
        => celsius switch
        {
            null => "Default",
            >= 80 => "Attention",
            >= 60 => "Warning",
            _ => "Good",
        };
}
