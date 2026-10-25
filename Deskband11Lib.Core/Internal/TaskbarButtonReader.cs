using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Deskband11Lib.Core.Internal.GeneratedCom;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Deskband11Lib.Core.Internal;

internal sealed partial class TaskbarButtonReader : IDisposable
{
    private const uint ClassContextInProcServer = 0x1;
    private const uint CoInitializeMultithreaded = 0x0;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const int ButtonControlTypeIdentifier = 50000;
    private const int MaximumTaskbarButtonGap = 16;
    private const string StartButtonAutomationIdentifier = "StartButton";
    private const string WidgetsButtonAutomationIdentifier = "WidgetsButton";
    private const string SystemTrayIconAutomationIdentifier = "SystemTrayIcon";
    private const string NotifyItemIconAutomationIdentifier = "NotifyItemIcon";

    private readonly object _refreshLock = new();
    private TaskbarButtonGeometry _cachedGeometry;
    private bool _hasCachedGeometry;
    private bool _isRefreshRunning;
    private bool _isDisposed;

    public bool TryGetTaskbarButtonGeometry(HWND taskbarWindow, RECT searchRectangle, TaskbarOrientation orientation, out TaskbarButtonGeometry geometry)
    {
        geometry = default;
        if (taskbarWindow.IsNull) return false;

        QueueRefresh(taskbarWindow, searchRectangle, orientation);

        lock (_refreshLock)
        {
            if (!_hasCachedGeometry) return false;
            geometry = _cachedGeometry;
            return geometry.StartButton.IsValid || geometry.WidgetsButton.IsValid || geometry.TaskbarButtonsGroup.IsValid || geometry.NotificationArea.IsValid;
        }
    }

    public bool TryGetCachedTaskbarButtonGeometry(out TaskbarButtonGeometry geometry)
    {
        lock (_refreshLock)
        {
            geometry = _cachedGeometry;
            return _hasCachedGeometry;
        }
    }

    public Task RefreshAsync(HWND taskbarWindow, RECT searchRectangle, TaskbarOrientation orientation)
    {
        if (!TryBeginRefresh())
        {
            ForkDiag.Write("reader: RefreshAsync skipped, refresh already running or disposed");
            return Task.CompletedTask;
        }

        ForkDiag.WriteVerbose($"reader: RefreshAsync scheduling UIA read, orientation={orientation}");
        return Task.Run(() => RefreshCachedGeometry(taskbarWindow, searchRectangle, orientation));
    }

    public void Dispose()
    {
        lock (_refreshLock)
        {
            _isDisposed = true;
            _hasCachedGeometry = false;
            _cachedGeometry = default;
        }
    }

    private void QueueRefresh(HWND taskbarWindow, RECT searchRectangle, TaskbarOrientation orientation)
    {
        if (!TryBeginRefresh()) return;

        ThreadPool.QueueUserWorkItem(_ => RefreshCachedGeometry(taskbarWindow, searchRectangle, orientation));
    }

    private bool TryBeginRefresh()
    {
        lock (_refreshLock)
        {
            if (_isDisposed || _isRefreshRunning) return false;

            _isRefreshRunning = true;
            return true;
        }
    }

