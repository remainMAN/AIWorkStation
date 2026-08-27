using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using AIWorkStation.ViewModels;

namespace AIWorkStation;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
        : this(new MainViewModel(), initializeOnLoad: true)
    {
    }

    internal MainWindow(MainViewModel viewModel, bool initializeOnLoad)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        SourceInitialized += (_, _) => ApplyWorkAreaBounds(preferPointerMonitor: true);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(() => ApplyWorkAreaBounds(preferPointerMonitor: false));
        };
        if (initializeOnLoad)
            Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Closing += (_, args) =>
        {
            if (ViewModel.IsApplying)
            {
                args.Cancel = true;
                MessageBox.Show("正在安全写入、验证或恢复网络配置，请等待当前操作完成后再关闭。",
                    "AI WorkStation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ViewModel.CancelLatencyTests(detach: true);
            ViewModel.ClearPlaintextPassword();
        };
    }

    private void ApplyWorkAreaBounds(bool preferPointerMonitor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = preferPointerMonitor && NativeMethods.GetCursorPos(out var pointer)
            ? NativeMethods.MonitorFromPoint(pointer, NativeMethods.MonitorDefaultToNearest)
            : NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = NativeMethods.MonitorInfo.Create();
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null) return;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        var workArea = new Rect(topLeft, bottomRight);
        var placement = CalculateWindowPlacement(workArea, new Size(1180, 760), new Size(960, 640), 24);

        MinWidth = placement.Minimum.Width;
        MinHeight = placement.Minimum.Height;
        MaxWidth = placement.Maximum.Width;
        MaxHeight = placement.Maximum.Height;
        Width = Math.Min(Math.Max(ActualWidth > 0 ? ActualWidth : Width, MinWidth), MaxWidth);
        Height = Math.Min(Math.Max(ActualHeight > 0 ? ActualHeight : Height, MinHeight), MaxHeight);
        if (preferPointerMonitor)
        {
            Width = placement.Bounds.Width;
            Height = placement.Bounds.Height;
            Left = placement.Bounds.Left;
            Top = placement.Bounds.Top;
        }
        else
        {
            Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
    }

    internal static WindowPlacement CalculateWindowPlacement(Rect workArea, Size recommended, Size minimum, double margin)
    {
        var availableWidth = Math.Max(320, workArea.Width - margin * 2);
        var availableHeight = Math.Max(240, workArea.Height - margin * 2);
        var minWidth = Math.Min(minimum.Width, availableWidth);
        var minHeight = Math.Min(minimum.Height, availableHeight);
        var width = Math.Min(recommended.Width, availableWidth);
        var height = Math.Min(recommended.Height, availableHeight);
        var left = workArea.Left + Math.Max(margin, (workArea.Width - width) / 2);
        var top = workArea.Top + Math.Max(margin, (workArea.Height - height) / 2);
        return new(new Rect(left, top, width, height), new Size(minWidth, minHeight), new Size(availableWidth, availableHeight));
    }

    internal bool IsInsideCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = NativeMethods.MonitorInfo.Create();
        return handle != IntPtr.Zero && monitor != IntPtr.Zero &&
               NativeMethods.GetMonitorInfo(monitor, ref info) && NativeMethods.GetWindowRect(handle, out var window) &&
               window.Left >= info.Work.Left && window.Top >= info.Work.Top &&
               window.Right <= info.Work.Right && window.Bottom <= info.Work.Bottom;
    }

    internal readonly record struct WindowPlacement(Rect Bounds, Size Minimum, Size Maximum);

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
            internal static MonitorInfo Create() => new() { Size = Marshal.SizeOf<MonitorInfo>() };
        }
    }
}
