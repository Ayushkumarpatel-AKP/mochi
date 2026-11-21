using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;

namespace Mochi.ViewModels;

/// <summary>The folders Mochi keeps ready for "search by meaning", and the background switch.</summary>
public partial class MainViewModel
{
    public ObservableCollection<string> SearchFolders { get; } = new();

    public bool BackgroundIndex
    {
        get => App.Settings.BackgroundIndex;
        set
        {
            App.Settings.BackgroundIndex = value; App.Settings.Save();
            if (value) SmartIndexService.Start(); else SmartIndexService.Stop();
            OnPropertyChanged();
        }
    }

    private void InitSearchFolders()
    {
        SearchFolders.Clear();
        foreach (var f in SmartIndexService.Folders()) SearchFolders.Add(f);
    }

    public void AddSearchFolder(string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        if (SearchFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return;
        App.Settings.SearchFolders.Add(folder); App.Settings.Save();
        SearchFolders.Add(folder);
        _ = IndexStore.For(folder).RefreshAsync();   // start reading it now
    }

    [RelayCommand]
    private void RemoveSearchFolder(string folder)
    {
        App.Settings.SearchFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)); App.Settings.Save();
        SearchFolders.Remove(folder);
    }
}
