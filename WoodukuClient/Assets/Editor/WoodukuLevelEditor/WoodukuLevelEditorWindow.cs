using System.Collections.Generic;
using System.IO;
using GameLogic.Wooduku;
using UnityEditor;
using UnityEngine;

namespace Wooduku.LevelEditor
{
    /// <summary>
    /// Wooduku 关卡编辑器：设定 N、色板存档、格子涂色、唯一解检测、导出 JSON。
    /// 菜单：自定义窗口 / Wooduku 关卡编辑器
    /// </summary>
    public sealed class WoodukuLevelEditorWindow : EditorWindow
    {
        private const string DefaultLevelDir = "Assets/GameRes/WoodukuLevels";
        private const string DefaultPalettePath = "Assets/GameRes/WoodukuLevels/WoodukuColorPalette.asset";
        private const string LastOpenedLevelKey = "Wooduku.LevelEditor.LastOpenedLevelPath";

        private int _levelId = 1;
        private int _size = 4;
        private int _hintCount = 5;
        private WoodukuLevelDifficulty _difficulty;
        private int _difficultyScore;
        private string _sourceName = string.Empty;
        private WoodukuCellRef[] _fixedQueenCells = System.Array.Empty<WoodukuCellRef>();
        private int[] _regions;
        private int _paintColorIndex;
        private WoodukuColorPaletteAsset _palette;
        private Vector2 _scroll;
        private Vector2 _boardScroll;
        private string _status = "就绪。标定色区后点击「检测固有解」。";
        private MessageType _statusType = MessageType.Info;
        private WoodukuLevelSolver.Result _lastResult;
        private bool _showSolutionOnBoard = true;
        private float _cellPx = 36f;
        private bool _isDraggingPaint;
        private string _exportPath;
        private int _levelCount;
        private int _maxLevelId;
        private WoodukuLevelCatalogAsset _packedCatalog;
        private WoodukuLevelPackAsset _activePack;

        // 识图
        private string _imagePath = @"F:\SelfMaj\FishWooduku\WoodukuDoc\UIGame.jpg";
        private Texture2D _imagePreview;
        private WoodukuBoardImageRecognizer.CropNorm _crop = WoodukuBoardImageRecognizer.DefaultMobileCrop();
        private bool _autoValidateAfterRecognize = true;
        private Vector2 _imagePreviewScroll;

        private void OnDestroy()
        {
            DestroyPreviewTexture();
        }

        [MenuItem("自定义窗口/Wooduku 关卡编辑器", priority = 80)]
        private static void Open()
        {
            var win = GetWindow<WoodukuLevelEditorWindow>();
            win.titleContent = new GUIContent("Wooduku 关卡");
            win.minSize = new Vector2(720, 560);
            win.Show();
        }

        private void OnEnable()
        {
            EnsurePalette();
            EnsureBoard(_size);
            _packedCatalog = AssetDatabase.LoadAssetAtPath<WoodukuLevelCatalogAsset>(
                WoodukuLevelRepository.CatalogPath);
            RefreshLevelStats();
            _exportPath = Path.Combine(DefaultLevelDir, $"level_{_levelId:D3}.json").Replace('\\', '/');

            if (_packedCatalog != null && TryLoadPackedLevel(_levelId))
            {
                return;
            }

            var lastOpenedPath = EditorPrefs.GetString(LastOpenedLevelKey, string.Empty);
            if (!string.IsNullOrEmpty(lastOpenedPath))
            {
                ImportJsonFromPath(lastOpenedPath, false);
            }
        }

        private void OnGUI()
        {
            EnsurePalette();
            EnsureBoard(_size);
            HandleKeyboardShortcuts();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeader();
            EditorGUILayout.Space(8);
            DrawPaletteSection();
            EditorGUILayout.Space(8);
            DrawRecognizeSection();
            EditorGUILayout.Space(8);
            DrawBoardSection();
            EditorGUILayout.Space(8);
            DrawValidateSection();

            EditorGUILayout.EndScrollView();
        }

        private void HandleKeyboardShortcuts()
        {
            var current = Event.current;
            if (current.type != EventType.KeyDown ||
                current.keyCode != KeyCode.S ||
                (!current.control && !current.command))
            {
                return;
            }

            SaveCurrentLevel();
            current.Use();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("关卡基本信息", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"总关卡数：{_levelCount}", GUILayout.Width(90));

                EditorGUILayout.LabelField("关卡 ID", GUILayout.Width(48));
                EditorGUI.BeginChangeCheck();
                var newLevelId = EditorGUILayout.IntField(_levelId, GUILayout.Width(56));
                if (EditorGUI.EndChangeCheck())
                {
                    _levelId = Mathf.Max(1, newLevelId);
                    _exportPath = BuildLevelPath(_levelId);
                }

                if (_packedCatalog != null && GUILayout.Button("跳转", GUILayout.Width(52)))
                {
                    TryLoadPackedLevel(_levelId);
                }

                if (GUILayout.Button("新建关卡", GUILayout.Width(90)))
                {
                    CreateNewLevel();
                }

                if (GUILayout.Button("导入 JSON", GUILayout.Width(90)))
                {
                    ImportJson();
                }

                if (GUILayout.Button(_packedCatalog != null ? "保存关卡" : "导出 JSON", GUILayout.Width(90)))
                {
                    SaveCurrentLevel();
                }

                GUI.enabled = _levelId > 1;
                if (GUILayout.Button("上一关", GUILayout.Width(70)))
                {
                    OpenAdjacentLevel(-1);
                }

                GUI.enabled = true;
                if (GUILayout.Button("下一关", GUILayout.Width(70)))
                {
                    OpenAdjacentLevel(1);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("提示次数", GUILayout.Width(52));
                _hintCount = Mathf.Max(0, EditorGUILayout.IntField(_hintCount, GUILayout.Width(48)));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _difficulty = (WoodukuLevelDifficulty)EditorGUILayout.EnumPopup(
                    "难度标识", _difficulty, GUILayout.Width(260));
                _difficultyScore = EditorGUILayout.IntField(
                    "难度分", _difficultyScore, GUILayout.Width(180));
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(_sourceName) ? "原创关卡" : $"来源：{_sourceName}");
            }

