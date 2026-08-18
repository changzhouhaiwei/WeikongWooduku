using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Wooduku.LevelEditor
{
    /// <summary>
    /// 从玩法截图识别 N×N 色区：采样格子色 → K-Means → 映射到现有色板最近色。
    /// </summary>
    public static class WoodukuBoardImageRecognizer
    {
        public sealed class CropNorm
        {
            public float Left = 0.08f;
            public float Right = 0.08f;
            public float Top = 0.38f;
            public float Bottom = 0.18f;
        }

        public sealed class Result
        {
            public bool Ok;
            public string Error;
            /// <summary>palette slot index per cell, length size*size。</summary>
            public int[] Regions;
            public Color[] CellSamples;
            public Color[] ClusterCentroids;
            public int[] ClusterToPaletteSlot;
            public RectInt BoardPixels;
        }

        public static Texture2D LoadTexture(string absolutePath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                error = "图片不存在。";
                return null;
            }

            var bytes = File.ReadAllBytes(absolutePath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                error = "无法解码图片（支持 png/jpg）。";
                return null;
            }

            return tex;
        }

        /// <summary>
        /// 在竖屏中部搜索「色块密度最高」的正方形窗口，作为棋盘裁剪（避开顶栏规则区与底栏按钮）。
        /// </summary>
        public static CropNorm AutoDetectCrop(Texture2D tex)
        {
            var w = tex.width;
            var h = tex.height;
            var pixels = tex.GetPixels32();
            var bg = AverageCorners(pixels, w, h);

            // 积分图：tile 像素 = 1
            var integral = new int[(w + 1) * (h + 1)];
            var tileCount = 0;
            for (var y = 0; y < h; y++)
            {
                var rowSum = 0;
                for (var x = 0; x < w; x++)
                {
                    var c = (Color)pixels[y * w + x];
                    Color.RGBToHSV(c, out _, out var s, out var v);
                    var dist = ColorDistance(c, bg);
                    var isTile = dist > 0.10f && s > 0.14f && v > 0.28f && v < 0.93f ? 1 : 0;
                    tileCount += isTile;
                    rowSum += isTile;
                    var idx = (y + 1) * (w + 1) + (x + 1);
                    integral[idx] = integral[y * (w + 1) + (x + 1)] + rowSum;
                }
            }

            var crop = DefaultMobileCrop();
            if (tileCount < 200)
            {
                return crop;
            }

            // 显示坐标搜索带：避开顶部 UI/规则、底部按钮（y_display 从上往下）
            var bandTop = Mathf.RoundToInt(h * 0.30f);
            var bandBottom = Mathf.RoundToInt(h * 0.88f);
            var bandLeft = Mathf.RoundToInt(w * 0.02f);
            var bandRight = Mathf.RoundToInt(w * 0.98f);

            // side 约等于屏宽的 70%~96%
            var sideMin = Mathf.Max(64, Mathf.RoundToInt(w * 0.55f));
            var sideMax = Mathf.Min(bandRight - bandLeft, bandBottom - bandTop);
            sideMax = Mathf.Min(sideMax, Mathf.RoundToInt(w * 0.98f));

            var bestScore = -1f;
            var bestX = 0;
            var bestDispY = bandTop;
            var bestSide = sideMin;
            var step = Mathf.Max(4, w / 80);

            for (var side = sideMax; side >= sideMin; side -= step)
            {
                for (var dispY = bandTop; dispY + side <= bandBottom; dispY += step)
                {
                    for (var x = bandLeft; x + side <= bandRight; x += step)
                    {
                        // 显示 y → tex y：窗口 [dispY, dispY+side) → tex [h-dispY-side, h-dispY)
                        var texY0 = h - (dispY + side);
                        var texY1 = h - dispY; // exclusive top in tex+1 sense for integral
                        var sum = RectSum(integral, w, x, texY0, x + side, texY1);
                        var density = sum / (float)(side * side);
                        // 偏好更接近方形满铺、略偏垂直居中
                        var centerY = (dispY + side * 0.5f) / h;
                        var centerBias = 1f - Mathf.Abs(centerY - 0.55f) * 0.35f;
                        var sizeBias = side / (float)w;
                        var score = density * (0.75f + 0.25f * sizeBias) * centerBias;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestDispY = dispY;
                            bestSide = side;
                        }
                    }
                }
            }

            if (bestScore < 0.12f)
            {
                return crop;
            }

            // 微扩一点，吃进圆角
            var pad = Mathf.Max(2, bestSide / 80);
            var left = Mathf.Clamp(bestX - pad, 0, w - 2);
            var right = Mathf.Clamp(bestX + bestSide + pad, left + 2, w);
            var top = Mathf.Clamp(bestDispY - pad, 0, h - 2);
            var bottom = Mathf.Clamp(bestDispY + bestSide + pad, top + 2, h);

            crop.Left = left / (float)w;
            crop.Right = 1f - right / (float)w;
            crop.Top = top / (float)h;
            crop.Bottom = 1f - bottom / (float)h;
            return crop;
        }

        public static CropNorm DefaultMobileCrop()
        {
            return new CropNorm
            {
                Left = 0.06f,
                Right = 0.06f,
                Top = 0.40f,
                Bottom = 0.18f
            };
        }

        private static int RectSum(int[] integral, int w, int x0, int y0, int x1, int y1)
        {
            // 半开区间 [x0,x1) × [y0,y1)，integral 尺寸 (w+1)*(h+1)
            var stride = w + 1;
            x0 = Mathf.Max(0, x0);
            y0 = Mathf.Max(0, y0);
            return integral[y1 * stride + x1]
                   - integral[y0 * stride + x1]
                   - integral[y1 * stride + x0]
                   + integral[y0 * stride + x0];
        }

        public static RectInt CropToPixels(Texture2D tex, CropNorm crop)
        {
            var w = tex.width;
            var h = tex.height;
            var x0 = Mathf.Clamp(Mathf.RoundToInt(crop.Left * w), 0, w - 2);
            var x1 = Mathf.Clamp(Mathf.RoundToInt(w * (1f - crop.Right)) - 1, x0 + 1, w - 1);
            // 显示坐标：top/bottom → tex y（底部为 0）
            var displayY0 = Mathf.Clamp(Mathf.RoundToInt(crop.Top * h), 0, h - 2);
            var displayY1 = Mathf.Clamp(Mathf.RoundToInt(h * (1f - crop.Bottom)) - 1, displayY0 + 1, h - 1);
            var texY1 = h - 1 - displayY0; // 上边 → 较高 tex y
            var texY0 = h - 1 - displayY1; // 下边 → 较低 tex y
            if (texY0 > texY1)
            {
                var t = texY0;
                texY0 = texY1;
                texY1 = t;
            }

            return new RectInt(x0, texY0, x1 - x0 + 1, texY1 - texY0 + 1);
        }

        public static Result Recognize(
            Texture2D tex,
            CropNorm crop,
            int size,
            IReadOnlyList<Color> paletteColors,
            IReadOnlyList<int> paletteSlots)
        {
            var result = new Result();
            if (tex == null)
            {
                result.Error = "纹理为空。";
                return result;
            }

            if (size < 2 || size > 12)
            {
                result.Error = "边长无效。";
                return result;
            }

            if (paletteColors == null || paletteSlots == null || paletteColors.Count != size ||
                paletteSlots.Count != size)
            {
                result.Error = "需要恰好 N 个开启的色板颜色用于映射。";
                return result;
            }

            var board = CropToPixels(tex, crop);
            if (board.width < size * 4 || board.height < size * 4)
            {
                result.Error = "棋盘裁剪区域过小，请调整裁剪边距。";
                return result;
            }

            result.BoardPixels = board;
            var pixels = tex.GetPixels32();
            var w = tex.width;
            var cellColors = new Color[size * size];

            for (var r = 0; r < size; r++)
            {
                for (var c = 0; c < size; c++)
                {
                    // 行 r=0 为棋盘最上行 → 高 tex y
                    var cellW = board.width / (float)size;
                    var cellH = board.height / (float)size;
                    var cx0 = board.xMin + c * cellW;
                    var cy0 = board.yMax - (r + 1) * cellH;
                    cellColors[r * size + c] = SampleCell(pixels, w, cx0, cy0, cellW, cellH);
                }
            }

            result.CellSamples = cellColors;

            // 凝聚聚类：更能保留「只占 1 格」的稀有色区
            AgglomerativeCluster(cellColors, size, out var labels, out var centroids);
            result.ClusterCentroids = centroids;

            // 聚类 → 色板槽：贪心唯一最近匹配
            var clusterToSlot = AssignClustersToPalette(centroids, paletteColors, paletteSlots);
            result.ClusterToPaletteSlot = clusterToSlot;

            var regions = new int[size * size];
            for (var i = 0; i < regions.Length; i++)
            {
                regions[i] = clusterToSlot[labels[i]];
            }

            result.Regions = regions;
            result.Ok = true;
            return result;
        }

        private static Color SampleCell(Color32[] pixels, int w, float x0, float y0, float cellW, float cellH)
        {
            // 内缩，避开圆角边与缝；忽略过黑/过白（解图标）
            var insetX = cellW * 0.22f;
            var insetY = cellH * 0.22f;
            var xStart = Mathf.FloorToInt(x0 + insetX);
            var yStart = Mathf.FloorToInt(y0 + insetY);
            var xEnd = Mathf.CeilToInt(x0 + cellW - insetX);
            var yEnd = Mathf.CeilToInt(y0 + cellH - insetY);
            var h = pixels.Length / w;

            xStart = Mathf.Clamp(xStart, 0, w - 1);
            xEnd = Mathf.Clamp(xEnd, xStart + 1, w);
            yStart = Mathf.Clamp(yStart, 0, h - 1);
            yEnd = Mathf.Clamp(yEnd, yStart + 1, h);

            var rs = new List<float>(64);
            var gs = new List<float>(64);
            var bs = new List<float>(64);
            float fr = 0, fg = 0, fb = 0;
            var fallCount = 0;

            for (var y = yStart; y < yEnd; y++)
            {
                for (var x = xStart; x < xEnd; x++)
                {
                    var p = pixels[y * w + x];
                    var r = p.r / 255f;
                    var g = p.g / 255f;
                    var b = p.b / 255f;
                    fr += r;
                    fg += g;
                    fb += b;
                    fallCount++;

                    var c = new Color(r, g, b);
                    Color.RGBToHSV(c, out _, out var s, out var v);
                    if (v < 0.18f || v > 0.97f)
                    {
                        continue; // 图标黑白
                    }

                    if (s < 0.06f && v > 0.85f)
                    {
                        continue; // 近白底
                    }

                    rs.Add(r);
                    gs.Add(g);
                    bs.Add(b);
                }
            }

            if (rs.Count >= 8)
            {
                return new Color(Median(rs), Median(gs), Median(bs), 1f);
            }

            if (fallCount == 0)
            {
                return Color.gray;
            }

            return new Color(fr / fallCount, fg / fallCount, fb / fallCount, 1f);
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            var m = values.Count / 2;
            if ((values.Count & 1) == 1)
            {
                return values[m];
            }

            return (values[m - 1] + values[m]) * 0.5f;
        }

        /// <summary>
        /// 平均链接凝聚聚类，合并到恰好 k 簇；适合色区大小极不均匀的棋盘。
        /// </summary>
        private static void AgglomerativeCluster(Color[] samples, int k, out int[] labels, out Color[] centroids)
        {
            var n = samples.Length;
            labels = new int[n];
            if (n == 0)
            {
                centroids = Array.Empty<Color>();
                return;
            }

            // parent[i] = 簇代表；初始每格一簇
            var parent = new int[n];
            var members = new List<int>[n];
            for (var i = 0; i < n; i++)
            {
                parent[i] = i;
                members[i] = new List<int> { i };
            }

            var active = new List<int>(n);
            for (var i = 0; i < n; i++)
            {
                active.Add(i);
            }

            while (active.Count > k)
            {
                var bestA = 0;
                var bestB = 1;
                var bestD = float.MaxValue;
                for (var i = 0; i < active.Count; i++)
                {
                    for (var j = i + 1; j < active.Count; j++)
                    {
                        var a = active[i];
                        var b = active[j];
                        var d = AverageLinkage(samples, members[a], members[b]);
                        if (d < bestD)
                        {
                            bestD = d;
                            bestA = a;
                            bestB = b;
                        }
                    }
                }

                // 合并 bestB → bestA
                members[bestA].AddRange(members[bestB]);
                members[bestB] = null;
                active.Remove(bestB);
            }

            centroids = new Color[k];
            for (var ci = 0; ci < k; ci++)
            {
                var root = active[ci];
                var list = members[root];
                float sr = 0, sg = 0, sb = 0;
                for (var i = 0; i < list.Count; i++)
                {
                    var s = samples[list[i]];
                    sr += s.r;
                    sg += s.g;
                    sb += s.b;
                    labels[list[i]] = ci;
                }

                var inv = 1f / list.Count;
                centroids[ci] = new Color(sr * inv, sg * inv, sb * inv, 1f);
            }
        }

        private static float AverageLinkage(Color[] samples, List<int> a, List<int> b)
        {
            var sum = 0f;
            var count = 0;
            for (var i = 0; i < a.Count; i++)
            {
                for (var j = 0; j < b.Count; j++)
                {
                    sum += ColorDistance(samples[a[i]], samples[b[j]]);
                    count++;
                }
            }

            return count == 0 ? float.MaxValue : sum / count;
        }

        private static int[] AssignClustersToPalette(
            Color[] centroids,
            IReadOnlyList<Color> paletteColors,
            IReadOnlyList<int> paletteSlots)
        {
            var k = centroids.Length;
            var map = new int[k];
            // 按「最近距离」全局贪心：反复选最小未用 pair
            var pairs = new List<ClusterPalPair>(k * k);
            for (var c = 0; c < k; c++)
            {
                for (var p = 0; p < k; p++)
                {
                    pairs.Add(new ClusterPalPair(c, p, ColorDistance(centroids[c], paletteColors[p])));
                }
            }

            pairs.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            var assignedCluster = new bool[k];
            var assignedPal = new bool[k];
            var left = k;
            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (assignedCluster[pair.Cluster] || assignedPal[pair.PalIdx])
                {
                    continue;
                }

                map[pair.Cluster] = paletteSlots[pair.PalIdx];
                assignedCluster[pair.Cluster] = true;
                assignedPal[pair.PalIdx] = true;
                left--;
                if (left == 0)
                {
                    break;
                }
            }

            // 兜底
            for (var c = 0; c < k; c++)
            {
                if (assignedCluster[c])
                {
                    continue;
                }

                for (var p = 0; p < k; p++)
                {
                    if (!assignedPal[p])
                    {
                        map[c] = paletteSlots[p];
                        assignedPal[p] = true;
                        assignedCluster[c] = true;
                        break;
                    }
                }
            }

            return map;
        }

        public static float ColorDistance(Color a, Color b)
        {
            // 加重色相，便于区分棕/金/粉等相近木色
            var dr = a.r - b.r;
            var dg = a.g - b.g;
            var db = a.b - b.b;
            Color.RGBToHSV(a, out var ha, out var sa, out var va);
            Color.RGBToHSV(b, out var hb, out var sb, out var vb);
            var dh = Mathf.Abs(ha - hb);
            if (dh > 0.5f)
            {
                dh = 1f - dh;
            }

            // 低饱和时色相不可靠，降低 hue 权重
            var hueW = 2.2f * Mathf.Min(sa, sb);
            var ds = sa - sb;
            var dv = va - vb;
            return dr * dr * 0.7f + dg * dg * 0.7f + db * db * 0.7f
                   + dh * dh * hueW + ds * ds * 0.9f + dv * dv * 0.4f;
        }

        private static Color AverageCorners(Color32[] pixels, int w, int h)
        {
            Color Acc(int x, int y) => (Color)pixels[y * w + x];
            return (Acc(2, 2) + Acc(w - 3, 2) + Acc(2, h - 3) + Acc(w - 3, h - 3)) * 0.25f;
        }

        private readonly struct ClusterPalPair
        {
            public readonly int Cluster;
            public readonly int PalIdx;
            public readonly float Dist;

            public ClusterPalPair(int cluster, int palIdx, float dist)
            {
                Cluster = cluster;
                PalIdx = palIdx;
                Dist = dist;
            }
        }
    }
}
