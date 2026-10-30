using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Windows.Widgets;
using TempFanWidget.Sensors;

namespace TempFanWidget.Cards;

/// <summary>
/// Turns a <see cref="SensorSnapshot"/> into the exact Adaptive Card payload
/// the Windows widget host expects: one template + one data JSON blob.
/// Keeps the widget host contract in one place so it's easy to change.
/// </summary>
public static class CardFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // CardViewModel property names match the ${{...}} placeholders, so
        // emit them camelCase to mirror what the templates expect.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The brand mark shown in the card header. It is the packaged tile that
    // scripts/make-icons.ps1 scales from Assets/logo.png, so re-branding that
    // one file re-brands the card too. Read once: the file never changes.
    private static readonly Lazy<string> LogoDataUri = new(() =>
        ReadLogoDataUri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.png")));

    /// <summary>
    /// The image as an inline <c>data:</c> URI (Adaptive Card schema 1.2+), which
    /// needs no hosting. Empty when the file is missing (unpackaged run), and
    /// the templates hide the logo column in that case.
    /// </summary>
    internal static string ReadLogoDataUri(string path)
    {
        try
        {
            return File.Exists(path)
                ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Pick the template that matches a widget size.</summary>
    public static string TemplateFor(WidgetSize size) => size switch
    {
        WidgetSize.Small => CardTemplates.Small,
        WidgetSize.Large => CardTemplates.Large,
        _ => CardTemplates.Medium,
    };

    /// <summary>Serialise a snapshot into the data payload a template binds to.</summary>
    public static string BuildDataJson(SensorSnapshot? snapshot)
    {
        var model = snapshot is not null ? ToViewModel(snapshot) : BuildEmptyViewModel();
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    /// <summary>Default data model when no sensor snapshot has been polled yet.</summary>
    public static CardViewModel BuildEmptyViewModel()
    {
        return new CardViewModel(
            CpuTempDisplay: "—",
            GpuTempDisplay: "—",
            CpuFanDisplay: "—",
            GpuFanDisplay: "—",
            CpuTempColor: "Default",
            GpuTempColor: "Default",
            CpuLoadDisplay: "—",
            SourceDisplay: "Awaiting sensor feed",
            UpdatedDisplay: DateTime.Now.ToString("HH:mm:ss"),
            DownloadDisplay: "—",
            UploadDisplay: "—",
            LogoDataUri: LogoDataUri.Value,
            CpuNote: string.Empty);
    }

    /// <summary>
    /// Human-facing projection of a snapshot. Pure function — same snapshot
    /// always produces the same view model, which makes diffs trivial to test.
    /// </summary>
    public static CardViewModel ToViewModel(SensorSnapshot s)
    {
        var cpuFan = s.FanContaining("cpu");
        var gpuFan = s.FanContaining("gpu");

        return new CardViewModel(
            CpuTempDisplay: CardViewModelFactory.Temp(s.CpuTemperatureC),
            GpuTempDisplay: CardViewModelFactory.Temp(s.GpuTemperatureC),
            CpuFanDisplay: CardViewModelFactory.Fan(cpuFan?.Rpm),
            GpuFanDisplay: CardViewModelFactory.Fan(gpuFan?.Rpm),
            CpuTempColor: CardViewModelFactory.TempColor(s.CpuTemperatureC),
            GpuTempColor: CardViewModelFactory.TempColor(s.GpuTemperatureC),
            CpuLoadDisplay: CardViewModelFactory.Load(s.CpuLoadPercent),
            SourceDisplay: s.SourceDescription,
            UpdatedDisplay: s.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
            DownloadDisplay: CardViewModelFactory.Rate(s.DownloadBytesPerSec),
            UploadDisplay: CardViewModelFactory.Rate(s.UploadBytesPerSec),
            LogoDataUri: LogoDataUri.Value,
            CpuNote: s.CpuTemperatureIsEstimate ? " · EST." : string.Empty);
    }
}
