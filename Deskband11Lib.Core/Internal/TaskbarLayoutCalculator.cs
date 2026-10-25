using Windows.Win32;
using Windows.Win32.Foundation;

namespace Deskband11Lib.Core.Internal;

/// <summary>
/// Fork 改动：布局计算全面轴向化。
/// "主轴"（axis）指沿任务栏条带的方向——横排任务栏为 X 轴，竖排任务栏为 Y 轴；
/// "厚度"（cross extent）指垂直于主轴的任务栏宽度。
/// PreferredWidth 语义 = 沿主轴的期望长度；PreferredHeight = 厚度方向期望值。
/// </summary>
internal sealed class TaskbarLayoutCalculator(TaskbarWindowLocator taskbarWindowLocator, TaskbarContentHostOptions options, TaskbarButtonReader taskbarButtonReader, Func<int> getEffectiveMonitorIdentity)
{
    private static double? s_lastScaleFactor;

    public async Task RefreshTaskbarButtonMeasurementAsync()
    {
        if (!options.TrackTaskbarButtons) return;
        if (!taskbarWindowLocator.TryRefresh(getEffectiveMonitorIdentity()))
        {
            ForkDiag.WriteVerbose("calc: refresh skipped, locator TryRefresh failed");
            return;
        }
        if (!TryGetWindowRectangle(taskbarWindowLocator.TaskbarWindow, out var taskbarRectangle))
        {
            ForkDiag.WriteVerbose("calc: refresh skipped, no taskbar rect");
            return;
        }

        var orientation = TaskbarOrientationDetector.Detect(taskbarRectangle);
        var rowRectangle = TryGetWindowRectangle(taskbarWindowLocator.RebarWindow, out var rebarRectangle) ? rebarRectangle : taskbarRectangle;
        var notificationBoundary = ResolveNotificationBoundary(taskbarRectangle, orientation);
        ForkDiag.WriteVerbose($"calc: orientation={orientation} taskbarRect={taskbarRectangle.left},{taskbarRectangle.top},{taskbarRectangle.right},{taskbarRectangle.bottom} notifWindowNull={taskbarWindowLocator.NotificationWindow.IsNull} notificationBoundary={notificationBoundary}");
        if (TryGetTaskbarButtonsSearchRectangle(taskbarRectangle, rowRectangle, orientation, notificationBoundary, out var taskbarButtonsSearchRectangle)) await taskbarButtonReader.RefreshAsync(taskbarWindowLocator.TaskbarWindow, taskbarButtonsSearchRectangle, orientation);
        else ForkDiag.WriteVerbose("calc: search rectangle invalid, no refresh");
    }