    private void RefreshCachedGeometry(HWND taskbarWindow, RECT searchRectangle, TaskbarOrientation orientation)
    {
        ForkDiag.WriteVerbose("reader: RefreshCachedGeometry entered");
        try
        {
            if (TryReadTaskbarGeometry(taskbarWindow, searchRectangle, orientation, out var geometry))
            {
                ForkDiag.WriteVerbose($"reader: geometry OK start={geometry.StartButton} widgets={geometry.WidgetsButton} group={geometry.TaskbarButtonsGroup} notif={geometry.NotificationArea}");
                lock (_refreshLock)
                {
                    if (_isDisposed) return;

                    _cachedGeometry = geometry;
                    _hasCachedGeometry = true;
                }
            }
            else
            {
                ForkDiag.Write("reader: measurement returned false");
            }
        }
        catch (Exception ex)
        {
            ForkDiag.Write($"reader: RefreshCachedGeometry swallowed {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_refreshLock)
            {
                _isRefreshRunning = false;
            }
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL2050", Justification = "UI Automation is an optional geometry probe. Failures keep the previous cached measurement.")]
    private static unsafe bool TryReadTaskbarGeometry(HWND taskbarWindow, RECT searchRectangle, TaskbarOrientation orientation, out TaskbarButtonGeometry geometry)
    {
        geometry = default;
        ForkDiag.WriteVerbose($"reader: TryReadTaskbarGeometry entered, orientation={orientation}");
        if (!TryGetWindowProcessIdentifier(taskbarWindow, out var taskbarProcessIdentifier)) return false;

        IGeneratedUIAutomation? automation = null;
        IGeneratedUIAutomationCondition? trueCondition = null;
        IGeneratedUIAutomationElement? taskbarElement = null;
        IGeneratedUIAutomationElementArray? taskButtonElements = null;
        List<GeneratedRectangle> taskbarAppButtonRectangles = [];
        var startButtonSpan = ButtonSpan.Invalid;
        var widgetsButtonSpan = ButtonSpan.Invalid;
        var notificationAreaStart = int.MaxValue;
        var notificationAreaEnd = int.MinValue;
        var coInitializeResult = CoInitializeEx(0, CoInitializeMultithreaded);
        var shouldUninitializeCom = coInitializeResult is 0 or 1;
        if (coInitializeResult < 0 && coInitializeResult != RpcChangedMode)
        {
            ForkDiag.Write($"reader: CoInitializeEx failed hr=0x{coInitializeResult:X8}");
            return false;
        }

        try
        {
            var classIdentifier = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
            var interfaceIdentifier = typeof(IGeneratedUIAutomation).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(&classIdentifier, 0, ClassContextInProcServer, &interfaceIdentifier, out automation));

            trueCondition = automation.CreateTrueCondition();
            taskbarElement = automation.ElementFromHandle(taskbarWindow);
            taskButtonElements = taskbarElement.FindAll(GeneratedTreeScope.Descendants, trueCondition);
            var count = taskButtonElements.GetLength();
            ForkDiag.WriteVerbose($"reader: UIA ok, {count} descendant elements under taskbar 0x{(long)taskbarWindow.Value:X}, searchRect={searchRectangle.left},{searchRectangle.top},{searchRectangle.right},{searchRectangle.bottom}");

            var visibleButtonCount = 0;
            var identifierSeen = new HashSet<string>();

            for (var index = 0; index < count; index++)
            {
                var taskButtonElement = taskButtonElements.GetElement(index);
                try
                {
                    var automationIdentifier = TryGetAutomationIdentifier(taskButtonElement);
                    var isNotificationAreaElement = automationIdentifier == SystemTrayIconAutomationIdentifier || automationIdentifier == NotifyItemIconAutomationIdentifier;

                    if (!string.IsNullOrEmpty(automationIdentifier) && identifierSeen.Add(automationIdentifier) && identifierSeen.Count <= 24)
                    {
                        ForkDiag.WriteVerbose($"reader: identifier '{automationIdentifier}' rect={TryDescribeRect(taskButtonElement)}");
                    }

                    if (!isNotificationAreaElement && !IsVisibleTaskbarButton(taskButtonElement, taskbarProcessIdentifier)) continue;
                    if (!TryGetBoundingRectangle(taskButtonElement, out var boundingRectangle)) continue;
                    if (automationIdentifier == StartButtonAutomationIdentifier)
                    {
                        ForkDiag.WriteVerbose($"reader: StartButton passed filters, rect L={boundingRectangle.Left} T={boundingRectangle.Top} R={boundingRectangle.Right} B={boundingRectangle.Bottom}");
                    }

                    visibleButtonCount++;

                    if (isNotificationAreaElement)
                    {
                        if (!IsInsideSearchRectangle(searchRectangle, boundingRectangle)) continue;
                        notificationAreaStart = Math.Min(notificationAreaStart, GetAxisStart(boundingRectangle, orientation));
                        notificationAreaEnd = Math.Max(notificationAreaEnd, GetAxisEnd(boundingRectangle, orientation));
                        continue;
                    }

                    if (!IsInsideSearchRectangle(searchRectangle, boundingRectangle)) continue;

                    if (automationIdentifier == StartButtonAutomationIdentifier)
                    {
                        startButtonSpan = ToAxisSpan(boundingRectangle, orientation);
                        continue;
                    }
                    if (automationIdentifier == WidgetsButtonAutomationIdentifier)
                    {
                        widgetsButtonSpan = ToAxisSpan(boundingRectangle, orientation);
                        continue;
                    }

                    taskbarAppButtonRectangles.Add(boundingRectangle);
                }
                finally { ReleaseComObject(taskButtonElement); }
            }

            var taskbarButtonsGroup = ResolveTaskbarButtonsGroup(startButtonSpan, taskbarAppButtonRectangles, orientation);
            var notificationAreaSpan = notificationAreaStart <= notificationAreaEnd ? new ButtonSpan(notificationAreaStart, notificationAreaEnd) : ButtonSpan.Invalid;
            geometry = new TaskbarButtonGeometry(startButtonSpan, widgetsButtonSpan, taskbarButtonsGroup, notificationAreaSpan);
            ForkDiag.WriteVerbose($"reader: loop done, visibleButtons={visibleButtonCount}, appButtonRects={taskbarAppButtonRectangles.Count}, start={startButtonSpan}, group={taskbarButtonsGroup}, notif={notificationAreaSpan}");
            return startButtonSpan.IsValid || widgetsButtonSpan.IsValid || taskbarButtonsGroup.IsValid || notificationAreaSpan.IsValid;
        }
        catch (COMException ex)
        {
            ForkDiag.Write($"reader: COMException hr=0x{ex.HResult:X8}: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(taskButtonElements);
            ReleaseComObject(taskbarElement);
            ReleaseComObject(trueCondition);
            ReleaseComObject(automation);
            if (shouldUninitializeCom) CoUninitialize();
        }
    }

    private static string TryGetAutomationIdentifier(IGeneratedUIAutomationElement element)
    {
        try
        {
            if (element.GetCurrentAutomationIdentifier(out var automationIdentifierHandle) < 0 || automationIdentifierHandle == 0) return string.Empty;
            try { return Marshal.PtrToStringBSTR(automationIdentifierHandle) ?? string.Empty; }
            finally { Marshal.FreeBSTR(automationIdentifierHandle); }
        }
        catch { return string.Empty; }
    }

    private static string TryDescribeRect(IGeneratedUIAutomationElement element)
    {
        try
        {
            return element.GetCurrentBoundingRectangle(out var rectangle) < 0 ? "(hr<0)" : $"({rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom})";
        }
        catch { return "(?)"; }
    }

    /// <summary>Fork 改动：按钮组解析按任务栏主轴排序与聚簇。</summary>
    private static ButtonSpan ResolveTaskbarButtonsGroup(ButtonSpan startButtonSpan, List<GeneratedRectangle> taskbarAppButtonRectangles, TaskbarOrientation orientation)
    {
        if (taskbarAppButtonRectangles.Count == 0) return ButtonSpan.Invalid;

        var anchor = startButtonSpan.IsValid ? startButtonSpan.End : 0;
        var groupEnd = anchor;
        var groupStart = int.MaxValue;

        foreach (var rectangle in taskbarAppButtonRectangles.OrderBy(rectangle => GetAxisStart(rectangle, orientation)))
        {
            if (GetAxisStart(rectangle, orientation) > groupEnd + MaximumTaskbarButtonGap) break;

            groupStart = Math.Min(groupStart, GetAxisStart(rectangle, orientation));
            groupEnd = Math.Max(groupEnd, GetAxisEnd(rectangle, orientation));
        }

        return groupEnd > anchor ? new ButtonSpan(groupStart, groupEnd) : ButtonSpan.Invalid;
    }

    private static ButtonSpan ToAxisSpan(GeneratedRectangle rectangle, TaskbarOrientation orientation) =>
        orientation == TaskbarOrientation.Horizontal
            ? new ButtonSpan(rectangle.Left, rectangle.Right)
            : new ButtonSpan(rectangle.Top, rectangle.Bottom);

    private static int GetAxisStart(GeneratedRectangle rectangle, TaskbarOrientation orientation) =>
        orientation == TaskbarOrientation.Horizontal ? rectangle.Left : rectangle.Top;

    private static int GetAxisEnd(GeneratedRectangle rectangle, TaskbarOrientation orientation) =>
        orientation == TaskbarOrientation.Horizontal ? rectangle.Right : rectangle.Bottom;

    private static bool IsVisibleTaskbarButton(IGeneratedUIAutomationElement element, int taskbarProcessIdentifier)
    {
        if (element.GetCurrentProcessIdentifier(out var processIdentifier) < 0 || processIdentifier != taskbarProcessIdentifier) return false;
        if (element.GetCurrentControlType(out var controlType) < 0 || controlType != ButtonControlTypeIdentifier) return false;
        if (element.GetCurrentIsOffscreen(out var isOffscreen) < 0 || isOffscreen != 0) return false;
        return true;
    }

    private static bool TryGetWindowProcessIdentifier(HWND windowHandle, out int processIdentifier)
    {
        processIdentifier = 0;
        var threadIdentifier = PInvoke.GetWindowThreadProcessId(windowHandle, out var nativeProcessIdentifier);
        if (threadIdentifier == 0 || nativeProcessIdentifier == 0 || nativeProcessIdentifier > int.MaxValue) return false;

        processIdentifier = (int)nativeProcessIdentifier;
        return true;
    }

    private static bool TryGetBoundingRectangle(IGeneratedUIAutomationElement element, out GeneratedRectangle boundingRectangle)
    {
        boundingRectangle = default;
        if (element.GetCurrentBoundingRectangle(out boundingRectangle) < 0) return false;
        return boundingRectangle.Right > boundingRectangle.Left && boundingRectangle.Bottom > boundingRectangle.Top;
    }

    private static bool IsInsideSearchRectangle(RECT rectangle, GeneratedRectangle generatedRectangle)
    {
        return generatedRectangle.Right > rectangle.left && generatedRectangle.Left < rectangle.right && generatedRectangle.Top < rectangle.bottom && generatedRectangle.Bottom > rectangle.top;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(Guid* classIdentifier, nint outerUnknown, uint classContext, Guid* interfaceIdentifier, out IGeneratedUIAutomation automation);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInitialize);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
