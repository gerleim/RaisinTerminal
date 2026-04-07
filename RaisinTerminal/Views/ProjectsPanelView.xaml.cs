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
    private const string ProjectDragFormat = "RaisinTerminal.ProjectNode";
    private readonly HashSet<string> _expandedAttachmentFolders = new();
    private Point _dragStartPoint;
    private bool _dragReady;
    private object? _dragSource; // The DataContext of the item being dragged
    private TreeViewItem? _dragSourceItem;
    private TreeViewItem? _dropHighlightItem;

    public ProjectsPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ProjectsTree.PreviewMouseLeftButtonDown += OnTreePreviewMouseDown;
        ProjectsTree.PreviewMouseMove += OnTreePreviewMouseMove;
        ProjectsTree.PreviewMouseLeftButtonUp += (_, _) => { _dragReady = false; _dragSource = null; _dragSourceItem = null; };
        ProjectsTree.AllowDrop = true;
        ProjectsTree.DragOver += OnTreeDragOver;
        ProjectsTree.Drop += OnTreeDrop;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProjectsPanelViewModel oldVm)
        {
            oldVm.Projects.CollectionChanged -= OnCollectionChanged;
            oldVm.Groups.CollectionChanged -= OnCollectionChanged;
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is ProjectsPanelViewModel vm)
        {
            vm.Projects.CollectionChanged += OnCollectionChanged;
            vm.Groups.CollectionChanged += OnCollectionChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            RebuildTreeItems(vm);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

        // Groups first
        foreach (var group in vm.Groups)
        {
            var item = CreateGroupItem(group, vm);
            ProjectsTree.Items.Add(item);
        }

        // Ungrouped projects at root level
        foreach (var project in vm.Projects)
        {
            var item = CreateProjectItem(project, vm);
            ProjectsTree.Items.Add(item);
        }

        // Virtual "Ungrouped" node for unmatched sessions
        if (vm.UngroupedNode is { } ungrouped && ungrouped.Terminals.Count > 0)
        {
            var item = CreateProjectItem(ungrouped, vm);
            ProjectsTree.Items.Add(item);
        }
    }

    private TreeViewItem CreateGroupItem(ProjectGroupNodeViewModel group, ProjectsPanelViewModel vm)
    {
        var item = new TreeViewItem
        {
            DataContext = group,
            HeaderTemplate = (DataTemplate)Resources["ProjectGroupNodeTemplate"],
            Header = group,
            IsExpanded = group.IsExpanded,
            Style = (Style)Resources["TreeViewItemStyle"],
            ContextMenu = CreateGroupContextMenu(group, vm),
            AllowDrop = true
        };

        item.DragOver += OnGroupDragOver;
        item.DragLeave += OnGroupDragLeave;
        item.Drop += (_, e) => OnGroupDrop(e, group, vm);

        foreach (var project in group.Projects)
        {
            item.Items.Add(CreateProjectItem(project, vm));
        }

        group.Projects.CollectionChanged += (_, _) => RebuildGroupChildren(item, group, vm);

        return item;
    }

    private void RebuildGroupChildren(TreeViewItem item, ProjectGroupNodeViewModel group, ProjectsPanelViewModel vm)
    {
        item.Items.Clear();
        foreach (var project in group.Projects)
        {
            item.Items.Add(CreateProjectItem(project, vm));
        }
    }

    private ContextMenu CreateGroupContextMenu(ProjectGroupNodeViewModel group, ProjectsPanelViewModel vm)
    {
        var menu = new ContextMenu();

        var addProject = new MenuItem { Header = "Add Project to Group" };
        addProject.Click += (_, _) => vm.AddProjectToGroupCommand_Execute(group);
        menu.Items.Add(addProject);

        menu.Items.Add(new Separator());

        var newTerminal = new MenuItem { Header = "Rename Group" };
        newTerminal.Click += (_, _) => vm.RenameGroupCommand.Execute(group);
        menu.Items.Add(newTerminal);

        menu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Remove Group" };
        remove.Click += (_, _) => vm.RemoveGroupCommand.Execute(group);
        menu.Items.Add(remove);

        return menu;
    }

    private TreeViewItem CreateProjectItem(ProjectNodeViewModel project, ProjectsPanelViewModel vm)
    {
        var item = new TreeViewItem
        {
            DataContext = project,
            HeaderTemplate = (DataTemplate)Resources["ProjectNodeTemplate"],
            Header = project,
            IsExpanded = project.IsExpanded,
            Style = (Style)Resources["TreeViewItemStyle"]
        };

        // Build context menu with dynamic "Move to Group" submenu
        // Skip for the virtual "Ungrouped" node (empty HomePath)
        if (!string.IsNullOrEmpty(project.HomePath))
        {
            item.ContextMenu = CreateProjectContextMenu(project, vm);
        }

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
        project.Terminals.CollectionChanged += (_, _) => RebuildProjectChildren(item, project, vm);

        // Subscribe to attachment changes
        project.Attachments.CollectionChanged += (_, _) => RebuildProjectChildren(item, project, vm);

        return item;
    }

    private ContextMenu CreateProjectContextMenu(ProjectNodeViewModel project, ProjectsPanelViewModel vm)
    {
        var menu = new ContextMenu();

        var newTerminal = new MenuItem { Header = "New Terminal Here" };
        newTerminal.Click += (_, _) => vm.NewTerminalCommand.Execute(project);
        menu.Items.Add(newTerminal);

        var newClaude = new MenuItem { Header = "New Claude Terminal Here" };
        newClaude.Click += (_, _) => vm.NewClaudeTerminalCommand.Execute(project);
        menu.Items.Add(newClaude);

        if (project.HasSlnx)
        {
            var openSlnx = new MenuItem { Header = "Open slnx" };
            openSlnx.Click += (_, _) => vm.OpenSlnxCommand.Execute(project);
            menu.Items.Add(openSlnx);
        }

        menu.Items.Add(new Separator());

        // "Move to Group" submenu
        var groups = vm.GetGroups();
        if (groups.Count > 0)
        {
            var moveToGroup = new MenuItem { Header = "Move to Group" };

            foreach (var group in groups)
            {
                var groupItem = new MenuItem { Header = group.Name };
                if (project.Project.GroupId == group.Id)
                    groupItem.IsChecked = true;
                var capturedGroupId = group.Id;
                groupItem.Click += (_, _) =>
                    vm.MoveProjectToGroupCommand.Execute(new object[] { project, capturedGroupId });
                moveToGroup.Items.Add(groupItem);
            }

            moveToGroup.Items.Add(new Separator());

            var noneItem = new MenuItem { Header = "None (root level)" };
            if (project.Project.GroupId == null)
                noneItem.IsChecked = true;
            noneItem.Click += (_, _) =>
                vm.MoveProjectToGroupCommand.Execute(new object[] { project, null! });
            moveToGroup.Items.Add(noneItem);

            menu.Items.Add(moveToGroup);
            menu.Items.Add(new Separator());
        }

        var remove = new MenuItem { Header = "Remove Project" };
        remove.Click += (_, _) => vm.RemoveProjectCommand.Execute(project);
        menu.Items.Add(remove);

        menu.Items.Add(new Separator());

        var properties = new MenuItem { Header = "Properties" };
        properties.Click += (_, _) => vm.ProjectPropertiesCommand.Execute(project);
        menu.Items.Add(properties);

        return menu;
    }

    private void RebuildProjectChildren(TreeViewItem item, ProjectNodeViewModel project, ProjectsPanelViewModel vm)
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
                var moreItem = CreateToggleItem($"more\u2026 ({totalCount - MaxCollapsedAttachments})");
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
                var moreItem = CreateToggleItem($"more\u2026 ({totalCount - MaxCollapsedAttachments})");
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
        if (item == null) return;

        if (item.DataContext is AttachmentItemViewModel)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragSource = item.DataContext;
            _dragSourceItem = item;
            _dragReady = true;
        }
        else if (item.DataContext is ProjectNodeViewModel pn && !string.IsNullOrEmpty(pn.HomePath))
        {
            _dragStartPoint = e.GetPosition(null);
            _dragSource = pn;
            _dragSourceItem = item;
            _dragReady = true;
        }
    }

    private void OnTreePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragReady || e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragReady = false;

        if (_dragSource is AttachmentItemViewModel attachment)
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { attachment.FilePath });
            DragDrop.DoDragDrop(ProjectsTree, data, DragDropEffects.Copy);
        }
        else if (_dragSource is ProjectNodeViewModel project)
        {
            var data = new DataObject(ProjectDragFormat, project);
            DragDrop.DoDragDrop(_dragSourceItem!, data, DragDropEffects.Move);
            ClearDropHighlight();
        }

        _dragSource = null;
        _dragSourceItem = null;
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ProjectDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Don't allow dropping a project onto the group it's already in
        if (sender is TreeViewItem groupItem
            && groupItem.DataContext is ProjectGroupNodeViewModel group
            && e.Data.GetData(ProjectDragFormat) is ProjectNodeViewModel project
            && project.Project.GroupId == group.Group.Id)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Visual highlight
        if (sender is TreeViewItem item)
            SetDropHighlight(item);
    }

    private void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem item && _dropHighlightItem == item)
            ClearDropHighlight();
    }

    private void OnGroupDrop(DragEventArgs e, ProjectGroupNodeViewModel group, ProjectsPanelViewModel vm)
    {
        ClearDropHighlight();
        if (!e.Data.GetDataPresent(ProjectDragFormat)) return;
        if (e.Data.GetData(ProjectDragFormat) is not ProjectNodeViewModel project) return;
        if (project.Project.GroupId == group.Group.Id) return;

        vm.MoveProjectToGroupCommand.Execute(new object[] { project, group.Group.Id });
        e.Handled = true;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ProjectDragFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Check if hovering over a group item — let the group handle it
        var hit = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (hit?.DataContext is ProjectGroupNodeViewModel)
            return;

        // Only allow drop to root if currently in a group
        if (e.Data.GetData(ProjectDragFormat) is ProjectNodeViewModel project && project.Project.GroupId != null)
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;

        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ProjectDragFormat)) return;

        // Don't handle if dropped on a group (the group's Drop handler takes care of it)
        var hit = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (hit?.DataContext is ProjectGroupNodeViewModel)
            return;

        if (e.Data.GetData(ProjectDragFormat) is not ProjectNodeViewModel project) return;
        if (DataContext is not ProjectsPanelViewModel vm) return;

        vm.MoveProjectToGroupCommand.Execute(new object[] { project, null! });
        e.Handled = true;
    }

    private void SetDropHighlight(TreeViewItem item)
    {
        ClearDropHighlight();
        _dropHighlightItem = item;
        item.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0xAA));
        item.BorderThickness = new Thickness(1);
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlightItem != null)
        {
            _dropHighlightItem.BorderBrush = null;
            _dropHighlightItem.BorderThickness = new Thickness(0);
            _dropHighlightItem = null;
        }
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
