using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace QuickPreview;

internal sealed record ExplorerGridSnapshot(
    int FocusedIndex,
    int Rows,
    int Columns,
    int FocusedRow,
    int FocusedColumn,
    int? TargetIndex,
    string SourceName,
    string? TargetName,
    bool OffscreenStrideSafe = true,
    bool IndicesAreGlobal = true,
    string? SourceGroupName = null);

/// <summary>
/// Reads the active Explorer view's exposed grid layout. Selection is committed
/// separately through the native Shell API; UI Automation is never used for input.
/// </summary>
internal static class ExplorerGridLayout
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1000);
    private static int _probeInFlight;

    internal static ExplorerGridSnapshot? Read(IntPtr shellViewWindow, int focusedIndex, NavigationDirection direction,
        string? expectedSourceName = null)
    {
        if (focusedIndex < 0 || direction is not (NavigationDirection.Left or NavigationDirection.Up or
                NavigationDirection.Right or NavigationDirection.Down) ||
            !TryGetExternalWindowProcess(shellViewWindow, out var processId))
            return null;

        // A provider call cannot be forcibly cancelled. If one hangs, retain the
        // gate until it actually finishes rather than accumulating blocked tasks.
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
            return null;

        var elapsed = Stopwatch.StartNew();
        Task<ExplorerGridSnapshot?> probe;
        try
        {
            probe = Task.Run(() =>
            {
                try
                {
                    // The thread pool is MTA. Keep every UIA object on this worker
                    // and return only a small immutable data snapshot to the caller.
                    return ReadCore(shellViewWindow, processId, focusedIndex, direction, expectedSourceName, elapsed);
                }
                catch (Exception exception)
                {
                    // Stale elements, unsupported patterns, COM/provider failures
                    // and unavailable accessibility providers all fail closed.
                    Debug.WriteLine($"QuickPreview Explorer grid read: {exception.GetType().Name}");
                    return null;
                }
                finally { Volatile.Write(ref _probeInFlight, 0); }
            });
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _probeInFlight, 0);
            Debug.WriteLine($"QuickPreview Explorer grid worker: {exception.GetType().Name}");
            return null;
        }

        try
        {
            var remaining = ProbeTimeout - elapsed.Elapsed;
            return remaining > TimeSpan.Zero && probe.Wait(remaining) ? probe.GetAwaiter().GetResult() : null;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"QuickPreview Explorer grid wait: {exception.GetType().Name}");
            return null;
        }
    }

    private static ExplorerGridSnapshot? ReadCore(IntPtr shellViewWindow, uint processId, int focusedIndex,
        NavigationDirection direction, string? expectedSourceName, Stopwatch elapsed)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA || Expired() ||
            !TryGetExternalWindowProcess(shellViewWindow, out var currentProcess) || currentProcess != processId)
            return null;

        var root = AutomationElement.FromHandle(shellViewWindow);
        if (root is null || Expired()) return null;
        var rootInfo = root.Current;
        if (rootInfo.ProcessId != processId || rootInfo.ProcessId == Environment.ProcessId ||
            rootInfo.NativeWindowHandle != unchecked((int)shellViewWindow.ToInt64()))
            return null;

        var selectedCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true),
            new PropertyCondition(AutomationElement.ProcessIdProperty, (int)processId));
        var selected = FindCached(root, selectedCondition);
        AutomationElement? source = null;
        var sourceMatches = 0;
        for (var index = 0; index < Math.Min(selected.Count, 256); index++)
        {
            if (Expired()) return null;
            var candidate = selected[index];
            var name = candidate.Cached.Name;
            if (!string.IsNullOrWhiteSpace(expectedSourceName) &&
                !string.Equals(name, expectedSourceName, StringComparison.OrdinalIgnoreCase)) continue;
            source = candidate;
            sourceMatches++;
        }
        // Grouped Explorer resets AutomationId within each group. A unique selected
        // name, validated against the native focused item by the caller, is the anchor.
        if (sourceMatches != 1 || selected.Count > 256) return null;
        if (source is null || Expired()) return null;
        var sourceInfo = source.Cached;
        if (sourceInfo.ControlType != ControlType.ListItem || sourceInfo.ProcessId != processId ||
            string.IsNullOrWhiteSpace(sourceInfo.Name) ||
            !source.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject) ||
            selectionObject is not SelectionItemPattern selection || !selection.Current.IsSelected || Expired())
            return null;
        var hasSourceIndex = TryIndex(sourceInfo.AutomationId, out var actualIndex);

        if (!source.TryGetCurrentPattern(GridItemPattern.Pattern, out var itemObject) ||
            itemObject is not GridItemPattern item || Expired()) return null;
        var cell = item.Current;
        var gridElement = cell.ContainingGrid;
        if (Expired()) return null;
        if (gridElement is null)
            return ReadVisibleFallback(root, processId, focusedIndex, direction, sourceInfo.Name,
                knownGrouped: false, sourceGroupName: null, elapsed);
        var gridInfo = gridElement.Current;
        var grouped = gridInfo.ControlType == ControlType.Group;
        var sourceGroupName = grouped ? gridInfo.Name : null;
        if (gridInfo.ProcessId != processId ||
            !gridElement.TryGetCurrentPattern(GridPattern.Pattern, out var gridObject) ||
            gridObject is not GridPattern grid || Expired())
            return ReadVisibleFallback(root, processId, focusedIndex, direction, sourceInfo.Name,
                grouped, sourceGroupName, elapsed);
        var dimensions = grid.Current;
        if (dimensions.RowCount <= 0 || dimensions.ColumnCount <= 0 ||
            cell.Row < 0 || cell.Row >= dimensions.RowCount || cell.Column < 0 || cell.Column >= dimensions.ColumnCount)
            return null;

        var targetRow = cell.Row;
        var targetColumn = cell.Column;
        switch (direction)
        {
            case NavigationDirection.Left: targetColumn--; break;
            case NavigationDirection.Up: targetRow--; break;
            case NavigationDirection.Right: targetColumn++; break;
            case NavigationDirection.Down: targetRow++; break;
        }

        int? targetIndex = null;
        string? targetName = null;
        if (targetRow >= 0 && targetRow < dimensions.RowCount && targetColumn >= 0 && targetColumn < dimensions.ColumnCount && !Expired())
        {
            try
            {
                // GetItem is a query only. Do not Realize/ScrollIntoView/Select or
                // SetFocus: those actions can activate Explorer or move its view.
                var target = grid.GetItem(targetRow, targetColumn);
                if (target is not null && !Expired())
                {
                    var targetInfo = target.Current;
                    if (targetInfo.ControlType == ControlType.ListItem && targetInfo.ProcessId == processId &&
                        TryIndex(targetInfo.AutomationId, out var index) && (!hasSourceIndex || index != actualIndex) &&
                        !string.IsNullOrWhiteSpace(targetInfo.Name) &&
                        target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var targetSelection) &&
                        targetSelection is SelectionItemPattern &&
                        target.TryGetCurrentPattern(GridItemPattern.Pattern, out var targetCellObject) &&
                        targetCellObject is GridItemPattern targetCell && !Expired())
                    {
                        var targetPosition = targetCell.Current;
                        if (targetPosition.Row == targetRow && targetPosition.Column == targetColumn &&
                            Automation.Compare(targetPosition.ContainingGrid, gridElement))
                        {
                            targetIndex = index;
                            targetName = targetInfo.Name;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or
                ArgumentException or COMException or NotSupportedException)
            {
                // Virtualized/unrealized cells can throw instead of returning an
                // element. Preserve the credible lane dimensions for native fallback.
            }
        }

        if (Expired() || !TryGetExternalWindowProcess(shellViewWindow, out currentProcess) || currentProcess != processId)
            return null;
        if (grouped && targetIndex is null)
        {
            // At a group boundary the next visible row can belong to another grid.
            var adjacentGroup = ReadVisibleFallback(root, processId, focusedIndex, direction, sourceInfo.Name,
                knownGrouped: true, sourceGroupName, elapsed);
            if (adjacentGroup?.TargetIndex is not null) return adjacentGroup;
        }
        var nativeIndices = !grouped && hasSourceIndex && actualIndex == focusedIndex;
        return new ExplorerGridSnapshot(focusedIndex, dimensions.RowCount, dimensions.ColumnCount,
            cell.Row, cell.Column, targetIndex, sourceInfo.Name, targetName,
            OffscreenStrideSafe: nativeIndices, IndicesAreGlobal: nativeIndices, SourceGroupName: sourceGroupName);

        bool Expired() => elapsed.Elapsed >= ProbeTimeout;
    }

    private static AutomationElementCollection FindCached(AutomationElement root, System.Windows.Automation.Condition condition)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ProcessIdProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        cache.Add(GridItemPattern.RowProperty);
        cache.Add(GridItemPattern.ColumnProperty);
        using (cache.Activate()) return root.FindAll(TreeScope.Descendants, condition);
    }

    private static ExplorerGridSnapshot? ReadVisibleFallback(AutomationElement root, uint processId, int focusedIndex,
        NavigationDirection direction, string sourceName, bool knownGrouped, string? sourceGroupName, Stopwatch elapsed)
    {
        if (elapsed.Elapsed >= ProbeTimeout) return null;
        var elements = FindCached(root, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ProcessIdProperty, (int)processId)));
        if (elapsed.Elapsed >= ProbeTimeout || elements.Count > 4096) return null;
        var cells = new List<VisibleCell>();
        for (var index = 0; index < elements.Count; index++)
        {
            if (elapsed.Elapsed >= ProbeTimeout) return null;
            var element = elements[index];
            var info = element.Cached;
            if (info.ControlType != ControlType.ListItem || info.ProcessId != processId ||
                !TryIndex(info.AutomationId, out var itemIndex) || string.IsNullOrWhiteSpace(info.Name) ||
                element.GetCachedPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty, true) is not true ||
                element.GetCachedPropertyValue(GridItemPattern.RowProperty, true) is not int row || row < 0 || row == int.MaxValue ||
                element.GetCachedPropertyValue(GridItemPattern.ColumnProperty, true) is not int column || column < 0 || column == int.MaxValue)
                continue;
            var isSelected = element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true;
            cells.Add(new VisibleCell(itemIndex, row, column, info.Name, info.BoundingRectangle, isSelected));
        }
        var sources = cells.Where(cell => cell.IsSelected &&
            string.Equals(cell.Name, sourceName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (sources.Length != 1) return null;
        var source = sources[0];
        var duplicateIds = cells.Select(cell => cell.Index).Distinct().Count() != cells.Count;
        var duplicateCoordinates = cells.Select(cell => (cell.Row, cell.Column)).Distinct().Count() != cells.Count;
        var ambiguous = knownGrouped || duplicateIds || duplicateCoordinates;
        var targetRow = source.Row + (direction == NavigationDirection.Up ? -1 : direction == NavigationDirection.Down ? 1 : 0);
        var targetColumn = source.Column + (direction == NavigationDirection.Left ? -1 : direction == NavigationDirection.Right ? 1 : 0);
        VisibleCell? target = null;
        if (!ambiguous)
        {
            target = cells.FirstOrDefault(cell => cell.Row == targetRow && cell.Column == targetColumn &&
                IsInDirection(source.Bounds, cell.Bounds, direction));
        }
        // Repeated group-local row/column coordinates cannot identify another file.
        // Use only visible physical neighbors, never UIA selection/scroll/realization.
        target ??= FindSpatialNeighbor(cells, source, direction);
        var globalIndices = !ambiguous && source.Index == focusedIndex;
        return new ExplorerGridSnapshot(focusedIndex, cells.Max(cell => cell.Row) + 1, cells.Max(cell => cell.Column) + 1,
            source.Row, source.Column, target?.Index, source.Name, target?.Name,
            OffscreenStrideSafe: globalIndices, IndicesAreGlobal: globalIndices, SourceGroupName: sourceGroupName);
    }

    private static VisibleCell? FindSpatialNeighbor(IReadOnlyList<VisibleCell> cells, VisibleCell source, NavigationDirection direction)
    {
        if (!UsableBounds(source.Bounds)) return null;
        var horizontal = direction is NavigationDirection.Left or NavigationDirection.Right;
        var candidates = cells.Where(cell => !ReferenceEquals(cell, source) &&
            IsInDirection(source.Bounds, cell.Bounds, direction)).ToArray();
        if (horizontal)
        {
            return candidates.Where(cell => Math.Abs(CenterY(cell.Bounds) - CenterY(source.Bounds)) <=
                    Math.Max(4, Math.Min(source.Bounds.Height, cell.Bounds.Height) * 0.4))
                .OrderBy(cell => Math.Abs(CenterX(cell.Bounds) - CenterX(source.Bounds)))
                .ThenBy(cell => Math.Abs(CenterY(cell.Bounds) - CenterY(source.Bounds)))
                .FirstOrDefault();
        }
        if (candidates.Length == 0) return null;
        var nearestRow = candidates.Min(cell => Math.Abs(CenterY(cell.Bounds) - CenterY(source.Bounds)));
        return candidates.Where(cell => Math.Abs(CenterY(cell.Bounds) - CenterY(source.Bounds)) <= nearestRow +
                Math.Max(4, Math.Min(source.Bounds.Height, cell.Bounds.Height) * 0.35))
            .OrderBy(cell => Math.Abs(CenterX(cell.Bounds) - CenterX(source.Bounds)))
            .ThenBy(cell => Math.Abs(CenterY(cell.Bounds) - CenterY(source.Bounds)))
            .FirstOrDefault();
    }

    private static bool IsInDirection(System.Windows.Rect source, System.Windows.Rect target, NavigationDirection direction)
    {
        if (!UsableBounds(source) || !UsableBounds(target)) return false;
        var dx = CenterX(target) - CenterX(source);
        var dy = CenterY(target) - CenterY(source);
        return direction switch
        {
            NavigationDirection.Left => dx < -Math.Max(1, Math.Min(source.Width, target.Width) * 0.1),
            NavigationDirection.Right => dx > Math.Max(1, Math.Min(source.Width, target.Width) * 0.1),
            NavigationDirection.Up => dy < -Math.Max(4, Math.Min(source.Height, target.Height) * 0.4),
            NavigationDirection.Down => dy > Math.Max(4, Math.Min(source.Height, target.Height) * 0.4),
            _ => false
        };
    }

    private static bool UsableBounds(System.Windows.Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
    private static double CenterX(System.Windows.Rect bounds) => bounds.X + bounds.Width / 2;
    private static double CenterY(System.Windows.Rect bounds) => bounds.Y + bounds.Height / 2;
    private sealed record VisibleCell(int Index, int Row, int Column, string Name, System.Windows.Rect Bounds, bool IsSelected);

    private static bool TryIndex(string? value, out int index) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;

    private static bool TryGetExternalWindowProcess(IntPtr window, out uint processId)
    {
        processId = 0;
        return window != IntPtr.Zero && IsWindow(window) &&
               GetWindowThreadProcessId(window, out processId) != 0 && processId != 0 && processId != Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
