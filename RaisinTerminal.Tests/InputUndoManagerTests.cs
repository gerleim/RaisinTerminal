using RaisinTerminal.Core.Terminal;
using Xunit;

namespace RaisinTerminal.Tests;

public class InputUndoManagerTests
{
    [Fact]
    public void Undo_ReturnsNull_WhenEmpty()
    {
        var mgr = new InputUndoManager();
        Assert.Null(mgr.Undo());
        Assert.False(mgr.CanUndo);
    }

    [Fact]
    public void Undo_ReturnsPreviousStates()
    {
        var mgr = new InputUndoManager();
        mgr.Record("h", "paste");
        mgr.Record("he", "paste");
        Assert.Equal("h", mgr.Undo());
        Assert.Equal("", mgr.Undo());
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void Redo_AfterUndo()
    {
        var mgr = new InputUndoManager();
        mgr.Record("hello", "paste");
        mgr.Undo(); // back to ""
        Assert.Equal("hello", mgr.Redo());
        Assert.Null(mgr.Redo());
    }

    [Fact]
    public void Record_TruncatesRedoHistory()
    {
        var mgr = new InputUndoManager();
        mgr.Record("a", "paste");
        mgr.Record("ab", "paste");
        mgr.Undo(); // back to "a"
        mgr.Record("ax", "paste"); // diverge
        Assert.Null(mgr.Redo()); // "ab" is gone
        Assert.Equal("a", mgr.Undo());
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var mgr = new InputUndoManager();
        mgr.Record("hello", "paste");
        mgr.Clear();
        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void PasteIsAlwaysDiscrete()
    {
        var mgr = new InputUndoManager();
        mgr.Record("pasted", "paste");
        mgr.Record("pasted more", "paste");
        Assert.Equal("pasted", mgr.Undo());
        Assert.Equal("", mgr.Undo());
    }

    [Fact]
    public void DeleteCreatesDiscreteUndoPoint()
    {
        var mgr = new InputUndoManager();
        mgr.Record("hello", "paste");
        mgr.Record("hell", "delete");
        Assert.Equal("hello", mgr.Undo());
    }

    [Fact]
    public void DuplicateState_NotRecorded()
    {
        var mgr = new InputUndoManager();
        mgr.Record("hello", "paste");
        mgr.Record("hello", "paste"); // duplicate
        Assert.Equal("", mgr.Undo());
        Assert.Null(mgr.Undo()); // only one undo step
    }

    [Fact]
    public void CanUndo_CanRedo_ReflectState()
    {
        var mgr = new InputUndoManager();
        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        mgr.Record("a", "paste");
        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        mgr.Undo();
        Assert.False(mgr.CanUndo);
        Assert.True(mgr.CanRedo);
    }

    [Fact]
    public void CharTyping_MergesWithinWord_UpToMaxLength()
    {
        var mgr = new InputUndoManager();
        // Simulate typing "hello" (5 chars, under max merge length of 8)
        mgr.Record("h", "char");
        mgr.Record("he", "char");
        mgr.Record("hel", "char");
        mgr.Record("hell", "char");
        mgr.Record("hello", "char");
        // All merged into one undo unit (5 chars < 8 max)
        Assert.Equal("", mgr.Undo());
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void CharTyping_SplitsAfterMaxMergeLength()
    {
        var mgr = new InputUndoManager();
        // Type 16 chars without any word boundary
        for (int i = 1; i <= 16; i++)
            mgr.Record(new string('a', i), "char");
        // Should have multiple undo steps (split every ~8 chars)
        var first = mgr.Undo();
        Assert.NotNull(first);
        Assert.True(first!.Length < 16, $"Expected partial undo, got length {first.Length}");
        Assert.True(first.Length > 0, "Expected non-empty partial undo");
    }

    [Fact]
    public void CharTyping_SpaceStaysWithPrecedingWord()
    {
        var mgr = new InputUndoManager();
        // Simulate typing "hi there"
        // Space should stay with "hi", not start a new undo unit
        mgr.Record("h", "char");
        mgr.Record("hi", "char");
        mgr.Record("hi ", "char");       // space merges with "hi"
        mgr.Record("hi t", "char");      // new word starts → new checkpoint
        mgr.Record("hi th", "char");
        mgr.Record("hi the", "char");
        mgr.Record("hi ther", "char");
        mgr.Record("hi there", "char");
        // Undo should remove whole words (with trailing spaces)
        Assert.Equal("hi ", mgr.Undo());  // undo "there"
        Assert.Equal("", mgr.Undo());     // undo "hi " (space included)
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void CharTyping_SplitsOnNewWord()
    {
        var mgr = new InputUndoManager();
        mgr.Record("hello", "char");
        mgr.Record("hello\n", "char");    // newline merges with "hello"
        mgr.Record("hello\nworld", "char"); // new word after \n → new checkpoint
        Assert.Equal("hello\n", mgr.Undo()); // undo "world"
        Assert.Equal("", mgr.Undo());        // undo "hello\n"
    }

    [Fact]
    public void CharTyping_SplitsOnSlash()
    {
        var mgr = new InputUndoManager();
        // Simulate typing "cd /home/user"
        mgr.Record("c", "char");
        mgr.Record("cd", "char");
        mgr.Record("cd ", "char");        // space merges with "cd"
        mgr.Record("cd /", "char");       // new word after space → new checkpoint, "/" merges
        mgr.Record("cd /h", "char");      // new word after "/" → new checkpoint
        mgr.Record("cd /ho", "char");
        mgr.Record("cd /hom", "char");
        mgr.Record("cd /home", "char");
        mgr.Record("cd /home/", "char");  // "/" merges with "home"
        mgr.Record("cd /home/u", "char"); // new word after "/" → new checkpoint
        mgr.Record("cd /home/us", "char");
        mgr.Record("cd /home/user", "char");
        Assert.Equal("cd /home/", mgr.Undo());  // undo "user"
        Assert.Equal("cd /", mgr.Undo());       // undo "home/"
        Assert.Equal("", mgr.Undo());            // undo "cd /" (space and slash merged)
    }
}
