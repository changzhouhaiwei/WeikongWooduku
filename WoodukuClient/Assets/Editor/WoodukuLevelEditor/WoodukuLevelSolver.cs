using System.Collections.Generic;
using System.Text;
using GameLogic.Wooduku;

namespace Wooduku.LevelEditor
{
    /// <summary>
    /// 关卡校验与唯一解搜索（颜色唯一 + 行列唯一 + 八邻不接触）。
    /// </summary>
    public static class WoodukuLevelSolver
    {
        public sealed class Result
        {
            public bool BoardValid;
            public string BoardError;
            public int SolutionCount;
            /// <summary>每行放置的列索引；仅当 SolutionCount &gt;= 1 时有效。</summary>
            public int[] FirstSolutionCols;
            public bool HasUniqueSolution => BoardValid && SolutionCount == 1;
        }

        /// <summary>
        /// regions 为 row-major，长度 size*size，值为颜色 id（建议 0..size-1）。
        /// expectedColorCount：启用的颜色种数，须等于 size。
        /// </summary>
        public static Result Analyze(int size, int[] regions, int expectedColorCount)
        {
            return Analyze(size, regions, expectedColorCount, null);
        }

        public static Result Analyze(
            int size,
            int[] regions,
            int expectedColorCount,
            IReadOnlyList<WoodukuCellRef> fixedQueenCells)
        {
            var result = new Result();
            if (size < 2 || size > 12)
            {
                result.BoardValid = false;
                result.BoardError = "边长须在 2～12。";
                return result;
            }

            if (regions == null || regions.Length != size * size)
            {
                result.BoardValid = false;
                result.BoardError = "格子数组长度须为 size*size。";
                return result;
            }

            if (expectedColorCount != size)
            {
                result.BoardValid = false;
                result.BoardError = $"启用颜色数须等于边长 N（当前启用 {expectedColorCount}，N={size}）。";
                return result;
            }

            var counts = new Dictionary<int, int>();
            for (var i = 0; i < regions.Length; i++)
            {
                var id = regions[i];
                if (id < 0)
                {
                    result.BoardValid = false;
                    result.BoardError = $"格子 ({i / size},{i % size}) 未标定颜色。";
                    return result;
                }

                counts.TryGetValue(id, out var c);
                counts[id] = c + 1;
            }

            if (counts.Count != size)
            {
                result.BoardValid = false;
                result.BoardError = $"棋盘上实际出现 {counts.Count} 种颜色，须恰好 {size} 种。";
                return result;
            }

            for (var color = 0; color < size; color++)
            {
                if (!counts.ContainsKey(color))
                {
                    result.BoardValid = false;
                    result.BoardError = $"缺少颜色 id={color}（颜色 id 须为连续 0..N-1）。";
                    return result;
                }
            }

            var connErr = CheckFourConnected(size, regions);
            if (connErr != null)
            {
                result.BoardValid = false;
                result.BoardError = connErr;
                return result;
            }

            result.BoardValid = true;
            result.BoardError = null;

            var solutions = new List<int[]>();
            var colUsed = new bool[size];
            var colorUsed = new bool[size];
            var placeCol = new int[size];
            var requiredCols = new int[size];
            for (var row = 0; row < size; row++)
            {
                requiredCols[row] = -1;
            }

            if (fixedQueenCells != null)
            {
                for (var i = 0; i < fixedQueenCells.Count; i++)
                {
                    var cell = fixedQueenCells[i];
                    if (cell == null ||
                        cell.r < 0 ||
                        cell.r >= size ||
                        cell.c < 0 ||
                        cell.c >= size)
                    {
                        result.BoardValid = false;
                        result.BoardError = "预置皇后坐标越界。";
                        return result;
                    }

                    if (requiredCols[cell.r] >= 0 && requiredCols[cell.r] != cell.c)
                    {
                        result.BoardValid = false;
                        result.BoardError = $"第 {cell.r} 行存在多个预置皇后。";
                        return result;
                    }

                    requiredCols[cell.r] = cell.c;
                }
            }

            Search(
                size,
                regions,
                requiredCols,
                0,
                colUsed,
                colorUsed,
                placeCol,
                solutions,
                maxSolutions: 2);

            result.SolutionCount = solutions.Count;
            if (solutions.Count > 0)
            {
                result.FirstSolutionCols = (int[])solutions[0].Clone();
            }

            return result;
        }