            EditorGUI.BeginChangeCheck();
            var newSize = EditorGUILayout.IntSlider("边长 N（N×N）", _size, 2, 12);
            if (EditorGUI.EndChangeCheck() && newSize != _size)
            {
                _fixedQueenCells = System.Array.Empty<WoodukuCellRef>();
                _sourceName = string.Empty;
                ResizeBoard(newSize);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空棋盘", GUILayout.Width(100)))
                {
                    for (var i = 0; i < _regions.Length; i++)
                    {
                        _regions[i] = -1;
                    }

                    _lastResult = null;
                    SetStatus("已清空棋盘。", MessageType.Info);
                }

                if (GUILayout.Button("按行填充 0..N-1（调试）", GUILayout.Width(160)))
                {
                    FillDebugRows();
                }

                if (GUILayout.Button("按颜色数随机生成关卡", GUILayout.Width(170)))
                {
                    GenerateRandomLevel();
                }
            }
        }

        private void DrawPaletteSection()
        {
            EditorGUILayout.LabelField("颜色区（存档 / 开启）", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _palette = (WoodukuColorPaletteAsset)EditorGUILayout.ObjectField(
                "色板资源", _palette, typeof(WoodukuColorPaletteAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                EnsurePalette();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("新建色板资源", GUILayout.Width(120)))
                {
                    CreatePaletteAsset();
                }

                if (GUILayout.Button("保存色板", GUILayout.Width(100)))
                {
                    EditorUtility.SetDirty(_palette);
                    AssetDatabase.SaveAssets();
                    SetStatus($"色板已保存：{AssetDatabase.GetAssetPath(_palette)}", MessageType.Info);
                }

                if (GUILayout.Button("补齐默认色到 N", GUILayout.Width(130)))
                {
                    _palette.EnsureDefaults(_size);
                    EditorUtility.SetDirty(_palette);
                }

                if (GUILayout.Button("添加颜色", GUILayout.Width(80)))
                {
                    var i = _palette.slots.Count;
                    _palette.slots.Add(new WoodukuColorPaletteAsset.Slot
                    {
                        name = $"色{i}",
                        color = WoodukuColorPaletteAsset.DefaultColors[i % WoodukuColorPaletteAsset.DefaultColors.Length],
                        enabled = true
                    });
                    EditorUtility.SetDirty(_palette);
                }
            }

            var enabled = _palette.GetEnabledIndices();
            EditorGUILayout.HelpBox(
                $"已开启 {enabled.Count} 种颜色；检测固有解时须恰好等于 N={_size}。\n点击下方色块设为画笔，再在棋盘上单击/拖拽标定颜色区。",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                for (var i = 0; i < _palette.slots.Count; i++)
                {
                    var slot = _palette.slots[i];
                    if (slot == null)
                    {
                        continue;
                    }

                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(72)))
                    {
                        var wasEnabled = slot.enabled;
                        slot.enabled = EditorGUILayout.ToggleLeft("开", slot.enabled, GUILayout.Width(70));
                        if (wasEnabled != slot.enabled)
                        {
                            EditorUtility.SetDirty(_palette);
                        }

                        var style = new GUIStyle(GUI.skin.button);
                        if (i == _paintColorIndex)
                        {
                            style.fontStyle = FontStyle.Bold;
                        }

                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = slot.enabled ? slot.color : Color.Lerp(slot.color, Color.gray, 0.6f);
                        if (GUILayout.Button($"{i}", style, GUILayout.Height(28), GUILayout.Width(70)))
                        {
                            _paintColorIndex = i;
                        }

                        GUI.backgroundColor = prev;

                        slot.name = EditorGUILayout.TextField(slot.name, GUILayout.Width(70));
                        slot.color = EditorGUILayout.ColorField(GUIContent.none, slot.color, false, false, false,
                            GUILayout.Width(70), GUILayout.Height(18));
                    }
                }
            }

