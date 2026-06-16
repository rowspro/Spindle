using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace Spindle.ViewModels;

public sealed class TagActionStepVM : ViewModelBase
{
    public string[] Types { get; } = { "Title case", "Trim spaces", "Find & replace", "Set value", "Clear" };
    public string[] FieldOptions { get; } = { "Title", "Artist", "Album artist", "Album", "Genre" };

    private string _type = "Title case";
    public string Type
    {
        get => _type;
        set { if (SetField(ref _type, value)) { OnPropertyChanged(nameof(ShowFindReplace)); OnPropertyChanged(nameof(ShowValue)); } }
    }
    private string _field = "Title";
    public string Field { get => _field; set => SetField(ref _field, value); }
    private string _find = "";
    public string Find { get => _find; set => SetField(ref _find, value); }
    private string _replace = "";
    public string Replace { get => _replace; set => SetField(ref _replace, value); }
    private string _value = "";
    public string Value { get => _value; set => SetField(ref _value, value); }
    private bool _regex;
    public bool Regex { get => _regex; set => SetField(ref _regex, value); }

    public bool ShowFindReplace => Type == "Find & replace";
    public bool ShowValue => Type == "Set value";
}

public sealed class TagActionVM : ViewModelBase
{
    private string _name;
    public TagActionVM(string name) { _name = name; }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public ObservableCollection<TagActionStepVM> Steps { get; } = new();
    public string Summary => $"{Steps.Count} step{(Steps.Count == 1 ? "" : "s")}";
    public void RaiseSummary() => OnPropertyChanged(nameof(Summary));
}

/// <summary>Saved, composable tag cleanup recipes (Title-case / Trim / Find&amp;Replace / Set / Clear), applied
/// to the track-metadata table as pending edits (review + Save = undoable).</summary>
public sealed class TagActionsViewModel : ViewModelBase
{
    private readonly Action _persist;

    public TagActionsViewModel(Action persist)
    {
        _persist = persist;
        NewCommand = new RelayCommand(NewAction);
        DeleteCommand = new RelayCommand(DeleteSelected, () => Selected != null);
        AddStepCommand = new RelayCommand(AddStep, () => Selected != null);
        RemoveStepCommand = new RelayCommand(RemoveStep, () => Selected != null && SelectedStep != null);
    }

    public ObservableCollection<TagActionVM> Actions { get; } = new();

    private TagActionVM? _selected;
    public TagActionVM? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); DeleteCommand.RaiseCanExecuteChanged(); AddStepCommand.RaiseCanExecuteChanged(); } }
    }
    public bool HasSelection => Selected != null;

    private TagActionStepVM? _selectedStep;
    public TagActionStepVM? SelectedStep { get => _selectedStep; set { if (SetField(ref _selectedStep, value)) RemoveStepCommand.RaiseCanExecuteChanged(); } }

    public RelayCommand NewCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddStepCommand { get; }
    public RelayCommand RemoveStepCommand { get; }

    private void NewAction()
    {
        var a = new TagActionVM(UniqueName("New action"));
        a.Steps.Add(new TagActionStepVM());
        Actions.Add(a);
        Selected = a;
        a.RaiseSummary();
        _persist();
    }

    private void DeleteSelected()
    {
        if (Selected == null) return;
        Actions.Remove(Selected);
        Selected = Actions.FirstOrDefault();
        _persist();
    }

    private void AddStep()
    {
        if (Selected == null) return;
        Selected.Steps.Add(new TagActionStepVM());
        Selected.RaiseSummary();
        _persist();
    }

    private void RemoveStep()
    {
        if (Selected == null || SelectedStep == null) return;
        Selected.Steps.Remove(SelectedStep);
        Selected.RaiseSummary();
        _persist();
    }

    public void Persist() => _persist();

    /// <summary>Run an action over every row of the track table (as pending edits).</summary>
    public void Apply(TagActionVM a, TagGridViewModel grid)
    {
        if (a == null) return;
        var steps = a.Steps.ToList();
        grid.Mutate(row => { foreach (var s in steps) ApplyStep(s, row); });
    }

    private static void ApplyStep(TagActionStepVM s, TagRowViewModel row)
    {
        string Get() => s.Field switch
        {
            "Title" => row.Title, "Artist" => row.Artist, "Album artist" => row.AlbumArtist,
            "Album" => row.Album, "Genre" => row.Genre, _ => row.Title,
        };
        void Set(string v)
        {
            switch (s.Field)
            {
                case "Title": row.Title = v; break;
                case "Artist": row.Artist = v; break;
                case "Album artist": row.AlbumArtist = v; break;
                case "Album": row.Album = v; break;
                case "Genre": row.Genre = v; break;
            }
        }
        var cur = Get() ?? "";
        switch (s.Type)
        {
            case "Title case": Set(TitleCase(cur)); break;
            case "Trim spaces": Set(Regex.Replace(cur, @"\s+", " ").Trim()); break;
            case "Set value": Set(s.Value ?? ""); break;
            case "Clear": Set(""); break;
            case "Find & replace":
                try { Set(s.Regex ? Regex.Replace(cur, s.Find ?? "", s.Replace ?? "") : cur.Replace(s.Find ?? "", s.Replace ?? "")); }
                catch { }
                break;
        }
    }

    private static string TitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var words = s.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            words[i] = char.ToUpper(w[0]) + w.Substring(1).ToLowerInvariant();
        }
        return string.Join(" ", words);
    }

    private string UniqueName(string b)
    {
        var n = b; int k = 2;
        while (Actions.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{b} {k++}";
        return n;
    }

    public void Load(List<TagActionDto>? dtos)
    {
        Actions.Clear();
        foreach (var d in dtos ?? new List<TagActionDto>())
        {
            var a = new TagActionVM(d.Name);
            foreach (var s in d.Steps)
                a.Steps.Add(new TagActionStepVM { Type = s.Type, Field = s.Field, Find = s.Find, Replace = s.Replace, Value = s.Value, Regex = s.Regex });
            Actions.Add(a);
        }
        Selected = Actions.FirstOrDefault();
    }

    public List<TagActionDto> Snapshot() =>
        Actions.Select(a => new TagActionDto
        {
            Name = a.Name,
            Steps = a.Steps.Select(s => new TagActionStepDto { Type = s.Type, Field = s.Field, Find = s.Find, Replace = s.Replace, Value = s.Value, Regex = s.Regex }).ToList(),
        }).ToList();
}
