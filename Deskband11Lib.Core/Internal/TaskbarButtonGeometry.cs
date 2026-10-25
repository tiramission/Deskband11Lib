namespace Deskband11Lib.Core.Internal;

/// <summary>
/// Fork 改动：ButtonSpan 语义 = 沿任务栏主轴的跨度（横排为 X 轴 Left→Right，竖排为 Y 轴 Top→Bottom）。
/// </summary>
internal readonly record struct ButtonSpan(int Start, int End)
{
    public static ButtonSpan Invalid => new(0, 0);

    public bool IsValid => End > Start;

    public int Length => Math.Max(0, End - Start);
}

internal readonly record struct TaskbarButtonGeometry(ButtonSpan StartButton, ButtonSpan WidgetsButton, ButtonSpan TaskbarButtonsGroup, ButtonSpan NotificationArea);