            // 画笔映射：palette 槽位 index → 导出时的逻辑色 id（仅 enabled 按顺序重映射 0..N-1）
            EditorGUILayout.LabelField($"当前画笔：槽位 {_paintColorIndex}" +
                                       (_paintColorIndex >= 0 && _paintColorIndex < _palette.slots.Count
                                           ? $"（{_palette.slots[_paintColorIndex].name}）"
                                           : ""));
        }

        private void DrawRecognizeSection()
        {
            EditorGUILayout.LabelField("识图导入", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "从玩法截图识别色区，写入当前棋盘。颜色按「现有已开启色板」最近匹配，不要求像素色完全一致。识别前请把 N 设成与截图一致（如 UIGame 为 6）。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                _imagePath = EditorGUILayout.TextField("图片路径", _imagePath);
                if (GUILayout.Button("浏览…", GUILayout.Width(60)))
                {
                    var start = string.IsNullOrEmpty(_imagePath)
                        ? Application.dataPath
                        : Path.GetDirectoryName(_imagePath);
                    var picked = EditorUtility.OpenFilePanel("选择关卡截图", start ?? "", "jpg,jpeg,png");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _imagePath = picked;
                        ReloadPreview();
                    }
                }

                if (GUILayout.Button("加载预览", GUILayout.Width(80)))
                {
                    ReloadPreview();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("自动框选棋盘", GUILayout.Width(120)))
                {
                    EnsurePreviewLoaded();
                    if (_imagePreview != null)
                    {
                        _crop = WoodukuBoardImageRecognizer.AutoDetectCrop(_imagePreview);
                        SetStatus(
                            $"自动裁剪：L={_crop.Left:F2} R={_crop.Right:F2} T={_crop.Top:F2} B={_crop.Bottom:F2}",
                            MessageType.Info);
                    }
                }

                _autoValidateAfterRecognize = EditorGUILayout.ToggleLeft("识别后自动检测固有解",
                    _autoValidateAfterRecognize, GUILayout.Width(180));
            }

            _crop.Left = EditorGUILayout.Slider("裁左", _crop.Left, 0f, 0.45f);
            _crop.Right = EditorGUILayout.Slider("裁右", _crop.Right, 0f, 0.45f);
            _crop.Top = EditorGUILayout.Slider("裁上", _crop.Top, 0f, 0.6f);
            _crop.Bottom = EditorGUILayout.Slider("裁下", _crop.Bottom, 0f, 0.5f);

            if (_imagePreview != null)
            {
                DrawImagePreviewWithCrop();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("识别并写入当前配置", GUILayout.Height(30)))
                {
                    RecognizeFromImage();
                }

                if (GUILayout.Button("识别 UIGame.jpg", GUILayout.Height(30), GUILayout.Width(140)))
                {
                    _imagePath = @"F:\SelfMaj\FishWooduku\WoodukuDoc\UIGame.jpg";
                    ReloadPreview();
                    if (_imagePreview != null)
                    {
                        _crop = WoodukuBoardImageRecognizer.AutoDetectCrop(_imagePreview);
                    }

                    if (_size != 6)
                    {
                        ResizeBoard(6);
                    }

                    EnsureEnabledPaletteCount(_size);
                    RecognizeFromImage();
                }
            }
        }

        private void DrawImagePreviewWithCrop()
        {
            const float maxW = 280f;
            var aspect = _imagePreview.height / (float)_imagePreview.width;
            var w = maxW;
            var h = maxW * aspect;
            _imagePreviewScroll = EditorGUILayout.BeginScrollView(_imagePreviewScroll, GUILayout.Height(Mathf.Min(h + 8f, 320f)));
            var rect = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, _imagePreview, ScaleMode.StretchToFill);

            // 裁剪框（显示坐标：Top 从上往下）
            var x = rect.x + rect.width * _crop.Left;
            var y = rect.y + rect.height * _crop.Top;
            var rw = rect.width * (1f - _crop.Left - _crop.Right);
            var rh = rect.height * (1f - _crop.Top - _crop.Bottom);
            var cropRect = new Rect(x, y, rw, rh);
            Handles.BeginGUI();
            Handles.color = Color.red;
            Handles.DrawSolidRectangleWithOutline(cropRect, new Color(1f, 0f, 0f, 0.08f), Color.red);
            Handles.EndGUI();

            // 格子辅助线
            Handles.BeginGUI();
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            for (var i = 1; i < _size; i++)
            {
                var lx = cropRect.x + cropRect.width * i / _size;
                var ly = cropRect.y + cropRect.height * i / _size;
                Handles.DrawLine(new Vector3(lx, cropRect.y), new Vector3(lx, cropRect.yMax));
                Handles.DrawLine(new Vector3(cropRect.x, ly), new Vector3(cropRect.xMax, ly));
            }

            Handles.EndGUI();
            EditorGUILayout.EndScrollView();
        }

        private void ReloadPreview()
        {
            DestroyPreviewTexture();
            _imagePreview = WoodukuBoardImageRecognizer.LoadTexture(_imagePath, out var err);
            if (_imagePreview == null)
            {
                SetStatus("预览失败：" + err, MessageType.Error);
            }
            else
            {
                SetStatus($"已加载预览 {_imagePreview.width}×{_imagePreview.height}", MessageType.Info);
            }

            Repaint();
        }

        private void EnsurePreviewLoaded()
        {
            if (_imagePreview == null)
            {
                ReloadPreview();
            }
        }

        private void DestroyPreviewTexture()
        {
            if (_imagePreview != null)
            {
                DestroyImmediate(_imagePreview);
                _imagePreview = null;
            }
        }

        private void RecognizeFromImage()
        {
            EnsurePalette();
            EnsureEnabledPaletteCount(_size);
            EnsurePreviewLoaded();
            if (_imagePreview == null)
            {
                return;
            }

            var enabled = _palette.GetEnabledIndices();
            enabled.Sort();
            if (enabled.Count != _size)
            {
                SetStatus($"识图需要恰好开启 {_size} 种颜色（当前 {enabled.Count}）。", MessageType.Error);
                return;
            }

            var colors = new List<Color>(_size);
            for (var i = 0; i < enabled.Count; i++)
            {
                colors.Add(_palette.slots[enabled[i]].color);
            }

            var result = WoodukuBoardImageRecognizer.Recognize(
                _imagePreview, _crop, _size, colors, enabled);
            if (!result.Ok)
            {
                SetStatus("识图失败：" + result.Error, MessageType.Error);
                return;
            }

            EnsureBoard(_size);
            for (var i = 0; i < result.Regions.Length; i++)
            {
                _regions[i] = result.Regions[i];
            }

            _lastResult = null;
            EditorUtility.SetDirty(_palette);

            var msg =
                $"识图完成：已写入 {_size}×{_size} 色区（映射到最近色板）。棋盘像素区 {result.BoardPixels.width}×{result.BoardPixels.height}。";
            SetStatus(msg, MessageType.Info);

            if (_autoValidateAfterRecognize)
            {
                RunValidate();
                if (_statusType != MessageType.Error)
                {
                    SetStatus(msg + " " + _status, _statusType);
                }
            }

            Repaint();
        }

        /// <summary>保证前 N 个色槽开启，其余可保持原状；若开启数不足则自动开启。</summary>
        private void EnsureEnabledPaletteCount(int n)
        {
            _palette.EnsureDefaults(n);
            var enabled = _palette.GetEnabledIndices();
            if (enabled.Count == n)
            {
                return;
            }

            if (enabled.Count > n)
            {
                // 只保留最小的 N 个开启槽
                enabled.Sort();
                for (var i = 0; i < _palette.slots.Count; i++)
                {
                    _palette.slots[i].enabled = false;
                }

                for (var i = 0; i < n; i++)
                {
                    _palette.slots[enabled[i]].enabled = true;
                }
            }
            else
            {
                for (var i = 0; i < _palette.slots.Count && enabled.Count < n; i++)
                {
                    if (!_palette.slots[i].enabled)
                    {
                        _palette.slots[i].enabled = true;
                        enabled.Add(i);
                    }
                }
            }

            EditorUtility.SetDirty(_palette);
        }

        private void DrawBoardSection()
        {
            EditorGUILayout.LabelField("棋盘标定", EditorStyles.boldLabel);
            _cellPx = EditorGUILayout.Slider("格子大小", _cellPx, 22f, 56f);
            _showSolutionOnBoard = EditorGUILayout.Toggle("显示检测到的解（猴）", _showSolutionOnBoard);

            var boardW = _size * (_cellPx + 2f) + 8f;
            var boardH = _size * (_cellPx + 2f) + 8f;
            _boardScroll = EditorGUILayout.BeginScrollView(_boardScroll, GUILayout.Height(Mathf.Min(boardH + 20f, 420f)));
            var rect = GUILayoutUtility.GetRect(boardW, boardH);

            HandleBoardInput(rect);
            DrawBoard(rect);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("左键涂色 / 拖拽连涂；右键清除格子为未定义。", EditorStyles.miniLabel);
        }

        private void DrawBoard(Rect origin)
        {
            EditorGUI.DrawRect(origin, new Color(0.15f, 0.15f, 0.15f, 0.35f));

            int[] solCols = null;
            if (_showSolutionOnBoard && _lastResult != null && _lastResult.SolutionCount >= 1)
            {
                solCols = _lastResult.FirstSolutionCols;
            }

            for (var r = 0; r < _size; r++)
            {
                for (var c = 0; c < _size; c++)
                {
                    var cell = CellRect(origin, r, c);
                    var region = _regions[r * _size + c];
                    var fill = new Color(0.25f, 0.25f, 0.25f, 0.9f);
                    var label = "";
                    if (region >= 0 && region < _palette.slots.Count && _palette.slots[region] != null)
                    {
                        fill = _palette.slots[region].color;
                        label = region.ToString();
                    }
                    else if (region >= 0)
                    {
                        fill = Color.magenta;
                        label = "?";
                    }

                    EditorGUI.DrawRect(cell, fill);
                    EditorGUI.DrawRect(new Rect(cell.x, cell.y, cell.width, 1f), Color.black);
                    EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), Color.black);
                    EditorGUI.DrawRect(new Rect(cell.x, cell.y, 1f, cell.height), Color.black);
                    EditorGUI.DrawRect(new Rect(cell.xMax - 1f, cell.y, 1f, cell.height), Color.black);

                    var isSol = solCols != null && solCols[r] == c;
                    var isFixed = IsFixedQueen(r, c);
                    var text = isFixed ? "锁" : isSol ? "猴" : label;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var style = new GUIStyle(EditorStyles.boldLabel)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            normal = { textColor = isSol || isFixed ? Color.white : Color.black }
                        };
                        GUI.Label(cell, text, style);
                    }
                }
            }
        }

        private void HandleBoardInput(Rect origin)
        {
            var e = Event.current;
            if (e == null || !origin.Contains(e.mousePosition))
            {
                if (e != null && e.type == EventType.MouseUp)
                {
                    _isDraggingPaint = false;
                }

                return;
            }

            if (!TryPickCell(origin, e.mousePosition, out var r, out var c))
            {
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                PaintCell(r, c, _paintColorIndex);
                _isDraggingPaint = true;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && _isDraggingPaint)
            {
                PaintCell(r, c, _paintColorIndex);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDown && e.button == 1)
            {
                PaintCell(r, c, -1);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp)
            {
                _isDraggingPaint = false;
            }
        }

        private Rect CellRect(Rect origin, int r, int c)
        {
            return new Rect(
                origin.x + 4f + c * (_cellPx + 2f),
                origin.y + 4f + r * (_cellPx + 2f),
                _cellPx,
                _cellPx);
        }

        private bool TryPickCell(Rect origin, Vector2 mouse, out int r, out int c)
        {
            r = -1;
            c = -1;
            var local = mouse - new Vector2(origin.x + 4f, origin.y + 4f);
            if (local.x < 0 || local.y < 0)
            {
                return false;
            }

            c = Mathf.FloorToInt(local.x / (_cellPx + 2f));
            r = Mathf.FloorToInt(local.y / (_cellPx + 2f));
            return r >= 0 && r < _size && c >= 0 && c < _size;
        }

        private void PaintCell(int r, int c, int paletteSlot)
        {
            if (paletteSlot >= 0)
            {
                if (paletteSlot >= _palette.slots.Count || !_palette.slots[paletteSlot].enabled)
                {
                    SetStatus("当前画笔颜色未开启，请先勾选「开」。", MessageType.Warning);
                    return;
                }
            }

            _regions[r * _size + c] = paletteSlot;
            _lastResult = null;
        }

        private void DrawValidateSection()
        {
            EditorGUILayout.LabelField("检测固有解", EditorStyles.boldLabel);
            if (GUILayout.Button("检测是否固有解（唯一解）", GUILayout.Height(28)))
            {
                RunValidate();
            }

            EditorGUILayout.HelpBox(_status, _statusType);

            if (_lastResult != null && _lastResult.BoardValid)
            {
                EditorGUILayout.LabelField($"解数量：{_lastResult.SolutionCount}");
                if (_lastResult.SolutionCount >= 1)
                {
                    EditorGUILayout.LabelField(
                        "解（行,列）：" + WoodukuLevelSolver.FormatSolution(_size, _lastResult.FirstSolutionCols));
                }
            }
        }

        private void RunValidate()
        {
            var map = BuildExportColorMap(out var enabledSlots, out var err);
            if (map == null)
            {
                _lastResult = null;
                SetStatus(err, MessageType.Error);
                return;
            }

            var logical = new int[_regions.Length];
            for (var i = 0; i < _regions.Length; i++)
            {
                var slot = _regions[i];
                if (slot < 0)
                {
                    logical[i] = -1;
                    continue;
                }

                if (!map.TryGetValue(slot, out var lid))
                {
                    SetStatus($"格子使用了未开启颜色槽位 {slot}。", MessageType.Error);
                    _lastResult = null;
                    return;
                }

                logical[i] = lid;
            }

            _lastResult = WoodukuLevelSolver.Analyze(
                _size, logical, enabledSlots.Count, _fixedQueenCells);
            if (!_lastResult.BoardValid)
            {
                SetStatus("棋盘无效：" + _lastResult.BoardError, MessageType.Error);
                return;
            }

            if (_lastResult.HasUniqueSolution)
            {
                SetStatus(
                    $"固有解：唯一解。{WoodukuLevelSolver.FormatSolution(_size, _lastResult.FirstSolutionCols)}",
                    MessageType.Info);
            }
            else if (_lastResult.SolutionCount == 0)
            {
                SetStatus("无解：不存在满足规则的解方块集合。", MessageType.Warning);
            }
            else
            {
                SetStatus($"非固有解：找到至少 {_lastResult.SolutionCount} 组解（已停止继续搜索）。", MessageType.Warning);
            }

            Repaint();
        }

        /// <summary>
        /// 开启的色板槽位按槽位序号排序，映射为逻辑色 id 0..K-1。
        /// 棋盘上只允许使用这些槽位；K 须等于 N。
        /// </summary>
        private Dictionary<int, int> BuildExportColorMap(out List<int> enabledSlots, out string error)
        {
            error = null;
            enabledSlots = _palette.GetEnabledIndices();
            enabledSlots.Sort();
            if (enabledSlots.Count != _size)
            {
                error = $"已开启颜色数={enabledSlots.Count}，须等于 N={_size}。请在色板中开启/关闭颜色。";
                return null;
            }

            var map = new Dictionary<int, int>();
            for (var i = 0; i < enabledSlots.Count; i++)
            {
                map[enabledSlots[i]] = i;
            }

            return map;
        }

        private void SaveCurrentLevel()
        {
            if (_packedCatalog == null)
            {
                ExportJson(requireUnique: true);
                return;
            }

            if (!TryBuildCurrentLevelFile(requireUnique: true, out var file))
            {
                return;
            }

            var entry = _packedCatalog.FindPack(_levelId);
            if (entry == null)
            {
                SetStatus($"无法保存：关卡 {_levelId} 不在分包目录中。", MessageType.Error);
                return;
            }

            var pack = AssetDatabase.LoadAssetAtPath<WoodukuLevelPackAsset>(entry.assetPath);
            var index = _levelId - entry.firstLevelId;
            if (pack == null || index < 0 || index >= pack.levels.Count)
            {
                SetStatus($"无法保存：分包资源无效 {entry.assetPath}。", MessageType.Error);
                return;
            }

            pack.levels[index] = file;
            _activePack = pack;
            EditorUtility.SetDirty(pack);
            AssetDatabase.SaveAssets();
            SetStatus($"已保存关卡 {_levelId}：{entry.assetPath}", MessageType.Info);
        }

        private bool TryBuildCurrentLevelFile(bool requireUnique, out WoodukuLevelFile file)
        {
            file = null;
            if (_lastResult == null || !_lastResult.BoardValid)
            {
                RunValidate();
            }

            if (_lastResult == null || !_lastResult.BoardValid)
            {
                SetStatus("无法保存：棋盘未通过校验。", MessageType.Error);
                return false;
            }

            if (requireUnique && !_lastResult.HasUniqueSolution)
            {
                SetStatus("无法保存：不是固有解（唯一解）。", MessageType.Error);
                return false;
            }

            var map = BuildExportColorMap(out var enabledSlots, out var err);
            if (map == null)
            {
                SetStatus(err, MessageType.Error);
                return false;
            }

            var logical = new int[_regions.Length];
            for (var i = 0; i < _regions.Length; i++)
            {
                logical[i] = map[_regions[i]];
            }

            var colors = new WoodukuColorEntry[enabledSlots.Count];
            for (var i = 0; i < enabledSlots.Count; i++)
            {
                var slot = _palette.slots[enabledSlots[i]];
                colors[i] = new WoodukuColorEntry
                {
                    id = i,
                    name = slot.name,
                    hex = WoodukuLevelJson.ColorToHex(slot.color),
                    enabled = true
                };
            }

            var cells = new WoodukuCellRef[_size];
            for (var r = 0; r < _size; r++)
            {
                cells[r] = new WoodukuCellRef { r = r, c = _lastResult.FirstSolutionCols[r] };
            }

            file = new WoodukuLevelFile
            {
                id = _levelId,
                size = _size,
                hintCount = _hintCount,
                difficulty = _difficulty,
                difficultyScore = _difficultyScore,
                sourceName = _sourceName,
                hasUniqueSolution = _lastResult.HasUniqueSolution,
                solutionCount = _lastResult.SolutionCount,
                colors = colors,
                regions = logical,
                solutionCols = _lastResult.FirstSolutionCols,
                solutionCells = cells,
                fixedQueenCells = CloneCells(_fixedQueenCells)
            };
            return true;
        }

        private void ExportJson(bool requireUnique)
        {
            if (!TryBuildCurrentLevelFile(requireUnique, out var file))
            {
                return;
            }

            var path = string.IsNullOrEmpty(_exportPath)
                ? Path.Combine(DefaultLevelDir, $"level_{_levelId:D3}.json").Replace('\\', '/')
                : _exportPath.Replace('\\', '/');

            if (!path.StartsWith("Assets/"))
            {
                // 绝对路径
                var abs = path;
                var dir = Path.GetDirectoryName(abs);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(abs, WoodukuLevelJson.ToJson(file));
                RememberOpenedLevel(abs);
                RefreshLevelStats();
                SetStatus($"已导出：{abs}", MessageType.Info);
                return;
            }

            EnsureDir(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? DefaultLevelDir);
            File.WriteAllText(full, WoodukuLevelJson.ToJson(file));
            AssetDatabase.Refresh();
            _exportPath = path;
            RememberOpenedLevel(path);
            RefreshLevelStats();
            SetStatus($"已导出：{path}", MessageType.Info);
        }

        private void ImportJson()
        {
            EnsureDir(DefaultLevelDir);
            var currentPath = ToAbsolutePath(_exportPath);
            var initialDirectory = File.Exists(currentPath)
                ? Path.GetDirectoryName(currentPath)
                : Path.GetFullPath(DefaultLevelDir);
            var abs = EditorUtility.OpenFilePanel("导入关卡 JSON", initialDirectory, "json");
            if (string.IsNullOrEmpty(abs))
            {
                return;
            }

            ImportJsonFromPath(abs, true);
        }

        private bool ImportJsonFromPath(string path, bool showMissingError)
        {
            var abs = ToAbsolutePath(path);
            if (!File.Exists(abs))
            {
                if (showMissingError)
                {
                    SetStatus($"导入失败：找不到关卡文件 {path}", MessageType.Error);
                }

                return false;
            }

            WoodukuLevelFile file;
            try
            {
                var json = File.ReadAllText(abs);
                file = WoodukuLevelJson.FromJson(json);
            }
            catch (System.Exception ex)
            {
                SetStatus($"导入失败：{ex.Message}", MessageType.Error);
                return false;
            }

            if (file == null ||
                file.size < 2 ||
                file.regions == null ||
                file.regions.Length != file.size * file.size)
            {
                SetStatus("导入失败：JSON 格式无效或 regions 长度不匹配。", MessageType.Error);
                return false;
            }

            _levelId = file.id;
            _hintCount = file.hintCount;
            _difficulty = file.difficulty;
            _difficultyScore = file.difficultyScore;
            _sourceName = file.sourceName ?? string.Empty;
            _fixedQueenCells = CloneCells(file.fixedQueenCells);
            ResizeBoard(file.size);

            // 将逻辑色写回色板前 N 个开启槽
            _palette.EnsureDefaults(_size);
            for (var i = 0; i < _palette.slots.Count; i++)
            {
                _palette.slots[i].enabled = i < _size;
            }

            if (file.colors != null)
            {
                for (var i = 0; i < file.colors.Length && i < _palette.slots.Count; i++)
                {
                    var c = file.colors[i];
                    _palette.slots[i].name = c.name;
                    _palette.slots[i].color = WoodukuLevelJson.HexToColor(c.hex, _palette.slots[i].color);
                    _palette.slots[i].enabled = true;
                }
            }

            EditorUtility.SetDirty(_palette);

            // regions 已是逻辑 0..N-1，直接对应槽位 0..N-1
            for (var i = 0; i < file.regions.Length; i++)
            {
                _regions[i] = file.regions[i];
            }

            _exportPath = AbsoluteToAssetPath(abs);
            RememberOpenedLevel(_exportPath);
            _lastResult = null;
            RunValidate();
            SetStatus($"已导入：{abs}", _statusType);
            return true;
        }

        private void OpenAdjacentLevel(int offset)
        {
            var targetId = _levelId + offset;
            if (targetId < 1)
            {
                return;
            }

            if (_packedCatalog != null && TryLoadPackedLevel(targetId))
            {
                return;
            }

            var targetPath = BuildLevelPath(targetId);
            ImportJsonFromPath(targetPath, true);
        }

        private void CreateNewLevel()
        {
            RefreshLevelStats();
            _levelId = _maxLevelId + 1;
            _exportPath = Path.Combine(DefaultLevelDir, $"level_{_levelId:D3}.json").Replace('\\', '/');
            _difficulty = WoodukuLevelDifficulty.Normal;
            _difficultyScore = 0;
            _sourceName = string.Empty;
            _fixedQueenCells = System.Array.Empty<WoodukuCellRef>();

            for (var i = 0; i < _regions.Length; i++)
            {
                _regions[i] = -1;
            }

            _lastResult = null;
            SetStatus($"已新建关卡 {_levelId}，请编辑并导出 JSON。", MessageType.Info);
        }

        private void RefreshLevelStats()
        {
            if (_packedCatalog != null && _packedCatalog.totalLevelCount > 0)
            {
                _levelCount = _packedCatalog.totalLevelCount;
                _maxLevelId = _packedCatalog.totalLevelCount;
                return;
            }

            _levelCount = 0;
            _maxLevelId = 0;

            var directory = Path.GetFullPath(DefaultLevelDir);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory.GetFiles(directory, "level_*.json", SearchOption.TopDirectoryOnly);
            _levelCount = files.Length;
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var idText = name.Substring("level_".Length);
                if (int.TryParse(idText, out var id))
                {
                    _maxLevelId = Mathf.Max(_maxLevelId, id);
                }
            }
        }

        private bool TryLoadPackedLevel(int levelId)
        {
            var entry = _packedCatalog?.FindPack(levelId);
            if (entry == null)
            {
                SetStatus($"分包目录中不存在关卡 {levelId}。", MessageType.Warning);
                return false;
            }

            var pack = AssetDatabase.LoadAssetAtPath<WoodukuLevelPackAsset>(entry.assetPath);
            if (pack == null || !pack.TryGetLevel(levelId, out var file) || file == null)
            {
                SetStatus($"无法加载分包关卡 {levelId}：{entry.assetPath}", MessageType.Error);
                return false;
            }

            _activePack = pack;
            _levelId = file.id;
            _hintCount = file.hintCount;
            _difficulty = file.difficulty;
            _difficultyScore = file.difficultyScore;
            _sourceName = file.sourceName ?? string.Empty;
            _fixedQueenCells = CloneCells(file.fixedQueenCells);
            ResizeBoard(file.size);

            _palette.EnsureDefaults(_size);
            for (var i = 0; i < _palette.slots.Count; i++)
            {
                _palette.slots[i].enabled = i < _size;
            }

            if (file.colors != null)
            {
                for (var i = 0; i < file.colors.Length && i < _palette.slots.Count; i++)
                {
                    var color = file.colors[i];
                    if (color == null)
                    {
                        continue;
                    }

                    _palette.slots[i].name = color.name;
                    _palette.slots[i].color =
                        WoodukuLevelJson.HexToColor(color.hex, _palette.slots[i].color);
                }
            }

            for (var i = 0; i < file.regions.Length; i++)
            {
                _regions[i] = file.regions[i];
            }

            _exportPath = string.Empty;
            _lastResult = null;
            RunValidate();
            SetStatus(
                $"已加载分包关卡 {levelId}/{_packedCatalog.totalLevelCount}，" +
                $"难度={_difficulty}，难度分={_difficultyScore}。",
                _statusType);
            Repaint();
            return true;
        }

        private bool IsFixedQueen(int row, int col)
        {
            if (_fixedQueenCells == null)
            {
                return false;
            }

            for (var i = 0; i < _fixedQueenCells.Length; i++)
            {
                var cell = _fixedQueenCells[i];
                if (cell != null && cell.r == row && cell.c == col)
                {
                    return true;
                }
            }

            return false;
        }

        private static WoodukuCellRef[] CloneCells(WoodukuCellRef[] source)
        {
            if (source == null || source.Length == 0)
            {
                return System.Array.Empty<WoodukuCellRef>();
            }

            var result = new WoodukuCellRef[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var cell = source[i];
                result[i] = cell == null ? null : new WoodukuCellRef { r = cell.r, c = cell.c };
            }

            return result;
        }

        private string BuildLevelPath(int levelId)
        {
            var currentPath = string.IsNullOrEmpty(_exportPath)
                ? DefaultLevelDir
                : Path.GetDirectoryName(_exportPath.Replace('\\', '/'));
            var directory = string.IsNullOrEmpty(currentPath) ? DefaultLevelDir : currentPath;
            return Path.Combine(directory, $"level_{levelId:D3}.json").Replace('\\', '/');
        }

        private static string ToAbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return Path.GetFullPath(path);
        }

        private static void RememberOpenedLevel(string path)
        {
            EditorPrefs.SetString(LastOpenedLevelKey, path.Replace('\\', '/'));
        }

        private void EnsurePalette()
        {
            if (_palette != null)
            {
                _palette.EnsureDefaults(Mathf.Max(_size, 4));
                return;
            }

            _palette = AssetDatabase.LoadAssetAtPath<WoodukuColorPaletteAsset>(DefaultPalettePath);
            if (_palette == null)
            {
                CreatePaletteAsset();
            }
            else
            {
                _palette.EnsureDefaults(Mathf.Max(_size, 4));
            }
        }

        private void CreatePaletteAsset()
        {
            EnsureDir(DefaultLevelDir);
            var asset = CreateInstance<WoodukuColorPaletteAsset>();
            asset.EnsureDefaults(8);
            AssetDatabase.CreateAsset(asset, DefaultPalettePath);
            AssetDatabase.SaveAssets();
            _palette = asset;
            SetStatus($"已创建色板：{DefaultPalettePath}", MessageType.Info);
        }

        private void EnsureBoard(int size)
        {
            if (_regions != null && _regions.Length == size * size)
            {
                return;
            }

            ResizeBoard(size);
        }

        private void ResizeBoard(int newSize)
        {
            var old = _regions;
            var oldSize = old == null ? 0 : (int)Mathf.Sqrt(old.Length);
            _size = newSize;
            _regions = new int[newSize * newSize];
            for (var i = 0; i < _regions.Length; i++)
            {
                _regions[i] = -1;
            }

            if (old != null && oldSize > 0)
            {
                var copy = Mathf.Min(oldSize, newSize);
                for (var r = 0; r < copy; r++)
                {
                    for (var c = 0; c < copy; c++)
                    {
                        _regions[r * newSize + c] = old[r * oldSize + c];
                    }
                }
            }

            _lastResult = null;
            _exportPath = Path.Combine(DefaultLevelDir, $"level_{_levelId:D3}.json").Replace('\\', '/');
        }

        private void FillDebugRows()
        {
            var enabled = _palette.GetEnabledIndices();
            enabled.Sort();
            if (enabled.Count < _size)
            {
                _palette.EnsureDefaults(_size);
                for (var i = 0; i < _size; i++)
                {
                    _palette.slots[i].enabled = true;
                }

                enabled = _palette.GetEnabledIndices();
                enabled.Sort();
            }

            for (var r = 0; r < _size; r++)
            {
                for (var c = 0; c < _size; c++)
                {
                    _regions[r * _size + c] = enabled[r % _size];
                }
            }

            _lastResult = null;
            SetStatus("已按行填充调试色区（通常无固有解，仅便于看色）。", MessageType.Info);
        }

        private void GenerateRandomLevel()
        {
            var enabled = _palette.GetEnabledIndices();
            enabled.Sort();
            var colorCount = enabled.Count;
            var addedColorCount = 0;
            if (_size < 4)
            {
                SetStatus($"边长 N={_size} 无法满足猴子八邻不接触规则，随机生成需要 N≥4。", MessageType.Error);
                return;
            }

            if (colorCount < _size)
            {
                addedColorCount = _size - colorCount;
                EnsureEnabledPaletteCount(_size);
                enabled = _palette.GetEnabledIndices();
                enabled.Sort();
                colorCount = enabled.Count;
            }

            if (colorCount > _size)
            {
                SetStatus($"随机生成要求颜色数量等于边长 N：当前 N={_size}，已开启 {colorCount} 种颜色。", MessageType.Error);
                return;
            }

            const int maxAttempts = 5000;
            var random = new System.Random();
            var solutionCols = BuildRandomSolution(_size, random);
            if (solutionCols == null ||
                !TryBuildRandomUniqueRegions(
                    _size,
                    solutionCols,
                    random,
                    maxAttempts,
                    out var logicalRegions,
                    out var result,
                    out var attempts,
                    out var acceptedExpansions))
            {
                _lastResult = null;
                SetStatus($"随机生成失败：无法构造 N={_size} 的唯一解关卡。", MessageType.Warning);
                return;
            }

            for (var i = 0; i < logicalRegions.Length; i++)
            {
                _regions[i] = enabled[logicalRegions[i]];
            }

            _lastResult = result;
            var colorMessage = addedColorCount > 0 ? $"，自动补充 {addedColorCount} 种颜色" : string.Empty;
            SetStatus(
                $"已按边长 N={_size} 随机生成唯一解关卡{colorMessage}（扩张 {acceptedExpansions} 格，校验 {attempts} 次）。",
                MessageType.Info);
            Repaint();
        }

        private static int[] BuildRandomSolution(int size, System.Random random)
        {
            var result = new int[size];
            var used = new bool[size];
            return PlaceRow(0) ? result : null;

            bool PlaceRow(int row)
            {
                if (row == size)
                {
                    return true;
                }

                var candidates = new List<int>(size);
                for (var col = 0; col < size; col++)
                {
                    candidates.Add(col);
                }

                Shuffle(candidates, random);
                foreach (var col in candidates)
                {
                    if (used[col] || row > 0 && System.Math.Abs(col - result[row - 1]) <= 1)
                    {
                        continue;
                    }

                    used[col] = true;
                    result[row] = col;
                    if (PlaceRow(row + 1))
                    {
                        return true;
                    }

                    used[col] = false;
                }

                return false;
            }
        }

        private static bool TryBuildRandomUniqueRegions(
            int size,
            int[] solutionCols,
            System.Random random,
            int maxAttempts,
            out int[] regions,
            out WoodukuLevelSolver.Result result,
            out int attempts,
            out int acceptedExpansions)
        {
            regions = null;
            result = null;
            attempts = 0;
            acceptedExpansions = 0;

            var colors = new List<int>(size);
            for (var color = 0; color < size; color++)
            {
                colors.Add(color);
            }

            var backgroundColor = -1;
            var backgroundSeed = -1;
            for (var setupAttempt = 0; setupAttempt < 64; setupAttempt++)
            {
                Shuffle(colors, random);
                var backgroundRow = random.Next(size);
                backgroundColor = colors[backgroundRow];
                backgroundSeed = backgroundRow * size + solutionCols[backgroundRow];

                regions = new int[size * size];
                for (var i = 0; i < regions.Length; i++)
                {
                    regions[i] = backgroundColor;
                }

                for (var row = 0; row < size; row++)
                {
                    regions[row * size + solutionCols[row]] = colors[row];
                }

                result = WoodukuLevelSolver.Analyze(size, regions, size);
                if (result.HasUniqueSolution)
                {
                    break;
                }
            }

            if (result == null || !result.HasUniqueSolution)
            {
                return false;
            }

            // 避免在 local function 中直接捕获 out 参数 regions
            var regionCells = regions;
            var regionSizes = new int[size];
            for (var i = 0; i < regionCells.Length; i++)
            {
                regionSizes[regionCells[i]]++;
            }

            // 初始状态由 N-1 个单格色区锁定唯一解，再随机侵占背景色。
            // 每一步都重新校验，只有仍然四连通且保持唯一解的扩张才会保留。
            var targetBackgroundSize = System.Math.Max(size * 2, (size * size + 2) / 3);
            var consecutiveFailures = 0;
            var maxConsecutiveFailures = size * 30;
            var frontier = new List<int>();
            var neighborColors = new List<int>(4);
            while (attempts < maxAttempts &&
                   consecutiveFailures < maxConsecutiveFailures &&
                   regionSizes[backgroundColor] > targetBackgroundSize)
            {
                frontier.Clear();
                for (var i = 0; i < regionCells.Length; i++)
                {
                    if (i != backgroundSeed &&
                        regionCells[i] == backgroundColor &&
                        HasNonBackgroundNeighbor(i))
                    {
                        frontier.Add(i);
                    }
                }

                if (frontier.Count == 0)
                {
                    break;
                }

                var cell = frontier[random.Next(frontier.Count)];
                neighborColors.Clear();
                AddNeighborColor(cell - size, cell / size > 0);
                AddNeighborColor(cell + size, cell / size < size - 1);
                AddNeighborColor(cell - 1, cell % size > 0);
                AddNeighborColor(cell + 1, cell % size < size - 1);

                var smallestSize = int.MaxValue;
                for (var i = 0; i < neighborColors.Count; i++)
                {
                    smallestSize = System.Math.Min(smallestSize, regionSizes[neighborColors[i]]);
                }

                for (var i = neighborColors.Count - 1; i >= 0; i--)
                {
                    if (regionSizes[neighborColors[i]] != smallestSize)
                    {
                        neighborColors.RemoveAt(i);
                    }
                }

                var newColor = neighborColors[random.Next(neighborColors.Count)];
                regionCells[cell] = newColor;
                attempts++;

                var candidateResult = WoodukuLevelSolver.Analyze(size, regionCells, size);
                if (candidateResult.HasUniqueSolution)
                {
                    result = candidateResult;
                    regionSizes[backgroundColor]--;
                    regionSizes[newColor]++;
                    acceptedExpansions++;
                    consecutiveFailures = 0;
                }
                else
                {
                    regionCells[cell] = backgroundColor;
                    consecutiveFailures++;
                }
            }

            return true;

            bool HasNonBackgroundNeighbor(int index)
            {
                var row = index / size;
                var col = index % size;
                return row > 0 && regionCells[index - size] != backgroundColor ||
                       row < size - 1 && regionCells[index + size] != backgroundColor ||
                       col > 0 && regionCells[index - 1] != backgroundColor ||
                       col < size - 1 && regionCells[index + 1] != backgroundColor;
            }

            void AddNeighborColor(int index, bool inBounds)
            {
                if (!inBounds ||
                    regionCells[index] == backgroundColor ||
                    neighborColors.Contains(regionCells[index]))
                {
                    return;
                }

                neighborColors.Add(regionCells[index]);
            }
        }

        private static void Shuffle<T>(List<T> list, System.Random random)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private void SetStatus(string msg, MessageType type)
        {
            _status = msg;
            _statusType = type;
        }

        private static void EnsureDir(string assetDir)
        {
            if (string.IsNullOrEmpty(assetDir))
            {
                return;
            }

            assetDir = assetDir.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetDir))
            {
                return;
            }

            var parts = assetDir.Split('/');
            var cur = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }

                cur = next;
            }
        }

        private static string AbsoluteToAssetPath(string absolute)
        {
            var data = Application.dataPath.Replace('\\', '/');
            var abs = absolute.Replace('\\', '/');
            if (abs.StartsWith(data))
            {
                return "Assets" + abs.Substring(data.Length);
            }

            return abs;
        }
    }
}
