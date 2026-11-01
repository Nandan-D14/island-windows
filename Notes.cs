using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Island;

// Quick notes: one auto-saved scratchpad in %APPDATA%\Island\notes.txt
public partial class MainWindow
{
    static readonly string NotesFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "notes.txt");
    readonly DispatcherTimer _notesSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _notesLoading;

    void LoadNotes()
    {
        _notesLoading = true;
        try { if (File.Exists(NotesFile)) NotesBox.Text = File.ReadAllText(NotesFile); } catch { }
        _notesLoading = false;
        _notesSave.Tick += (_, _) => { _notesSave.Stop(); SaveNotes(); };
        ShowNotesView();
    }

    // ---- formatted view (markdown) while you're not typing ----
    void ShowNotesView()
    {
        if (NotesBox.IsKeyboardFocused || NotesBox.Text.Trim() == "") { NotesView.Visibility = Visibility.Collapsed; NotesBox.Opacity = 1; return; }
        NotesMd.Content = Md.Render(NotesBox.Text, 13, ToggleNoteCheck);
        NotesView.Visibility = Visibility.Visible;
        NotesBox.Opacity = 0; // the formatted view's glass is see-through: hide the raw text under it
    }

    void OnNotesBlur(object s, System.Windows.Input.KeyboardFocusChangedEventArgs e) => ShowNotesView();

    // click the formatted view = edit (except on a checkbox or a link)
    void OnNotesViewClick(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Tag: "md-check" }) return;
        if (e.OriginalSource is System.Windows.Documents.TextElement te && (te is System.Windows.Documents.Hyperlink || te.Parent is System.Windows.Documents.Hyperlink)) return;
        NotesView.Visibility = Visibility.Collapsed;
        NotesBox.Opacity = 1;
        OnInputMouseDown(NotesBox, e);
        NotesBox.Focus();
        NotesBox.CaretIndex = NotesBox.Text.Length;
        NotesBox.ScrollToEnd();
        e.Handled = true;
    }

    // - [ ] task  <->  - [x] task
    void ToggleNoteCheck(int line)
    {
        var lines = NotesBox.Text.Split('\n');
        if (line < 0 || line >= lines.Length) return;
        var l = lines[line];
        var m = System.Text.RegularExpressions.Regex.Match(l, @"\[([ xX])\]");
        if (!m.Success) return;
        lines[line] = l[..m.Index] + (m.Groups[1].Value == " " ? "[x]" : "[ ]") + l[(m.Index + m.Length)..];
        NotesBox.Text = string.Join("\n", lines);
        ShowNotesView();
    }

    void OnNotesChanged(object s, TextChangedEventArgs e)
    {
        if (_notesLoading) return;
        NotesSaved.Text = "";
        _notesSave.Stop(); _notesSave.Start();
        if (!NotesBox.IsKeyboardFocused) ShowNotesView(); // changed by Gemini ("add a note") or a checkbox
    }

    void SaveNotes()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(NotesFile)!);
            File.WriteAllText(NotesFile, NotesBox.Text);
            NotesSaved.Text = "Saved";
        }
        catch { NotesSaved.Text = "Couldn't save"; }
    }

    string AddNote(string text)
    {
        text = text.Trim();
        if (text == "") return "Nothing to note.";
        var cur = NotesBox.Text.TrimEnd();
        NotesBox.Text = (cur == "" ? "" : cur + Environment.NewLine) + "\u2022 " + text;
        _notesSave.Stop(); SaveNotes();
        return "Noted: " + text;
    }
}
