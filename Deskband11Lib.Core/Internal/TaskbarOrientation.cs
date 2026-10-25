using Windows.Win32.Foundation;

namespace Deskband11Lib.Core.Internal;

/// <summary>Fork 新增：任务栏方向。主轴 = 沿条带方向；竖排任务栏的主轴为 Y。</summary>
internal enum TaskbarOrientation
{
    Horizontal,
    Vertical
}

internal static class TaskbarOrientationDetector
{
    public static TaskbarOrientation Detect(RECT taskbarRectangle)
    {
        var width = taskbarRectangle.right - taskbarRectangle.left;
        var height = taskbarRectangle.bottom - taskbarRectangle.top;
        return width >= height ? TaskbarOrientation.Horizontal : TaskbarOrientation.Vertical;
    }
}
