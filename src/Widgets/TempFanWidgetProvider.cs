using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TempFanWidget.Cards;
using TempFanWidget.Hardware;
using TempFanWidget.Sensors;

namespace TempFanWidget.Widgets;

/// <summary>
/// The COM-activated glue between the Windows widget host and our
/// hardware monitor. The CLSID in <see cref="GuidAttribute"/> must match
/// <c>Package.appxmanifest</c> (<c>com:Class</c> + <c>CreateInstance</c>) —
/// keep the three in sync; it is the pin that ties runtime to package.
/// </summary>
/// <remarks>
/// The host may recreate this object at any time (it COM-activates a fresh
/// process running <see cref="Program"/>, so state must not live here beyond
/// what recovery below rebuilds). The constructor therefore also *recovers*:
/// it re-reads everything we ever registered through <c>WidgetManager</c> and
/// re-pins each widget without waiting for a new CreateWidget callback.
/// </remarks>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[GuidAttribute("F6EF3C98-9CBA-495E-853F-78C332113F2B")]
public sealed class TempFanWidgetProvider : IWidgetProvider
{
    /// <summary>Track recovery so a second activation never double-reposts.</summary>
    private static bool _recovered;

    // Maps widget-id → its cached context. Records the pinned set, which is
    // also the authoritative "how many widgets alive" for the monitor timer.
    // Concurrent: the host calls in on COM threads while the poll timer
    // enumerates it from a thread-pool thread.
    private readonly ConcurrentDictionary<string, WidgetContext> _widgets = new();
    private readonly HardwareMonitor _monitor = HardwareMonitor.Default;

    /// <summary>COM entry point — the host constructs one instance per activation.</summary>
    public TempFanWidgetProvider()
    {
        _monitor.SnapshotChanged += PushToAll;
        RecoverRunningWidgets();
    }

    /// <summary>
    /// On a fresh activation, the widget host does not replay CreateWidget for
    /// already-pinned widgets; we re-read our own registrations instead. If a
    /// stored definition no longer exists in the manifest (renamed/unpinned
    /// between builds) it is deleted so the host doesn't show a zombie.
    /// </summary>
    private void RecoverRunningWidgets()
    {
        if (_recovered)
            return;

        try
        {
            var infos = WidgetManager.GetDefault().GetWidgetInfos();
            if (infos is null)
                return;

            foreach (var info in infos)
            {
                var context = info.WidgetContext;
                _widgets[context.Id] = context;
                Push(context);
            }

            // N widgets are alive again without any CreateWidget callback.
            if (_widgets.Count > 0)
                _monitor.SetWidgetCount(_widgets.Count);
        }
        catch
        {
            // WidgetManager can be momentarily unavailable at boot; a later
            // CreateWidget / Activate brings us back regardless.
        }
        finally
        {
            _recovered = true;
        }
    }

    /// <summary>Called when the user pins the widget — re-post and start polling.</summary>
    public void CreateWidget(WidgetContext widgetContext)
    {
        _widgets[widgetContext.Id] = widgetContext;

        // The first widget starts the timer and queues an immediate poll; its
        // result reaches this widget through PushToAll. Until then (or if
        // another widget already filled the cache) show what we have.
        _monitor.SetWidgetCount(_widgets.Count);
        Push(widgetContext);
    }

    /// <summary>
    /// Called when the user unpins — drop the id; stops polling at zero and
    /// lets the process exit (the host re-launches it on the next pin).
    /// </summary>
    public void DeleteWidget(string widgetId, string customState)
    {
        if (!_widgets.TryRemove(widgetId, out _))
            return;

        _monitor.SetWidgetCount(_widgets.Count);
        if (_widgets.IsEmpty)
            ProcessLifetime.RequestExit();
    }

    /// <summary>Widget size changed (or re-pinned): re-pick the template.</summary>
    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        _widgets[args.WidgetContext.Id] = args.WidgetContext;
        Push(args.WidgetContext);
    }

    /// <summary>Fired when the user taps a card Action (e.g. the ↻ Refresh verb).</summary>
    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        if (args.Verb == "refresh" || args.Verb.Contains("refresh", StringComparison.Ordinal))
            _monitor.PollNow();
    }

    /// <summary>Widget became visible; nothing to gate here — we already poll.</summary>
    public void Activate(WidgetContext widgetContext) { }

    /// <summary>Widget left the viewport; polling continues for simplicity.</summary>
    public void Deactivate(string widgetId) { }

    // ————— internals —————

    /// <summary>Smoother: every meaningful snapshot re-renders every pinned widget.</summary>
    private void PushToAll(SensorSnapshot _)
    {
        var snapshot = _monitor.Current;
        if (snapshot is null)
            return;

        foreach (var context in _widgets.Values)
        {
            try
            {
                Push(context);
            }
            catch
            {
                // A widget the host already dropped must not starve the others.
            }
        }
    }

    /// <summary>Turn a stored context into a concrete template/data payload.</summary>
    private void Push(WidgetContext context)
    {
        var snapshot = _monitor.Current;
        var data = CardFactory.BuildDataJson(snapshot);

        var update = new WidgetUpdateRequestOptions(context.Id)
        {
            Template = CardFactory.TemplateFor(context.Size),
            Data = data,
        };
        WidgetManager.GetDefault().UpdateWidget(update);
    }
}
