using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Wooduku
{
    /// <summary>
    /// 一局玩法状态：标记、确认/排除、进度与通关。
    /// </summary>
    public sealed class WoodukuGameSession
    {
        private readonly WoodukuLevelFile _level;
        private readonly WoodukuCellMark[] _marks;
        private readonly HashSet<long> _solutionKeys;
        private readonly HashSet<long> _fixedQueenKeys;
        private readonly Color[] _palette;

        public WoodukuGameSession(WoodukuLevelFile level)
        {
            _level = level ?? throw new ArgumentNullException(nameof(level));
            if (_level.size <= 0)
            {
                throw new ArgumentException("Level size must be > 0.", nameof(level));
            }

            var cellCount = _level.size * _level.size;
            if (_level.regions == null || _level.regions.Length != cellCount)
            {
                throw new ArgumentException("Level regions length mismatch.", nameof(level));
            }

            _marks = new WoodukuCellMark[cellCount];
            _solutionKeys = new HashSet<long>();
            _fixedQueenKeys = new HashSet<long>();
            if (_level.solutionCells != null)
            {
                foreach (var cell in _level.solutionCells)
                {
                    _solutionKeys.Add(Key(cell.r, cell.c));
                }
            }
            else if (_level.solutionCols != null && _level.solutionCols.Length == _level.size)
            {
                for (var r = 0; r < _level.size; r++)
                {
                    _solutionKeys.Add(Key(r, _level.solutionCols[r]));
                }
            }

            if (_level.fixedQueenCells != null)
            {
                foreach (var cell in _level.fixedQueenCells)
                {
                    if (!InBounds(cell.r, cell.c) || !IsSolution(cell.r, cell.c))
                    {
                        continue;
                    }

                    _fixedQueenKeys.Add(Key(cell.r, cell.c));
                    _marks[Index(cell.r, cell.c)] = WoodukuCellMark.Confirmed;
                }
            }

            RecalcFound();
            _palette = BuildPalette(_level);
        }

        public int Size => _level.size;
        public int LevelId => _level.id;
        public int TotalCats => _level.size;
        public int ErrorCount { get; private set; }
        public int FoundCount { get; private set; }
        public bool IsCleared => FoundCount >= TotalCats && TotalCats > 0;

        public event Action Changed;
        public event Action Cleared;

        public WoodukuCellMark GetMark(int r, int c) => _marks[Index(r, c)];

        public int GetRegion(int r, int c) => _level.regions[Index(r, c)];

        public Color GetCellColor(int r, int c)
        {
            var region = GetRegion(r, c);
            if (region < 0 || region >= _palette.Length)
            {
                return Color.gray;
            }

            return _palette[region];
        }

        public bool IsSolution(int r, int c) => _solutionKeys.Contains(Key(r, c));

        public bool IsFixedQueen(int r, int c) => _fixedQueenKeys.Contains(Key(r, c));

        /// <summary>
        /// 单击：Confirmed→None；None↔Exclude。
        /// </summary>
        public bool TryToggleExclude(int r, int c)
        {
            if (!InBounds(r, c))
            {
                return false;
            }

            if (IsFixedQueen(r, c))
            {
                return false;
            }

            var i = Index(r, c);
            var mark = _marks[i];
            if (mark == WoodukuCellMark.Confirmed)
            {
                _marks[i] = WoodukuCellMark.None;
                RecalcFound();
                NotifyChanged();
                return true;
            }

            _marks[i] = mark == WoodukuCellMark.Exclude
                ? WoodukuCellMark.None
                : WoodukuCellMark.Exclude;
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// 双击尝试确认。返回 true 表示状态有变化；isCorrect 表示是否正解。
        /// </summary>
        public bool TryConfirm(int r, int c, out bool isCorrect)
        {
            isCorrect = false;
            if (!InBounds(r, c))
            {
                return false;
            }

            if (IsFixedQueen(r, c))
            {
                isCorrect = true;
                return false;
            }

            var i = Index(r, c);
            if (_marks[i] == WoodukuCellMark.Confirmed)
            {
                _marks[i] = WoodukuCellMark.None;
                RecalcFound();
                NotifyChanged();
                isCorrect = true;
                return true;
            }

            if (IsSolution(r, c))
            {
                _marks[i] = WoodukuCellMark.Confirmed;
                isCorrect = true;
                RecalcFound();
                NotifyChanged();
                if (IsCleared)
                {
                    Cleared?.Invoke();
                }

                return true;
            }

            // 错解：进入 Exclude，计错误
            _marks[i] = WoodukuCellMark.Exclude;
            ErrorCount++;
            isCorrect = false;
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// 根据起点决定滑动模式：Exclude 格上开始→擦除；否则→打 X。不碰 Confirmed。
        /// </summary>
        public bool ResolveSwipeMode(int r, int c, out bool clearExclude)
        {
            clearExclude = false;
            if (!InBounds(r, c))
            {
                return false;
            }

            clearExclude = _marks[Index(r, c)] == WoodukuCellMark.Exclude;
            return true;
        }

        /// <summary>
        /// 滑动涂抹：clearExclude=false 时 None→Exclude；true 时 Exclude→None。不碰猴。
        /// </summary>
        public bool SwipePaint(int r, int c, bool clearExclude)
        {
            if (!InBounds(r, c))
            {
                return false;
            }

            var i = Index(r, c);
            var mark = _marks[i];
            if (mark == WoodukuCellMark.Confirmed)
            {
                return false;
            }

            if (clearExclude)
            {
                if (mark != WoodukuCellMark.Exclude)
                {
                    return false;
                }

                _marks[i] = WoodukuCellMark.None;
            }
            else
            {
                if (mark != WoodukuCellMark.None)
                {
                    return false;
                }

                _marks[i] = WoodukuCellMark.Exclude;
            }

            NotifyChanged();
            return true;
        }

        /// <summary>兼容旧调用：仅打 X。</summary>
        public bool SwipeExclude(int r, int c) => SwipePaint(r, c, clearExclude: false);

        private void RecalcFound()
        {
            var found = 0;
            foreach (var key in _solutionKeys)
            {
                Decode(key, out var r, out var c);
                if (_marks[Index(r, c)] == WoodukuCellMark.Confirmed)
                {
                    found++;
                }
            }

            FoundCount = found;
        }

        private void NotifyChanged() => Changed?.Invoke();

        private bool InBounds(int r, int c) => r >= 0 && c >= 0 && r < Size && c < Size;

        private int Index(int r, int c) => r * Size + c;

        private static long Key(int r, int c) => ((long)r << 32) | (uint)c;

        private static void Decode(long key, out int r, out int c)
        {
            r = (int)(key >> 32);
            c = (int)(key & 0xFFFFFFFF);
        }

        private static Color[] BuildPalette(WoodukuLevelFile level)
        {
            var n = level.size;
            var colors = new Color[n];
            for (var i = 0; i < n; i++)
            {
                colors[i] = Color.HSVToRGB(i / (float)Mathf.Max(1, n), 0.45f, 0.85f);
            }

            if (level.colors == null)
            {
                return colors;
            }

            foreach (var entry in level.colors)
            {
                if (entry == null || entry.id < 0 || entry.id >= n)
                {
                    continue;
                }

                colors[entry.id] = WoodukuLevelJson.HexToColor(entry.hex, colors[entry.id]);
            }

            return colors;
        }
    }
}
