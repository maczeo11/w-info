using Microsoft.Windows.Widgets.Providers;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TempFanWidget.Widgets;

/// <summary>
/// Local COM-server plumbing that lets the Windows widget host reach our
/// managed provider.
///
/// How activation works end to end:
///   1. The Widgets Board reads <c>Package.appxmanifest</c> and finds our
///      <c>com:ComServer</c> entry (an <c>ExeServer</c> whose CLSID matches
///      this file's provider) plus the <c>com.microsoft.windows.widgets</c>
///      app-extension that lists our widget definitions. Both share one CLSID.
///   2. When the user pins the widget, the host exec-launches
///      <c>TempFanWidget.exe -RegisterProcessAsComServer</c>.
///   3. Our <see cref="Program"/> detects the flag and calls
///      <see cref="RegistrationManager{TWidgetProvider}.RegisterProvider"/>,
///      which registers an <see cref="IClassFactory"/> under that CLSID
///      (classic <c>CoRegisterClassObject</c>).
///   4. The host CoCreates the class; the factory hands back a WinRT-wrapped
///      instance of <see cref="TempFanWidgetProvider"/> via
///      <c>WinRT.MarshalInspectable&lt;IWidgetProvider&gt;.FromManaged</c>.
///   5. The host calls the standard <c>IWidgetProvider</c> callbacks
///      (CreateWidget / DeleteWidget / OnActionInvoked / …).
///
/// This mirrors Microsoft's official sample:
/// WindowsAppSDK-Samples/Samples/Widgets/cs-console-packaged (MIT).
/// </summary>
internal static class Com
{
    /// <summary>Standard well-known COM interface GUIDs.</summary>
    internal static class Guids
    {
        /// <summary>The one interface every COM object must expose.</summary>
        public const string IUnknown = "00000000-0000-0000-C000-000000000046";

        /// <summary>The activator interface the host uses to create us.</summary>
        public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
    }

    /// <summary>
    /// Raw (non-projected) <c>IClassFactory</c>. Declared by hand because the
    /// widget host treats us as a classic local COM server, not a WinRT
    /// activatable class — this shim is what bridges the two worlds.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid(Guids.IClassFactory)]
    internal interface IClassFactory
    {
        /// <summary>Host asks for one instance; we hand back the provider.</summary>
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        /// <summary>Lock/unlock the server in memory; we never lock.</summary>
        [PreserveSig]
        int LockServer(bool fLock);
    }

    /// <summary>Thin, exception-friendly facade over the registration APIs.</summary>
    internal static class ClassObject
    {
        private const uint CLSCTX_LOCAL_SERVER = 0x4; // "out-of-proc" server
        private const uint REGCLS_MULTIPLEUSE = 0x1; // one server may serve many clients

        /// <summary>
        /// Publishes our factory so any process in the COM catalog can create
        /// objects under <paramref name="clsid"/>; we become the server for it.
        /// </summary>
        public static void Register(Guid clsid, object factory, out uint cookie)
        {
            [DllImport("ole32.dll")]
            static extern int CoRegisterClassObject(
                [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
                [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
                uint dwClsContext, uint flags, out uint lpdwRegister);

            var hr = CoRegisterClassObject(clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        /// <summary>Withdraws the registration started by <see cref="Register"/>.</summary>
        public static int Revoke(uint cookie)
        {
            [DllImport("ole32.dll")]
            static extern int CoRevokeClassObject(uint dwRegister);

            return CoRevokeClassObject(cookie);
        }
    }
}

/// <summary>
/// The <see cref="Com.IClassFactory"/> implementation the host actually talks
/// to. Its only job: create managed provider objects and expose their WinRT
/// representation to the host.
/// </summary>
/// <typeparam name="T">The IWidgetProvider implementing class to instantiate.</typeparam>
internal sealed class WidgetProviderFactory<T> : Com.IClassFactory
    where T : IWidgetProvider, new()
{
    // Standard COM failure codes, for readability below.
    private const int CLASS_E_NOAGGREGATION = -2147221232;
    private const int E_NOINTERFACE = -2147467262;

    int Com.IClassFactory.CreateInstance(IntPtr outerUnknown, ref Guid requestedIid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        // We don't support COM aggregation; refuse cleanly rather than fail.
        if (outerUnknown != IntPtr.Zero)
            Marshal.ThrowExceptionForHR(CLASS_E_NOAGGREGATION);

        if (requestedIid == typeof(T).GUID || requestedIid == Guid.Parse(Com.Guids.IUnknown))
        {
            // Wrap the managed object into its WinRT CCW equivalent so the
            // host can call IWidgetProvider across the interop boundary.
            ppvObject = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(new T());
            return 0; // S_OK
        }

        // The caller wants something we don't expose; report the standard code.
        Marshal.ThrowExceptionForHR(E_NOINTERFACE);
        return E_NOINTERFACE;
    }

    int Com.IClassFactory.LockServer(bool fLock) => 0; // we never lock the server
}

/// <summary>
/// Registers the provider class under its CLSID for the lifetime of this
/// object, then revokes it when disposed — the host then knows it may exit.
/// Owns the registration so lifetime is RAII-simple: create, wait, dispose.
/// </summary>
internal sealed class RegistrationManager<TWidgetProvider> : IDisposable
    where TWidgetProvider : IWidgetProvider, new()
{
    private readonly IDisposable _registration;

    /// <summary>Creates the factory and publishes it under the provider's CLSID.</summary>
    private RegistrationManager(IDisposable registration) => _registration = registration;

    /// <summary>
    /// The one call from <see cref="Program"/>. The CLSID comes from the
    /// <c>Guid</c> attribute on the provider class itself, so manifest,
    /// attribute and registration stay a single source of truth.
    /// </summary>
    public static RegistrationManager<TWidgetProvider> RegisterProvider()
    {
        Com.ClassObject.Register(typeof(TWidgetProvider).GUID, new WidgetProviderFactory<TWidgetProvider>(), out var cookie);
        return new RegistrationManager<TWidgetProvider>(new Registration(cookie));
    }

    /// <summary>
    /// Called by <see cref="Program"/> once; blocks until the provider reports,
    /// through <see cref="ProcessLifetime"/>, that its last widget is gone.
    /// </summary>
    public void WaitUntilRevoked() => ProcessLifetime.ExitRequested.WaitOne();

    private sealed class Registration : IDisposable
    {
        private readonly uint _cookie;
        public Registration(uint cookie) => _cookie = cookie;

        public void Dispose()
        {
            Com.ClassObject.Revoke(_cookie);
        }
    }

    public void Dispose() => _registration.Dispose();
}

/// <summary>
/// Lets the provider tell <see cref="Program"/> that nothing is left to serve.
/// Without it the COM server would idle for ever after the last widget is
/// unpinned. The host simply launches a fresh process on the next pin, the
/// same lifecycle as Microsoft's widget sample.
/// </summary>
internal static class ProcessLifetime
{
    private static readonly ManualResetEvent Exit = new(false);

    /// <summary>Signalled once the last widget has been deleted.</summary>
    public static WaitHandle ExitRequested => Exit;

    public static void RequestExit() => Exit.Set();
}
