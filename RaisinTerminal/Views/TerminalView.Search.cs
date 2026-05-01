using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.Views;

public partial class TerminalView
{
    private bool _searchActive;
    private readonly TerminalSearchState _searchState = new();
    private DispatcherTimer? _searchDebounceTimer;

    private void InitSearch()
    {
        SearchInput.PreviewKeyDown += OnSearchInputKeyDown;
        SearchInput.TextChanged += OnSearchTextChanged;
    }

    private void OpenSearch()
    {
        _searchActive = true;
        SearchOverlay.Visibility = Visibility.Visible;

        if (Canvas.SelectionStart != null && Canvas.SelectionEnd != null)
        {
            var text = Canvas.GetSelectedText();
            if (!string.IsNullOrEmpty(text) && !text.Contains('\n'))
                SearchInput.Text = text;
        }

        SearchInput.Focus();
        SearchInput.SelectAll();

        if (!string.IsNullOrEmpty(SearchInput.Text))
            ExecuteSearch();
    }

    private void CloseSearch()
    {
        _searchActive = false;
        _searchDebounceTimer?.Stop();
        SearchOverlay.Visibility = Visibility.Collapsed;
        _searchState.Clear();
        Canvas.SearchMatches = null;
        Canvas.CurrentSearchMatch = null;
        Canvas.Invalidate();
        PinnedCanvas.SearchMatches = null;
        PinnedCanvas.CurrentSearchMatch = null;
        if (_isSplit) PinnedCanvas.Invalidate();
        if (_overlayActive)
            Dispatcher.BeginInvoke(() => OverlayInput.Focus(), DispatcherPriority.Input);
        else
            Dispatcher.BeginInvoke(() => Canvas.Focus(), DispatcherPriority.Input);
    }

    private void ExecuteSearch()
    {
        var query = SearchInput.Text;
        if (string.IsNullOrEmpty(query))
        {
            _searchState.Clear();
            UpdateSearchIndicator();
            Canvas.SearchMatches = null;
            Canvas.CurrentSearchMatch = null;
            Canvas.Invalidate();
            return;
        }

        _searchState.Query = query;
        _searchState.Matches.Clear();
        _searchState.Matches.AddRange(_vm!.SearchBuffer(query));

        if (_searchState.MatchCount > 0)
        {
            var buffer = _vm!.Emulator?.Buffer;
            if (buffer != null)
            {
                long viewTop = buffer.TotalLinesScrolled - _viewport.ScrollOffset;
                int idx = _searchState.Matches.FindIndex(m => m.AbsoluteRow >= viewTop);
                _searchState.CurrentMatchIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                _searchState.CurrentMatchIndex = 0;
            }
        }
        else
        {
            _searchState.CurrentMatchIndex = -1;
        }

        UpdateSearchIndicator();
        Canvas.SearchMatches = _searchState.Matches;
        Canvas.CurrentSearchMatch = _searchState.CurrentMatch;
        Canvas.Invalidate();
        if (_isSplit)
        {
            PinnedCanvas.SearchMatches = _searchState.Matches;
            PinnedCanvas.CurrentSearchMatch = _searchState.CurrentMatch;
            PinnedCanvas.Invalidate();
        }
    }

    private void UpdateSearchIndicator()
    {
        if (_searchState.MatchCount == 0)
            SearchResultsIndicator.Text = string.IsNullOrEmpty(_searchState.Query) ? "" : "0/0";
        else
            SearchResultsIndicator.Text = $"{_searchState.CurrentMatchIndex + 1}/{_searchState.MatchCount}";
    }

    private void NavigateSearchNext()
    {
        if (_searchState.MatchCount == 0) return;
        _searchState.CurrentMatchIndex = (_searchState.CurrentMatchIndex + 1) % _searchState.MatchCount;
        ScrollToCurrentMatch();
        UpdateSearchIndicator();
        Canvas.CurrentSearchMatch = _searchState.CurrentMatch;
        Canvas.Invalidate();
        if (_isSplit)
        {
            PinnedCanvas.CurrentSearchMatch = _searchState.CurrentMatch;
            PinnedCanvas.Invalidate();
        }
    }

    private void NavigateSearchPrevious()
    {
        if (_searchState.MatchCount == 0) return;
        _searchState.CurrentMatchIndex = (_searchState.CurrentMatchIndex - 1 + _searchState.MatchCount) % _searchState.MatchCount;
        ScrollToCurrentMatch();
        UpdateSearchIndicator();
        Canvas.CurrentSearchMatch = _searchState.CurrentMatch;
        Canvas.Invalidate();
        if (_isSplit)
        {
            PinnedCanvas.CurrentSearchMatch = _searchState.CurrentMatch;
            PinnedCanvas.Invalidate();
        }
    }

    private void ScrollToCurrentMatch()
    {
        var match = _searchState.CurrentMatch;
        if (match == null || _vm?.Emulator?.Buffer == null) return;

        var buffer = _vm.Emulator.Buffer;
        long matchAbsRow = match.Value.AbsoluteRow;

        long viewTop = buffer.TotalLinesScrolled - _viewport.ScrollOffset;
        long viewBottom = viewTop + buffer.Rows - 1;

        if (matchAbsRow >= viewTop && matchAbsRow <= viewBottom)
            return;

        int targetOffset = (int)(buffer.TotalLinesScrolled - matchAbsRow + buffer.Rows / 2);
        _viewport.ScrollOffset = Math.Clamp(targetOffset, 0, buffer.ScrollbackCount);
        _viewport.UserScrolledBack = _viewport.ScrollOffset > 0;
        UpdateScrollBar();
    }

    private void OnSearchInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift) NavigateSearchPrevious();
            else NavigateSearchNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            NavigateSearchPrevious();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            NavigateSearchNext();
            e.Handled = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchDebounceTimer.Tick += OnSearchDebounce;
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounce(object? sender, EventArgs e)
    {
        _searchDebounceTimer!.Stop();
        ExecuteSearch();
    }

    private void OnSearchUp(object sender, RoutedEventArgs e) => NavigateSearchPrevious();
    private void OnSearchDown(object sender, RoutedEventArgs e) => NavigateSearchNext();
    private void OnSearchClose(object sender, RoutedEventArgs e) => CloseSearch();
}
