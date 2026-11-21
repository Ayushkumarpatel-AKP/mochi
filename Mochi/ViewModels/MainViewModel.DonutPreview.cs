using CommunityToolkit.Mvvm.Input;
using Mochi.Services;


namespace Mochi.ViewModels;

/// <summary>What the Presets page shows about the donut being edited: a live preview, its slots, and quick removal.</summary>
public partial class MainViewModel
{
    public const int DonutSlots = 8;

    public bool IsFront => FaceIndex == 0;
    public bool IsBack => FaceIndex == 1;
    [RelayCommand] private void ShowFront() => FaceIndex = 0;
    [RelayCommand] private void ShowBack() => FaceIndex = 1;

    private int ShownSlots => CurrentIds.Count(id => id != "g:ai" || Ai.IsConfigured);   // the AI slice only counts while an AI is connected
    public string DonutSlotsText => Loc.T("x.donut_slots", ShownSlots, DonutSlots);
    public string DonutHint => ShownSlots >= DonutSlots ? Loc.T("x.donut_full") : Loc.T("x.donut_add_hint");

    private void NotifyDonutPreview()
    {
        OnPropertyChanged(nameof(DonutSlotsText));
        OnPropertyChanged(nameof(DonutHint));
        OnPropertyChanged(nameof(IsFront));
        OnPropertyChanged(nameof(IsBack));
        OnPropertyChanged(nameof(IsEverydayDonut));
    }

    /// <summary>Takes a slice out of the donut; the cards below follow.</summary>
    [RelayCommand]
    private void RemoveFromDonut(SliceRow r) { SetInDonut(r.Id, false); RefreshPresets(); }

    /// <summary>Same, from a click on the preview donut itself.</summary>
    public void RemoveFromDonut(string id) { SetInDonut(id, false); RefreshPresets(); }

    // ---------------------------------------------------------------- "pictures have their own donut"
    private DonutProfile? _noteProfile;
    private string _notePreset = "";

    public string DonutNote { get; private set; } = "";
    public bool HasDonutNote => DonutNote.Length > 0;
    public string DonutNoteAction { get; private set; } = "";
    public bool IsEverydayDonut => ProfileIndex == 0;

    private void ClearDonutNote()
    {
        _noteProfile = null; DonutNote = ""; DonutNoteAction = "";
        OnPropertyChanged(nameof(DonutNote)); OnPropertyChanged(nameof(HasDonutNote)); OnPropertyChanged(nameof(DonutNoteAction));
    }

    /// <summary>
    /// A preset added to the everyday donut does not appear for files that have a donut of their own (pictures use the Images donut,
    /// PDFs the PDF donut…). Say so, and offer to add it there too, so nobody wonders why a slice is missing.
    /// </summary>
    private void ExplainProfiles(string id)
    {
        ClearDonutNote();
        if (ProfileIndex != 0 || id.StartsWith("g:") || Converter.Find(id) is not { } preset) return;
        foreach (var pr in App.Settings.Profiles)
        {
            var ext = pr.Extensions.FirstOrDefault();
            if (ext is null || pr.PresetIds.Contains(id) || pr.BackPresetIds.Contains(id)) continue;
            if (!Converter.IsSupported("x." + ext, preset)) continue;
            _noteProfile = pr; _notePreset = id;
            DonutNote = Loc.T("x.donut_note", pr.Name, preset.DisplayName);
            DonutNoteAction = Loc.T("x.donut_note_add", pr.Name);
            break;
        }
        OnPropertyChanged(nameof(DonutNote)); OnPropertyChanged(nameof(HasDonutNote)); OnPropertyChanged(nameof(DonutNoteAction));
    }

    [RelayCommand]
    private void AddToProfile()
    {
        if (_noteProfile is not { } pr) return;
        var name = pr.Name;
        List<string>? target = pr.PresetIds.Count < DonutSlots ? pr.PresetIds : pr.BackPresetIds.Count < DonutSlots ? pr.BackPresetIds : null;
        if (target is null)
        {
            // both faces are full: take the user to that donut to choose what to drop
            ClearDonutNote();
            ProfileIndex = App.Settings.Profiles.IndexOf(pr) + 1;
            DonutNote = Loc.T("x.donut_profile_full", name);
            OnPropertyChanged(nameof(DonutNote)); OnPropertyChanged(nameof(HasDonutNote));
            return;
        }
        target.Add(_notePreset);
        App.Settings.Save();
        ClearDonutNote();
        RefreshPresets();
    }
}
