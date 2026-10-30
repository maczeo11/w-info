using TempFanWidget.Widgets;

namespace TempFanWidget;

/// <summary>
/// Process entry point for the widget provider exe.
///
/// Two launch modes, decided purely by the command line:
///
///   • <c>-RegisterProcessAsComServer</c> — the Windows widget host starts
///     us (via the <c>com:ComServer</c> entry in Package.appxmanifest) when
///     our widget gets pinned. We publish our COM class factory, then idle
///     servicing the host until the last widget is unpinned, and exit.
///
///   • plain launch (double-click, Start menu) — a widget has no window of
///     its own, so we exit quietly. Everything a user ever sees happens in
///     the Widgets Board (Win + W), not here.
/// </summary>
public static class Program
{
    private const string ComServerArgument = "-RegisterProcessAsComServer";

    [MTAThread] // the host calls in on the multi-threaded apartment
    public static int Main(string[] args)
    {
        if (!IsComServerLaunch(args))
            return 0; // launched as a normal app: nothing to show, exit silently

        // CsWinRT needs its interop initialised before *any* projected WinRT
        // call, including the registration below.
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Publishes the factory under our provider's Guid (which also matches
        // the manifest) and keeps the server alive while the host holds it.
        using var provider = RegistrationManager<TempFanWidgetProvider>.RegisterProvider();
        provider.WaitUntilRevoked(); // returns once the last widget has been unpinned

        return 0;
    }

    private static bool IsComServerLaunch(IReadOnlyList<string> args)
        => args.Count > 0 && args.Contains(ComServerArgument);
}
