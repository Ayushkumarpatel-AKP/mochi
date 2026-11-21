using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mochi.Services;

namespace Mochi.ViewModels;

/// <summary>A chip in a group: one preset that fans out from the group slice.</summary>
public sealed record MemberChip(string Id, string Label);

/// <summary>A group of presets shown as one donut slice; the user names it and picks its members.</summary>
public sealed partial class GroupRow : ObservableObject
{
    private readonly MainViewModel _vm;
    public DonutGroup Group { get; }
    public ObservableCollection<MemberChip> Members { get; } = new();

    public GroupRow(DonutGroup g, MainViewModel vm)
    {
        Group = g; _vm = vm;
        Reload();
    }

    private void Reload()
    {
        Members.Clear();
        foreach (var id in Group.Members) if (Converter.Find(id) is { } p) Members.Add(new MemberChip(id, p.DisplayName));
    }

    public string Name
    {
        get => Group.Name;
        set { Group.Name = value; _vm.GroupsChanged(); OnPropertyChanged(); }
    }

    /// <summary>Picking a preset in the box adds it to the group (up to 8).</summary>
    public Preset? PickToAdd
    {
        get => null;
        set
        {
            if (value is not null && Group.Members.Count < 8 && !Group.Members.Contains(value.Id)) { Group.Members.Add(value.Id); Reload(); _vm.GroupsChanged(); }
            OnPropertyChanged();
        }
    }

    [RelayCommand] private void RemoveMember(MemberChip c) { Group.Members.Remove(c.Id); Reload(); _vm.GroupsChanged(); }
    [RelayCommand] private void Delete() => _vm.DeleteGroup(this);
}

/// <summary>The donut's back face and groups, the output folder and other everyday options, right-click menu and update checks.</summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- front / back face
    public string[] Faces => new[] { Loc.T("x.front"), Loc.T("x.back") };

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFront), nameof(IsBack))] private int _faceIndex;
    partial void OnFaceIndexChanged(int value) => RefreshPresets();

    // ---------------------------------------------------------------- groups
    public ObservableCollection<GroupRow> GroupRows { get; } = new();

    private void LoadGroups()
    {
        GroupRows.Clear();
        foreach (var g in App.Settings.Groups) GroupRows.Add(new GroupRow(g, this));
    }

    public void ReloadGroups() => LoadGroups();

    public void GroupsChanged() { App.Settings.Save(); RefreshPresets(); }

    [RelayCommand]
    private void NewGroup()
    {
        var g = new DonutGroup { Id = Guid.NewGuid().ToString("N")[..6], Name = Loc.T("x.group") + " " + (App.Settings.Groups.Count + 1) };
        App.Settings.Groups.Add(g);
        GroupRows.Add(new GroupRow(g, this));
        GroupsChanged();
    }

    public void DeleteGroup(GroupRow r)
    {
        var id = "g:" + r.Group.Id;
        App.Settings.Groups.Remove(r.Group);
        App.Settings.DonutIds.Remove(id); App.Settings.DonutBackIds.Remove(id);
        foreach (var p in App.Settings.Profiles) { p.PresetIds.Remove(id); p.BackPresetIds.Remove(id); }
        GroupRows.Remove(r);
        GroupsChanged();
    }

    // ---------------------------------------------------------------- everyday options
    public string OutputFolderLabel => string.IsNullOrWhiteSpace(App.Settings.OutputFolder) ? Loc.T("x.next_to_original") : App.Settings.OutputFolder;
    public bool OpenFolderWhenDone { get => App.Settings.OpenFolderWhenDone; set { App.Settings.OpenFolderWhenDone = value; App.Settings.Save(); } }
    public bool CheckUpdates { get => App.Settings.CheckUpdates; set { App.Settings.CheckUpdates = value; App.Settings.Save(); } }

    public void SetOutputFolder(string? folder)
    {
        App.Settings.OutputFolder = folder ?? "";
        App.Settings.Save();
        OnPropertyChanged(nameof(OutputFolderLabel));
    }

    [RelayCommand] private void ResetOutputFolder() => SetOutputFolder(null);

    public Func<Task<string?>>? PickFolder { get; set; }

    [RelayCommand]
    private async Task ChooseOutputFolder()
    {
        if (PickFolder is null) return;
        var f = await PickFolder();
        if (f is not null) SetOutputFolder(f);
    }

    /// <summary>"Convert with Mochi" in Explorer's right-click menu (files and folders), per user, no administrator rights.</summary>
    public bool ContextMenu
    {
        get => App.Settings.ContextMenu;
        set { App.Settings.ContextMenu = value; App.Settings.Save(); ShellIntegration.Set(value); OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- window size
    public double WindowScale
    {
        get => App.Settings.WindowScale;
        set { App.SetScale(value); OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- updates
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateBusy;
    private Updater.Release? _found;
    public bool UpdateReady => _found is not null;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        UpdateBusy = true; UpdateStatus = Loc.T("x.up.checking");
        var r = await Updater.CheckAsync();
        _found = r.Newer ? r : null;
        UpdateStatus = r.Error is not null ? Loc.T("x.up.failed", r.Error) : r.Newer ? Loc.T("x.up.available", r.Version) : Loc.T("x.up.latest", Updater.Current);
        OnPropertyChanged(nameof(UpdateReady));
        UpdateBusy = false;
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_found is null) return;
        UpdateBusy = true; UpdateStatus = Loc.T("x.up.downloading", _found.Version);
        try { await Updater.DownloadAndRunAsync(_found); }
        catch (Exception ex) { UpdateStatus = Loc.T("x.up.failed", ex.Message); }
        UpdateBusy = false;
    }
}
