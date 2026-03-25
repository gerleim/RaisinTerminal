using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Raisin.WPF.Base;
using RaisinTerminal.Services;

namespace RaisinTerminal.ViewModels;

public class ImageGalleryViewModel : ToolWindowViewModel
{
    public string ProjectId { get; }

    public ObservableCollection<string> ImagePaths { get; } = [];

    private string? _selectedImage;
    public string? SelectedImage
    {
        get => _selectedImage;
        set => SetProperty(ref _selectedImage, value);
    }

    public ICommand OpenInExplorerCommand { get; }
    public ICommand DeleteAttachmentCommand { get; }
    public ICommand CopyPathCommand { get; }

    public ImageGalleryViewModel(string projectId, string projectName)
    {
        ProjectId = projectId;
        ContentId = $"attachments-{projectId}";
        UpdateBaseTitle($"Attachments - {projectName}");

        OpenInExplorerCommand = new RelayCommand(OpenInExplorer);
        DeleteAttachmentCommand = new RelayCommand(DeleteAttachment);
        CopyPathCommand = new RelayCommand(CopyPath);

        Refresh();
    }

    public void Refresh()
    {
        var files = AttachmentService.GetAttachments(ProjectId);
        var selected = SelectedImage;

        ImagePaths.Clear();
        foreach (var file in files)
            ImagePaths.Add(file);

        if (selected != null && ImagePaths.Contains(selected))
            SelectedImage = selected;
        else
            SelectedImage = ImagePaths.FirstOrDefault();
    }

    public void AddImage(string path)
    {
        ImagePaths.Insert(0, path);
    }

    public void RemoveImage(string path)
    {
        ImagePaths.Remove(path);
        if (SelectedImage == path)
            SelectedImage = ImagePaths.FirstOrDefault();
    }

    private void OpenInExplorer()
    {
        var dir = AttachmentService.GetProjectAttachmentsDir(ProjectId);
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir));
    }

    private void DeleteAttachment()
    {
        if (SelectedImage == null) return;
        var path = SelectedImage;
        AttachmentService.DeleteAttachment(path);
        ImagePaths.Remove(path);
        SelectedImage = ImagePaths.FirstOrDefault();
    }

    private void CopyPath()
    {
        if (SelectedImage != null)
            Clipboard.SetText(SelectedImage);
    }
}