        public static string FormatSolution(int size, int[] cols)
        {
            if (cols == null || cols.Length != size)
            {
                return "(无)";
            }

            var sb = new StringBuilder();
            for (var r = 0; r < size; r++)
            {
                if (r > 0)
                {
                    sb.Append(", ");
                }

                sb.Append($"({r},{cols[r]})");
            }

            return sb.ToString();
        }

        private static string CheckFourConnected(int size, int[] regions)
        {
            var visited = new bool[size * size];
            for (var color = 0; color < size; color++)
            {
                var start = -1;
                var total = 0;
                for (var i = 0; i < regions.Length; i++)
                {
                    if (regions[i] == color)
                    {
                        total++;
                        if (start < 0)
                        {
                            start = i;
                        }
                    }
                }

                if (total == 0)
                {
                    return $"颜色 {color} 无格子。";
                }

                System.Array.Clear(visited, 0, visited.Length);
                var stack = new Stack<int>();
                stack.Push(start);
                visited[start] = true;
                var reached = 0;
                while (stack.Count > 0)
                {
                    var idx = stack.Pop();
                    reached++;
                    var r = idx / size;
                    var c = idx % size;
                    TryPush(r - 1, c);
                    TryPush(r + 1, c);
                    TryPush(r, c - 1);
                    TryPush(r, c + 1);

                    void TryPush(int nr, int nc)
                    {
                        if (nr < 0 || nr >= size || nc < 0 || nc >= size)
                        {
                            return;
                        }

                        var ni = nr * size + nc;
                        if (visited[ni] || regions[ni] != color)
                        {
                            return;
                        }

                        visited[ni] = true;
                        stack.Push(ni);
                    }
                }

                if (reached != total)
                {
                    return $"颜色 {color} 不是四连通色块（共 {total} 格，连通 {reached} 格）。";
                }
            }

            return null;
        }

        private static void Search(
            int size,
            int[] regions,
            int[] requiredCols,
            int row,
            bool[] colUsed,
            bool[] colorUsed,
            int[] placeCol,
            List<int[]> solutions,
            int maxSolutions)
        {
            if (solutions.Count >= maxSolutions)
            {
                return;
            }

            if (row == size)
            {
                solutions.Add((int[])placeCol.Clone());
                return;
            }

            var firstCol = requiredCols[row] >= 0 ? requiredCols[row] : 0;
            var lastCol = requiredCols[row] >= 0 ? requiredCols[row] : size - 1;
            for (var col = firstCol; col <= lastCol; col++)
            {
                if (colUsed[col])
                {
                    continue;
                }

                var color = regions[row * size + col];
                if (colorUsed[color])
                {
                    continue;
                }

                if (TouchesPrevious(row, col, placeCol))
                {
                    continue;
                }

                colUsed[col] = true;
                colorUsed[color] = true;
                placeCol[row] = col;
                Search(
                    size,
                    regions,
                    requiredCols,
                    row + 1,
                    colUsed,
                    colorUsed,
                    placeCol,
                    solutions,
                    maxSolutions);
                colUsed[col] = false;
                colorUsed[color] = false;
            }
        }

        /// <summary>与已放置猴八邻接触则非法。</summary>
        private static bool TouchesPrevious(int row, int col, int[] placeCol)
        {
            for (var r = 0; r < row; r++)
            {
                var c = placeCol[r];
                var dr = row - r;
                var dc = col - c;
                if (dc < 0)
                {
                    dc = -dc;
                }

                if (dr <= 1 && dc <= 1)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
