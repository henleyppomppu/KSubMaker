using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace KSubMaker.App.Controls;

/// <summary>
/// The job grid. A plain <see cref="DataGrid"/> except for what it tells UI Automation.
///
/// <para>Measured 2026-11-02 on the user's machine (186 rows): dragging the scroll bar thumb
/// stuttered badly while the mouse wheel was fine. A sampled trace taken while jumping to random
/// scroll positions put layout itself at about 0.15% of the time and
/// <c>ContextLayoutManager.fireAutomationEvents → AutomationPeer.UpdateSubtree</c> at roughly
/// fifteen times that: after every layout pass WPF rebuilt the automation tree for every realised
/// row and every cell, because something on the system — an input-method host, a resident tool —
/// was listening for UI Automation events. A wheel tick replaces two or three rows; a thumb drag
/// replaces the whole page on every mouse move, about 0.16s each, and the moves pile up.</para>
///
/// <para>So the grid reports itself to UI Automation as a single element with no per-row or
/// per-cell children, and still exposes scrolling. A screen reader can no longer walk individual
/// cells. Nothing else changes — layout, virtualisation, templates and scrolling behaviour are
/// exactly those of <see cref="DataGrid"/>. (<see cref="DataGridAutomationPeer"/> is sealed, hence
/// a peer of our own rather than a trimmed copy of that one.)</para>
/// </summary>
public sealed class JobGrid : DataGrid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LeanPeer(this);

    private sealed class LeanPeer(JobGrid owner) : FrameworkElementAutomationPeer(owner)
    {
        private ScrollViewerAutomationPeer? _scroll;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

        protected override string GetClassNameCore() => nameof(DataGrid);

        // null means "no children": nothing per row or per cell is created, cached, or diffed after
        // each layout pass — which is the entire point of this class.
        protected override List<AutomationPeer>? GetChildrenCore() => null;

        public override object? GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.Scroll)
            {
                var viewer = FindScrollViewer((JobGrid)Owner);
                if (viewer is not null)
                {
                    _scroll ??= new ScrollViewerAutomationPeer(viewer);
                    return _scroll;
                }
            }

            return base.GetPattern(patternInterface);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer)
                {
                    return viewer;
                }

                if (FindScrollViewer(child) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