    public TaskbarLayoutSnapshot Calculate(double requestedWidth, double requestedHeight, double scaleFactor, TaskbarSlotInfo? ownSlot, IReadOnlyList<TaskbarSlotInfo> siblingSlots)
    {
        if (!taskbarWindowLocator.TryRefresh(getEffectiveMonitorIdentity())) return default;
        if (!TryGetWindowRectangle(taskbarWindowLocator.TaskbarWindow, out var taskbarRectangle)) return default;
        scaleFactor = NormalizeScaleFactor(scaleFactor);

        var orientation = TaskbarOrientationDetector.Detect(taskbarRectangle);
        var rowRectangle = TryGetWindowRectangle(taskbarWindowLocator.RebarWindow, out var rebarRectangle) ? rebarRectangle : taskbarRectangle;
        var notificationBoundary = ResolveNotificationBoundary(taskbarRectangle, orientation);

        TaskbarButtonGeometry geometry = default;
        if (options.TrackTaskbarButtons && TryGetTaskbarButtonsSearchRectangle(taskbarRectangle, rowRectangle, orientation, notificationBoundary, out var taskbarButtonsSearchRectangle)) taskbarButtonReader.TryGetTaskbarButtonGeometry(taskbarWindowLocator.TaskbarWindow, taskbarButtonsSearchRectangle, orientation, out geometry);

        var alignment = TaskbarAlignmentDetector.Detect(taskbarWindowLocator.TaskbarWindow, geometry.StartButton, orientation);
        var notificationStart = geometry.NotificationArea.IsValid ? geometry.NotificationArea.Start : notificationBoundary;
        var (areaStart, areaEnd, startAlign) = SelectContentArea(options.Placement, alignment, taskbarRectangle, orientation, notificationStart, geometry);

        var availableWidth = Math.Max(0, areaEnd - areaStart);

        var resolvedPlacement = ResolveActualPlacement(options.Placement, alignment, leftAvailableWidth: Math.Max(0, ComputeStartGap(taskbarRectangle, orientation, geometry).End - ComputeStartGap(taskbarRectangle, orientation, geometry).Start), rightAvailableWidth: Math.Max(0, ComputeEndGap(notificationStart, geometry).End - ComputeEndGap(notificationStart, geometry).Start));

        if (availableWidth <= 0) return new TaskbarLayoutSnapshot(0, 0, 0, 0, 0, scaleFactor, false, resolvedPlacement);

        var requestedThicknessInPixels = Math.Max(1, (int)Math.Ceiling(requestedHeight * scaleFactor));
        var crossExtent = orientation == TaskbarOrientation.Horizontal ? rowRectangle.bottom - rowRectangle.top : rowRectangle.right - rowRectangle.left;
        var thickness = Math.Min(requestedThicknessInPixels, Math.Max(1, crossExtent));

        var effectiveOwnSlot = ownSlot.HasValue && ownSlot.Value.ActualPlacement != resolvedPlacement ? ownSlot.Value with { ActualPlacement = resolvedPlacement } : ownSlot;
        var siblingsInSameArea = FilterSiblingsInSameArea(siblingSlots, resolvedPlacement, getEffectiveMonitorIdentity());

        double allocatedWidth;
        double offsetX;

        if (effectiveOwnSlot.HasValue && siblingsInSameArea.Count > 0)
        {
            var allocation = AllocateWidth(effectiveOwnSlot.Value, siblingsInSameArea, availableWidth, scaleFactor);
            allocatedWidth = allocation.Width;
            offsetX = allocation.Offset;
        }
        else
        {
            var requestedWidthInPixels = Math.Max(1, (int)Math.Ceiling(requestedWidth * scaleFactor));
            allocatedWidth = Math.Min(requestedWidthInPixels, availableWidth);
            offsetX = 0;
        }

        if (allocatedWidth <= 0) return new TaskbarLayoutSnapshot(0, 0, 0, 0, 0, scaleFactor, false, resolvedPlacement);

        // Fork 改动：轴向偏移 → 屏幕坐标的映射按方向分支；
        // 交叉轴（条带厚度方向）在剩余空间内居中，避免贴边。
        var stripAxisStart = orientation == TaskbarOrientation.Horizontal ? taskbarRectangle.left : taskbarRectangle.top;
        var axisOffset = (startAlign ? areaStart + offsetX : areaEnd - offsetX - allocatedWidth) - stripAxisStart;
        var crossBase = orientation == TaskbarOrientation.Horizontal ? rowRectangle.top - taskbarRectangle.top : rowRectangle.left - taskbarRectangle.left;
        var crossOffset = crossBase + Math.Max(0, crossExtent - thickness) / 2;

        return orientation == TaskbarOrientation.Horizontal
            ? new TaskbarLayoutSnapshot(axisOffset, crossOffset, allocatedWidth, thickness, availableWidth, scaleFactor, true, resolvedPlacement)
            : new TaskbarLayoutSnapshot(crossOffset, axisOffset, thickness, allocatedWidth, availableWidth, scaleFactor, true, resolvedPlacement);
    }

    private static TaskbarContentPlacement ResolveActualPlacement(TaskbarContentPlacement requestedPlacement, TaskbarAlignment alignment, int leftAvailableWidth, int rightAvailableWidth)
    {
        return requestedPlacement switch
        {
            TaskbarContentPlacement.LeftEdge => alignment == TaskbarAlignment.Center ? TaskbarContentPlacement.LeftEdge : TaskbarContentPlacement.BeforeNotificationArea,
            TaskbarContentPlacement.BeforeStartButton => alignment == TaskbarAlignment.Center ? TaskbarContentPlacement.BeforeStartButton : TaskbarContentPlacement.BeforeNotificationArea,
            TaskbarContentPlacement.Auto => leftAvailableWidth > rightAvailableWidth ? TaskbarContentPlacement.LeftEdge : TaskbarContentPlacement.BeforeNotificationArea,
            _ => TaskbarContentPlacement.BeforeNotificationArea
        };
    }

    private static List<TaskbarSlotInfo> FilterSiblingsInSameArea(IReadOnlyList<TaskbarSlotInfo> allSlots, TaskbarContentPlacement resolvedPlacement, int monitorIdentity)
    {
        var result = new List<TaskbarSlotInfo>();
        foreach (var slot in allSlots)
        {
            if (slot.MonitorIdentity != monitorIdentity) continue;
            if (slot.ActualPlacement != resolvedPlacement) continue;
            result.Add(slot);
        }
        return result;
    }

