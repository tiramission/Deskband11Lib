namespace Deskband11Lib.Core.Internal;

/// <summary>
/// Fork 诊断钩子：宿主应用挂接后可观测库内部探测过程（竖排任务栏适配开发期使用）。
/// </summary>
internal static class ForkDiag
{
    public static Action<string>? Trace { get; set; }

    /// <summary>逐元素级诊断输出开关（默认关闭，避免 500ms 布局刷新刷屏）。</summary>
    public static bool Verbose { get; set; }

    public static void Write(string message) => Trace?.Invoke(message);

    public static void WriteVerbose(string message)
    {
        if (Verbose) Trace?.Invoke(message);
    }
}
