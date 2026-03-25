using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public partial class ProjectsPanelView : UserControl
{
    private const int MaxCollapsedAttachments = 5;
    private readonly HashSet<string> _expandedAttachmentFolders = new();
    private Point _dragStartPoint;
    private bool _dragReady;

    public ProjectsPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ProjectsTree.PreviewMouseLeftButtonDown += OnTreePreviewMouseDown;
        ProjectsTree.PreviewMouseMove += OnTreePreviewMouseMove;
        ProjectsTree.PreviewMouseLeftButtonUp += (_, _) => _dragReady = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProjectsPanelViewModel oldVm)
        {
            oldVm.Projects.CollectionChanged -= OnProjectsChanged;
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is ProjectsPanelViewModel vm)
        {
            vm.Projects.CollectionChanged += OnProjectsChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            RebuildTreeItems(vm);
        }
    }

    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is ProjectsPanelViewModel vm)
            RebuildTreeItems(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectsPanelViewModel.UngroupedNode) && DataContext is ProjectsPanelViewModel vm)
            RebuildTreeItems(vm);
    }

    private void RebuildTreeItems(ProjectsPanelViewModel vm)
    {
        ProjectsTree.Items.Clear();

        foreach (var project in vm.Projects)
        {
            var item = CreateProjectItem(project);
            ProjectsTree.Items.Add(item);
        }

        if (vm.UngroupedNode is { } ungrouped && ungrouped.Terminals.Count > 0)
        {
            var item = CreateProjectItem(ungrouped);
            ProjectsTree.Items.Add(item);
        }
    }

    private TreeViewItem CreateProjectItem(ProjectNodeViewModel project)
    {
        var item = new TreeViewItem
        {
            DataContext = project,
            HeaderTemplate = (DataTemplate)Resources["ProjectNodeTemplate"],
            Header = project,
            IsExpanded = project.IsExpanded,
            Style = (Style)Resources["TreeViewItemStyle"]
        };

        // Add terminal children
        foreach (var terminal in project.Terminals)
        {
            item.Items.Add(CreateTerminalItem(terminal));
        }

        // Add attachments folder if project has attachments
        var attachmentsItem = CreateAttachmentsFolderItem(project);
        if (attachmentsItem != null)
            item.Items.Add(attachmentsItem);

        // Subscribe to terminal changes
        project.Terminals.CollectionChanged += (_, _) => RebuildProjectChildren(item, project);

        // Subscribe to attachment changes
        project.Attachments.CollectionChanged += (_, _) => RebuildProjectChildren(item, project);

        return item;
    }

    private void RebuildProjectChildren(TreeViewItem item, ProjectNodeViewModel project)
    {
        item.Items.Clear();

        foreach (var t in project.Terminals)
        {
            item.Items.Add(CreateTerminalItem(t));
        }

        var attachmentsItem = CreateAttachmentsFolderItem(project);
        if (attachmentsItem != null)
            item.Items.Add(attachmentsItem);
    }

    private TreeViewItem CreateTerminalItem(TerminalNodeViewModel terminal)
    {
        return new TreeViewItem
        {
            DataContext = terminal,
            HeaderTemplate = (DataTemplate)Resources["TerminalNodeTemplate"],
            Header = terminal,
            Style = (Style)Resources["TreeViewItemStyle"]
        };
    }

    private TreeViewItem? CreateAttachmentsFolderItem(ProjectNodeViewModel project)
    {
        if (project.Attachments.Count == 0)
            return null;

        var folderItem = new TreeViewItem
        {
            DataContext = project,
            HeaderTemplate = (DataTemplate)Resources["AttachmentsFolderTemplate"],
            Header = project.Attachments,
            IsExpanded = false,
            Style = (Style)Resources["TreeViewItemStyle"]
        };

        folderItem.MouseDoubleClick += OnAttachmentsFolderDoubleClick;

        var projectId = project.Project.Id;
        var isExpanded = _expandedAttachmentFolders.Contains(projectId);
        var totalCount = project.Attachments.Count;
        var items = isExpanded ? project.Attachments : project.Attachments.Take(MaxCollapsedAttachments);

        foreach (var attachment in items)
        {
            folderItem.Items.Add(CreateAttachmentItem(attachment));
        }

        if (totalCount > MaxCollapsedAttachments)
        {
            if (!isExpanded)
            {
                var moreItem = CreateToggleItem($"more… ({totalCount - MaxCollapsedAttachments})");
                moreItem.MouseLeftButtonUp += (_, e) =>
                {
                    _expandedAttachmentFolders.Add(projectId);
                    RebuildAttachmentsFolderChildren(folderItem, project);
                    e.Handled = true;
                };
                folderItem.Items.Add(moreItem);
            }
            else
            {
                var closeItem = CreateToggleItem("close");
                closeItem.MouseLeftButtonUp += (_, e) =>
                {
                    _expandedAttachmentFolders.Remove(projectId);
                    RebuildAttachmentsFolderChildren(folderItem, project);
                    e.Handled = true;
                };
                folderItem.Items.Add(closeItem);
            }
        }

        return folderItem;
    }

    private void RebuildAttachmentsFolderChildren(TreeViewItem folderItem, ProjectNodeViewModel project)
    {
        folderItem.Items.Clear();

        var projectId = project.Project.Id;
        var isExpanded = _expandedAttachmentFolders.Contains(projectId);
        var totalCount = project.Attachments.Count;
        var items = isExpanded ? project.Attachments : project.Attachments.Take(MaxCollapsedAttachments);

        foreach (var attachment in items)
        {
            folderItem.Items.Add(CreateAttachmentItem(attachment));
        }

        if (totalCount > MaxCollapsedAttachments)
        {
            if (!isExpanded)
            {
                var moreItem = CreateToggleItem($"more… ({totalCount - MaxCollapsedAttachments})");
                moreItem.MouseLeftButtonUp += (_, e) =>
                {
                    _expandedAttachmentFolders.Add(projectId);
                    RebuildAttachmentsFolderChildren(folderItem, project);
                    e.Handled = true;
                };
                folderItem.Items.Add(moreItem);
            }
            else
            {
                var closeItem = CreateToggleItem("close");
                closeItem.MouseLeftButtonUp += (_, e) =>
                {
                    _expandedAttachmentFolders.Remove(projectId);
                    RebuildAttachmentsFolderChildren(folderItem, project);
                    e.Handled = true;
                };
                folderItem.Items.Add(closeItem);
            }
        }

        folderItem.IsExpanded = true;
    }

    private static TreeViewItem CreateToggleItem(string text)
    {
        return new TreeViewItem
        {
            Header = new TextBlock
            {
                Text = text,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
            },
            Focusable = false
        };
    }

    private void OnAttachmentsFolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only handle if the double-click is on the folder header, not a child attachment item
        if (sender is not TreeViewItem folderItem)
            return;

        // Check that the click originated on the folder itself, not a child item
        var source = e.OriginalSource as DependencyObject;
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == folderItem)
                break;
            // If we hit a child TreeViewItem first, the click was on an attachment item
            if (current is TreeViewItem child && child != folderItem)
                return;
        }

        if (folderItem.DataContext is ProjectNodeViewModel project && DataContext is ProjectsPanelViewModel vm)
        {
            vm.OpenAttachmentsFolderCommand.Execute(project);
            e.Handled = true;
        }
    }

    private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not AttachmentItemViewModel) return;

        _dragStartPoint = e.GetPosition(null);
        _dragReady = true;
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragReady || e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not AttachmentItemViewModel attachment) return;

        _dragReady = false;
        var data = new DataObject(DataFormats.FileDrop, new[] { attachment.FilePath });
        DragDrop.DoDragDrop(ProjectsTree, data, DragDropEffects.Copy);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnTreeMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Walk up the visual tree from the clicked element to find a TreeViewItem with a TerminalNodeViewModel
        if (e.OriginalSource is not DependencyObject source)
            return;

        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TreeViewItem { DataContext: TerminalNodeViewModel terminal })
            {
                if (DataContext is ProjectsPanelViewModel vm)
                    vm.ActivateTerminalCommand.Execute(terminal);
                return;
            }
        }
    }

    private TreeViewItem CreateAttachmentItem(AttachmentItemViewModel attachment)
    {
        return new TreeViewItem
        {
            DataContext = attachment,
            HeaderTemplate = (DataTemplate)Resources["AttachmentItemTemplate"],
            Header = attachment,
            Style = (Style)Resources["TreeViewItemStyle"]
        };
    }
}