    private static (double Width, double Offset) AllocateWidth(TaskbarSlotInfo ownSlot, List<TaskbarSlotInfo> siblings, int totalAvailableWidth, double scaleFactor)
    {
        var allSlots = new List<TaskbarSlotInfo>(siblings) { ownSlot };
        allSlots.Sort((a, b) => (a.ManualSlotPriority, a.SlotIndex).CompareTo((b.ManualSlotPriority, b.SlotIndex)));

        var nonResizableFixedSlots = allSlots.Where(slot => !slot.IsStretch && !slot.AllowFixedSlotResize).OrderBy(slot => slot.ManualSlotPriority).ThenBy(slot => slot.SlotIndex).ToList();
        var resizableFixedSlots = allSlots.Where(slot => !slot.IsStretch && slot.AllowFixedSlotResize).OrderBy(slot => slot.ManualSlotPriority).ThenBy(slot => slot.SlotIndex).ToList();
        var stretchSlots = allSlots.Where(slot => slot.IsStretch).OrderBy(slot => slot.ManualSlotPriority).ThenBy(slot => slot.SlotIndex).ToList();

        var allocations = new Dictionary<nint, int>();
        var allocatedWidthSum = 0;

        foreach (var slot in nonResizableFixedSlots)
        {
            var desiredWidth = (int)Math.Ceiling(slot.PreferredWidth * scaleFactor);
            var allocatedWidth = Math.Min(desiredWidth, Math.Max(0, totalAvailableWidth - allocatedWidthSum));
            allocations[slot.WindowHandle] = allocatedWidth;
            allocatedWidthSum += allocatedWidth;
        }

        var remainingForResizable = Math.Max(0, totalAvailableWidth - allocatedWidthSum);
        var resizableDesiredWidths = resizableFixedSlots.Select(slot => (int)Math.Ceiling(slot.PreferredWidth * scaleFactor)).ToList();
        var resizableDesiredWidthSum = resizableDesiredWidths.Sum();
        if (resizableFixedSlots.Count > 0)
        {
            if (remainingForResizable >= resizableDesiredWidthSum)
            {
                for (var index = 0; index < resizableFixedSlots.Count; index++)
                {
                    var allocatedWidth = resizableDesiredWidths[index];
                    allocations[resizableFixedSlots[index].WindowHandle] = allocatedWidth;
                    allocatedWidthSum += allocatedWidth;
                }
            }
            else allocatedWidthSum += AllocateProportionally(resizableFixedSlots, resizableDesiredWidths, remainingForResizable, allocations);
        }

        var remainingForStretch = Math.Max(0, totalAvailableWidth - allocatedWidthSum);
        var stretchWidth = stretchSlots.Count > 0 ? remainingForStretch / stretchSlots.Count : 0;
        foreach (var slot in stretchSlots) allocations[slot.WindowHandle] = stretchWidth;

        var offset = 0.0;
        foreach (var slot in allSlots)
        {
            if (slot.WindowHandle == ownSlot.WindowHandle) break;
            offset += allocations.GetValueOrDefault(slot.WindowHandle, 0);
        }

        return (allocations.GetValueOrDefault(ownSlot.WindowHandle, 0), offset);
    }

    private static int AllocateProportionally(List<TaskbarSlotInfo> slots, List<int> desiredWidths, int availableWidth, Dictionary<nint, int> allocations)
    {
        var desiredWidthSum = desiredWidths.Sum();
        if (desiredWidthSum <= 0)
        {
            for (var index = 0; index < slots.Count; index++) allocations[slots[index].WindowHandle] = 0;
            return 0;
        }

        var distributedWidth = 0;
        for (var index = 0; index < slots.Count; index++)
        {
            var allocatedWidth = (int)((long)availableWidth * desiredWidths[index] / desiredWidthSum);
            allocations[slots[index].WindowHandle] = allocatedWidth;
            distributedWidth += allocatedWidth;
        }

        var leftoverWidth = availableWidth - distributedWidth;
        for (var index = 0; index < slots.Count && leftoverWidth > 0; index++)
        {
            allocations[slots[index].WindowHandle]++;
            leftoverWidth--;
        }

        return availableWidth;
    }

