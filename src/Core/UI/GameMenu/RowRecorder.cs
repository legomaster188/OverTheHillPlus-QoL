using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The recording pass behind the game-widget pages. The page's draw code (a mod's DrawSimple / DrawAdvanced, the
/// sections it added, the MODS list) runs unchanged while <see cref="Recording"/> is on: every page widget then
/// adds a <see cref="RowModel"/> here instead of drawing, and takes the player's input for it from the last frame
/// (<see cref="Take"/>), so setters, button actions and return values behave exactly as on the drawn page.
///
/// Inputs are matched to rows by their position on the page and their <see cref="RowModel.Key"/>: if the page
/// changed between the press and the next pass (a row appeared above it), the input is dropped instead of going to
/// another row. Also notices content the widgets can't describe (a mod drawing with raw GUI calls moves the layout
/// cursor by itself): <see cref="Untracked"/> then tells the host (it notes the page in the log).
/// Pure logic (no Unity or BepInEx types), unit tested.
/// </summary>
internal sealed class RowRecorder
{
    /// <summary>The recorder of the pass running now (null: no pass, the widgets draw as usual).</summary>
    public static RowRecorder Current { get; private set; }

    /// <summary>True while a recording pass runs: the page widgets record instead of drawing.</summary>
    public static bool Recording => Current != null;

    private readonly List<RowModel> _rows = new();
    private readonly Dictionary<int, (string key, RowInput input)> _pending = new();
    private float _trackedY;
    private int _customRowFrom = -1;
    private bool _customHadPieces;
    private float _customTop, _customBottom;
    private float _labelY = float.NaN;

    /// <summary>Gaps the layout cursor moved by itself up to this much are spacing, not hidden content.</summary>
    public const float SpacingTolerance = 14f;

    /// <summary>The rows of the last finished pass.</summary>
    public IReadOnlyList<RowModel> Rows => _rows;

    /// <summary>True when the last pass had content the widgets couldn't describe (raw GUI calls: they don't show).</summary>
    public bool Untracked { get; private set; }

    /// <summary>Starts a pass at layout position <paramref name="y"/>; inputs queued since the last pass stay for it.</summary>
    public void Begin(float y)
    {
        _rows.Clear();
        Untracked = false;
        _trackedY = y;
        _customRowFrom = -1;
        _labelY = float.NaN;
        Current = this;
    }

    /// <summary>Ends the pass; inputs no row took are dropped.</summary>
    public void End(float y)
    {
        CloseCustomRow();
        Gap(y);
        _pending.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>Stops a pass that threw (the next pass starts clean).</summary>
    public void Abort()
    {
        _pending.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>Queues what the player did to row <paramref name="index"/> of the last pass, for the next pass.</summary>
    public void Queue(int index, RowInput input)
    {
        if (index < 0 || index >= _rows.Count) return;
        _pending[index] = (_rows[index].Key, input);
    }

    /// <summary>
    /// Adds a row at layout position <paramref name="y"/> (its top) that takes up to <paramref name="height"/>,
    /// and returns the input queued for it, if any.
    /// </summary>
    public RowInput? Take(RowModel row, float y, float height)
    {
        CloseCustomRow();
        Gap(y);
        _labelY = float.NaN;
        int index = _rows.Count;
        _rows.Add(row);
        _trackedY = Math.Max(_trackedY, y + height);
        if (!_pending.TryGetValue(index, out var p)) return null;
        _pending.Remove(index);
        return p.key == row.Key ? p.input : null;
    }

    /// <summary>
    /// A text label placed at a rect (Menu.Label): labels on the same line join into one row, so a custom row's
    /// "name ... value" reads as one line.
    /// </summary>
    public void Label(string text, float y, float height)
    {
        if (string.IsNullOrEmpty(text)) { Track(y + height); return; }
        if (_customRowFrom >= 0 && y >= _customTop - 1f && y < _customBottom) _customHadPieces = true;
        else CloseCustomRow();
        if (!float.IsNaN(_labelY) && Math.Abs(_labelY - y) < 4f && _rows.Count > 0 && _rows[_rows.Count - 1].Kind == RowKind.Note)
        {
            _rows[_rows.Count - 1].Label += "   " + text;
            Track(y + height);
            return;
        }
        if (_customRowFrom < 0) Gap(y);
        _rows.Add(new RowModel { Kind = RowKind.Note, Label = text });
        _labelY = y;
        Track(y + height);
    }

    /// <summary>A button placed at a rect (Menu.ChipButton / Menu.Button inside a custom row). Returns its queued input.</summary>
    public RowInput? Piece(RowModel row, float y, float height)
    {
        bool inCustom = _customRowFrom >= 0 && y >= _customTop - 1f && y < _customBottom;
        if (inCustom) _customHadPieces = true;
        else
        {
            CloseCustomRow();
            Gap(y);
        }
        _labelY = float.NaN;
        int index = _rows.Count;
        _rows.Add(row);
        Track(y + height);
        if (!_pending.TryGetValue(index, out var p)) return null;
        _pending.Remove(index);
        return p.key == row.Key ? p.input : null;
    }

    /// <summary>A free-form row (Menu.NextRow) from <paramref name="y"/> to <paramref name="bottom"/>: what is placed on
    /// it with the widgets shows; a row with nothing recognisable on it counts as untracked content.</summary>
    public void CustomRow(float y, float bottom)
    {
        CloseCustomRow();
        Gap(y);
        _customRowFrom = _rows.Count;
        _customHadPieces = false;
        _customTop = y;
        _customBottom = bottom;
        _labelY = float.NaN;
        Track(bottom);
    }

    private void CloseCustomRow()
    {
        if (_customRowFrom < 0) return;
        if (!_customHadPieces) Untracked = true;
        _customRowFrom = -1;
    }

    private void Gap(float y)
    {
        if (y - _trackedY > SpacingTolerance) Untracked = true;
    }

    private void Track(float bottom) => _trackedY = Math.Max(_trackedY, bottom);
}
