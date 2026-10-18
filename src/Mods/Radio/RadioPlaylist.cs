using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Radio;

/// <summary>
/// Where the radio is: the station and the track in it. Next / previous wrap around inside the station; station
/// switches wrap around the list and start at the station's first track. With <see cref="Shuffle"/> a station plays
/// in a random order that visits every track once per round. Pure, so the tests cover it.
/// </summary>
internal sealed class RadioPlaylist
{
    private List<RadioStation> _stations;
    private readonly Random _random;
    private bool _shuffle;
    private int[] _order = Array.Empty<int>();
    private int _station, _pos;

    public RadioPlaylist(List<RadioStation> stations, int seed)
    {
        _stations = stations ?? new List<RadioStation>();
        _random = new Random(seed);
        BuildOrder(keepTrack: -1);
    }

    public IReadOnlyList<RadioStation> Stations => _stations;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            BuildOrder(keepTrack: CurrentIndex);
        }
    }

    public RadioStation CurrentStation => _stations.Count == 0 ? null : _stations[_station];

    public RadioTrack Current => CurrentStation == null || _order.Length == 0 ? null : CurrentStation.Tracks[_order[_pos]];

    private int CurrentIndex => _order.Length == 0 ? -1 : _order[_pos];

    public RadioTrack Next()
    {
        if (_order.Length == 0) return null;
        _pos++;
        if (_pos >= _order.Length)
        {
            _pos = 0;
            if (_shuffle && _order.Length > 2)
            {
                // A new round: reshuffle, but don't start it with the track that just ended.
                int last = _order[_order.Length - 1];
                BuildOrder(keepTrack: -1);
                if (_order[0] == last) (_order[0], _order[_order.Length - 1]) = (_order[_order.Length - 1], _order[0]);
            }
        }
        return Current;
    }

    public RadioTrack Prev()
    {
        if (_order.Length == 0) return null;
        _pos = (_pos - 1 + _order.Length) % _order.Length;
        return Current;
    }

    public RadioStation NextStation() => StepStation(1);

    public RadioStation PrevStation() => StepStation(-1);

    private RadioStation StepStation(int step)
    {
        if (_stations.Count == 0) return null;
        _station = ((_station + step) % _stations.Count + _stations.Count) % _stations.Count;
        BuildOrder(keepTrack: -1);
        return CurrentStation;
    }

    public bool SelectStation(string name)
    {
        for (int i = 0; i < _stations.Count; i++)
        {
            if (!string.Equals(_stations[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            _station = i;
            BuildOrder(keepTrack: -1);
            return true;
        }
        return false;
    }

    /// <summary>A new scan of the folder: stays on the same station and track when they are still there.</summary>
    public void Replace(List<RadioStation> stations)
    {
        string stationName = CurrentStation?.Name;
        string trackPath = Current?.Path;
        _stations = stations ?? new List<RadioStation>();
        _station = 0;
        int keep = -1;
        for (int i = 0; i < _stations.Count && stationName != null; i++)
        {
            if (!string.Equals(_stations[i].Name, stationName, StringComparison.OrdinalIgnoreCase)) continue;
            _station = i;
            var tracks = _stations[i].Tracks;
            for (int t = 0; t < tracks.Count; t++)
                if (string.Equals(tracks[t].Path, trackPath, StringComparison.OrdinalIgnoreCase)) { keep = t; break; }
            break;
        }
        BuildOrder(keep);
    }

    /// <summary>The play order of the current station; <paramref name="keepTrack"/> (a track index) becomes current.</summary>
    private void BuildOrder(int keepTrack)
    {
        int n = CurrentStation?.Tracks.Count ?? 0;
        _order = new int[n];
        for (int i = 0; i < n; i++) _order[i] = i;
        if (_shuffle)
            for (int i = n - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
        _pos = 0;
        if (keepTrack < 0) return;
        for (int i = 0; i < n; i++)
        {
            if (_order[i] != keepTrack) continue;
            // Shuffled: the kept track opens the round, so the round still visits every other track once.
            if (_shuffle) (_order[0], _order[i]) = (_order[i], _order[0]);
            else _pos = i;
            break;
        }
    }
}
