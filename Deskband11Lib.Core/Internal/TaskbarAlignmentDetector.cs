using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Deskband11Lib.Core.Internal;

internal static class TaskbarAlignmentDetector
{
    private const string AdvancedRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskbarAlignmentValueName = "TaskbarAl";
    private const double LeftAlignmentThresholdRatio = 0.15;

    public static TaskbarAlignment Detect(HWND taskbarWindow, ButtonSpan startButtonSpan, TaskbarOrientation orientation)
    {
        var registryAlignment = ReadRegistryAlignment();
        if (registryAlignment != TaskbarAlignment.Unknown) return registryAlignment;

        return InferAlignmentFromPosition(taskbarWindow, startButtonSpan, orientation);
    }

    public static TaskbarAlignment ReadRegistryAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedRegistryKeyPath);
            if (key?.GetValue(TaskbarAlignmentValueName) is int alignmentValue) return alignmentValue switch
            {
                0 => TaskbarAlignment.Left,
                1 => TaskbarAlignment.Center,
                _ => TaskbarAlignment.Unknown
            };
        }
        catch { }

        return TaskbarAlignment.Unknown;
    }

    /// <summary>Fork 改动：按主轴推断对齐（竖排任务栏的 Left = 顶端对齐）。</summary>
    private static TaskbarAlignment InferAlignmentFromPosition(HWND taskbarWindow, ButtonSpan startButtonSpan, TaskbarOrientation orientation)
    {
        if (!startButtonSpan.IsValid || taskbarWindow.IsNull) return TaskbarAlignment.Unknown;
        if (!PInvoke.GetWindowRect(taskbarWindow, out var taskbarRectangle)) return TaskbarAlignment.Unknown;

        var stripAxisLength = orientation == TaskbarOrientation.Horizontal ? taskbarRectangle.right - taskbarRectangle.left : taskbarRectangle.bottom - taskbarRectangle.top;
        if (stripAxisLength <= 0) return TaskbarAlignment.Unknown;

        var stripAxisStart = orientation == TaskbarOrientation.Horizontal ? taskbarRectangle.left : taskbarRectangle.top;
        return startButtonSpan.Start - stripAxisStart < stripAxisLength * LeftAlignmentThresholdRatio ? TaskbarAlignment.Left : TaskbarAlignment.Center;
    }
}