    /// <summary>Fork 改动：通知区边界按轴向取值——横排用通知区 left，竖排用通知区 top。</summary>
    private int ResolveNotificationBoundary(RECT taskbarRectangle, TaskbarOrientation orientation)
    {
        if (options.TrackNotificationArea && TryGetWindowRectangle(taskbarWindowLocator.NotificationWindow, out var notificationRectangle))
        {
            return orientation == TaskbarOrientation.Horizontal
                ? Math.Min(taskbarRectangle.right, notificationRectangle.left)
                : Math.Min(taskbarRectangle.bottom, notificationRectangle.top);
        }

        return orientation == TaskbarOrientation.Horizontal ? taskbarRectangle.right : taskbarRectangle.bottom;
    }

    private static (int AreaStart, int AreaEnd, bool StartAlign) SelectContentArea(TaskbarContentPlacement placement, TaskbarAlignment alignment, RECT taskbarRectangle, TaskbarOrientation orientation, int notificationStart, TaskbarButtonGeometry geometry)
    {
        var startGap = ComputeStartGap(taskbarRectangle, orientation, geometry);
        var endGap = ComputeEndGap(notificationStart, geometry);
        var startAvailableWidth = Math.Max(0, startGap.End - startGap.Start);
        var endAvailableWidth = Math.Max(0, endGap.End - endGap.Start);

        var useStartGap = placement switch
        {
            TaskbarContentPlacement.LeftEdge => alignment == TaskbarAlignment.Center,
            TaskbarContentPlacement.BeforeStartButton => alignment == TaskbarAlignment.Center,
            TaskbarContentPlacement.Auto => alignment == TaskbarAlignment.Center && startAvailableWidth > endAvailableWidth,
            _ => false
        };

        if (!useStartGap) return (endGap.Start, endGap.End, false);

        var startAlign = placement is TaskbarContentPlacement.LeftEdge or TaskbarContentPlacement.Auto;
        return (startGap.Start, startGap.End, startAlign);
    }

    private static (int Start, int End) ComputeStartGap(RECT taskbarRectangle, TaskbarOrientation orientation, TaskbarButtonGeometry geometry)
    {
        var stripAxisStart = orientation == TaskbarOrientation.Horizontal ? taskbarRectangle.left : taskbarRectangle.top;
        var start = geometry.WidgetsButton.IsValid ? geometry.WidgetsButton.End : stripAxisStart;
        var end = geometry.StartButton.IsValid ? geometry.StartButton.Start : stripAxisStart;
        return (start, end);
    }

    private static (int Start, int End) ComputeEndGap(int notificationStart, TaskbarButtonGeometry geometry)
    {
        var start = geometry.TaskbarButtonsGroup.IsValid ? geometry.TaskbarButtonsGroup.End : (geometry.StartButton.IsValid ? geometry.StartButton.End : notificationStart);
        var end = notificationStart;
        if (geometry.WidgetsButton.IsValid && geometry.WidgetsButton.Start >= start && geometry.WidgetsButton.Start < end) end = geometry.WidgetsButton.Start;
        return (start, end);
    }

    private static double NormalizeScaleFactor(double scaleFactor)
    {
        if (!double.IsFinite(scaleFactor) || scaleFactor <= 0) return s_lastScaleFactor ?? 1.0;

        s_lastScaleFactor = scaleFactor;
        return scaleFactor;
    }

    private static bool TryGetWindowRectangle(HWND window, out RECT rectangle)
    {
        rectangle = default;
        return !window.IsNull && PInvoke.GetWindowRect(window, out rectangle);
    }

    /// <summary>Fork 改动：按钮搜索带按方向构造——主轴方向用任务栏起止（含开始按钮），交叉方向用 ReBar 钳制。</summary>
    private static bool TryGetTaskbarButtonsSearchRectangle(RECT taskbarRectangle, RECT rowRectangle, TaskbarOrientation orientation, int axisEndBoundary, out RECT rectangle)
    {
        rectangle = orientation == TaskbarOrientation.Horizontal
            ? new RECT
            {
                left = taskbarRectangle.left,
                top = Math.Max(taskbarRectangle.top, rowRectangle.top),
                right = axisEndBoundary,
                bottom = Math.Min(taskbarRectangle.bottom, rowRectangle.bottom)
            }
            : new RECT
            {
                left = Math.Max(taskbarRectangle.left, rowRectangle.left),
                top = taskbarRectangle.top,
                right = Math.Min(taskbarRectangle.right, rowRectangle.right),
                bottom = Math.Min(taskbarRectangle.bottom, axisEndBoundary)
            };

        return rectangle.right > rectangle.left && rectangle.bottom > rectangle.top;
    }
}
