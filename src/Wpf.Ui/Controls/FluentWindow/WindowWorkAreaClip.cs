// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Wpf.Ui.Interop;

namespace Wpf.Ui.Controls;

/// <summary>
/// Keeps the client area of a maximized window inside the work area of the monitor it is on.
/// </summary>
/// <remarks>
/// <para>
/// A window that carries a border style is maximized to the work area <em>plus</em> the frame it draws
/// around itself, so the window rectangle reaches past the taskbar on every edge. The WPF
/// <see cref="System.Windows.Shell.WindowChrome"/> removes the caption but keeps that rectangle, which
/// is why the client area of a maximized <see cref="FluentWindow"/> ends up taller than the work area:
/// on a 1080p screen with a 48 pixel taskbar the content is 1040 pixels high instead of 1032.
/// </para>
/// <para>
/// The correction is made where the client area is decided - <c>WM_NCCALCSIZE</c> - rather than by
/// padding the content afterwards, because padding cannot make the content narrower than the window
/// that hosts it. The proposed rectangle is read after WPF has produced it and only narrowed, never
/// grown, so every other decision the framework makes is left intact.
/// </para>
/// <para>
/// This is deliberately not a chrome implementation: it answers one message and nothing else. It does
/// not replace <see cref="System.Windows.Shell.WindowChrome"/>, and it installs no other hook.
/// </para>
/// <para>
/// One instance belongs to one window and holds the only hook it installs, which is why the type is
/// disposable and why a host keeps it in a field rather than looking it up by window. That is the same
/// shape as <see cref="System.Windows.Shell.WindowChrome"/>, so a window that uses both owns both the same way.
/// </para>
/// </remarks>
internal sealed class WindowWorkAreaClip : IDisposable
{
    /// <summary>
    /// The offset of the top edge inside the proposed client rectangle.
    /// </summary>
    private const int TOP_OFFSET = 4;

    /// <summary>
    /// The offset of the bottom edge inside the proposed client rectangle. The rectangle is the first
    /// field of <c>NCCALCSIZE_PARAMS</c>, which is what allows the parameter to be read as a bare
    /// <c>RECT</c> in both forms of the message.
    /// </summary>
    private const int BOTTOM_OFFSET = 12;

    /// <summary>
    /// Held in a field so that <c>AddHook</c> and <c>RemoveHook</c> always receive the very same
    /// delegate instance, instead of two method group conversions that merely compare equal.
    /// </summary>
    private readonly HwndSourceHook hook;

    private readonly HwndSource hwndSource;

    private bool isDisposed;

    private WindowWorkAreaClip(HwndSource hwndSource)
    {
        this.hwndSource = hwndSource;
        hook = OnMessage;
    }

    /// <summary>
    /// Starts clipping the client area of the given window to the work area of its monitor.
    /// </summary>
    /// <param name="window">The window to correct. It must already have a handle.</param>
    /// <returns>
    /// The clip, or <see langword="null"/> when the window cannot be hooked yet - which is useful
    /// during design time and inside templates that are never rendered.
    /// </returns>
    /// <remarks>
    /// Call this from <c>OnSourceInitialized</c> after the base implementation, because the hook has to
    /// run after the one <see cref="System.Windows.Shell.WindowChrome"/> installs for the same window.
    /// </remarks>
    public static WindowWorkAreaClip? Attach(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var source = HwndSource.FromHwnd(handle);

        if (source is null || source.IsDisposed || source.CompositionTarget is null)
        {
            return null;
        }

        var clip = new WindowWorkAreaClip(source);

        clip.AddHook();

        return clip;
    }

    /// <summary>
    /// Removes the hook from the window it was attached to.
    /// </summary>
    /// <remarks>
    /// Disposing twice is harmless.
    /// </remarks>
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        if (!hwndSource.IsDisposed)
        {
            hwndSource.RemoveHook(hook);
        }
    }

    private void AddHook() => hwndSource.AddHook(hook);

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (isDisposed || msg != PInvoke.WM_NCCALCSIZE || wParam == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        ClipToWorkArea(hwnd, lParam);

        handled = true;

        return new IntPtr(PInvoke.WVR_REDRAW | PInvoke.WVR_VALIDRECTS);
    }

    /// <summary>
    /// Narrows the proposed client rectangle so that it cannot reach past the work area of the monitor
    /// the window is on.
    /// </summary>
    /// <param name="hwnd">The window the message belongs to.</param>
    /// <param name="lParam">A pointer to a <c>RECT</c> or to an <c>NCCALCSIZE_PARAMS</c>.</param>
    /// <remarks>
    /// Only the bottom edge is moved. It is the one an ordinary taskbar takes space away from, and
    /// leaving the other three edges alone keeps this from interacting with the way the framework
    /// centres a maximized window. A rectangle that already fits, or one that lies entirely below the
    /// work area, is left untouched so that an ordinary resized window is never affected.
    /// </remarks>
    private static void ClipToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        if (!TryGetWorkAreaBottom(new HWND(hwnd), out int workAreaBottom))
        {
            return;
        }

        int top = Marshal.ReadInt32(lParam, TOP_OFFSET);
        int bottom = Marshal.ReadInt32(lParam, BOTTOM_OFFSET);

        if (bottom <= workAreaBottom || top >= workAreaBottom)
        {
            return;
        }

        Marshal.WriteInt32(lParam, BOTTOM_OFFSET, workAreaBottom);
    }

    /// <summary>
    /// Reads the bottom edge of the work area of the monitor a window is on.
    /// </summary>
    /// <param name="windowHandle">The window to resolve a monitor for.</param>
    /// <param name="bottom">Receives the bottom edge, in physical pixels.</param>
    /// <returns><see langword="true"/> when a work area could be read.</returns>
    /// <remarks>
    /// <para>
    /// The monitor rectangle and the client rectangle that <c>WM_NCCALCSIZE</c> carries are expressed in
    /// the same physical pixels, so no scaling has to be applied even when two monitors use different
    /// scale factors. That is why the monitor of the window is used rather than
    /// <c>SystemParametersInfo(SPI_GETWORKAREA)</c>, which only ever describes the primary monitor.
    /// </para>
    /// <para>
    /// A handle that is being torn down can fail to resolve. The work area of the primary monitor is a
    /// better answer than none, and it is what a single monitor system would have produced anyway.
    /// </para>
    /// </remarks>
    private static unsafe bool TryGetWorkAreaBottom(HWND windowHandle, out int bottom)
    {
        bottom = 0;

        HMONITOR monitor = PInvoke.MonitorFromWindow(windowHandle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (!monitor.IsNull)
        {
            var info = default(MONITORINFO);
            info.cbSize = (uint)sizeof(MONITORINFO);

            if (PInvoke.GetMonitorInfo(monitor, &info))
            {
                bottom = info.rcWork.bottom;

                return true;
            }
        }

        RECT primaryWorkArea = default;

        if (PInvoke.SystemParametersInfo(
                SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWORKAREA,
                0u,
                &primaryWorkArea,
                0u
            ))
        {
            bottom = primaryWorkArea.bottom;

            return true;
        }

        return false;
    }
}
