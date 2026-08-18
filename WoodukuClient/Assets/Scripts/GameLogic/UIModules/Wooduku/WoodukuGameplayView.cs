using System.Collections;
using DG.Tweening;
using FishFramework;
using GameLogic.MainMenu;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.Wooduku
{
    /// <summary>
    /// 局内玩法常驻根（不走 UIPanel / OpenPanel）。
    /// 与主菜单平级：同挂 Panel 层底衬，进关/返回仅显隐切换；后续 OpenPanel 盖在二者之上。
    /// </summary>
    public sealed class WoodukuGameplayView : MonoBehaviour
    {
        public const string PrefabPath = "Assets/GameRes/Prefabs/Wooduku/UIWoodukuGame.prefab";
        private const string CatSpritePath = "Assets/GameRes/ImageAtlas/GamePlay/wooduku_mark_cat.png";
        private const string ExcludeSpritePath = "Assets/GameRes/ImageAtlas/GamePlay/wooduku_mark_x.png";
        private const float ExcludeAppearSeconds = 0.18f;
        // 与主菜单平级、同为 Panel 层底衬；后续 OpenPanel 须盖在其上（勿抬高）。
        private const int SortingOrder = -1;

        public static WoodukuGameplayView Instance { get; private set; }

        [SerializeField] private TButton backButton;
        [SerializeField] private TButton winBackButton;
        [SerializeField] private TButton winNextButton;
        [SerializeField] private TextMeshProUGUI progressLabel;
        [SerializeField] private TextMeshProUGUI levelLabel;
        [SerializeField] private RectTransform boardRoot;
        [SerializeField] private GameObject winOverlay;

        private WoodukuGameSession _session;
        private WoodukuBoardInput _boardInput;
        private CellView[] _cells;
        private Coroutine _flashCo;
        private Sprite _catSprite;
        private Sprite _excludeSprite;
        private Sprite _whiteSprite;
        private bool _boundUi;

        public static WoodukuGameplayView EnsureSpawned()
        {
            if (Instance != null)
            {
                return Instance;
            }

            var existing = FindObjectOfType<WoodukuGameplayView>(true);
            if (existing != null)
            {
                Instance = existing;
                return Instance;
            }

            if (GameModule.UI == null)
            {
                Debug.LogError("[WoodukuGameplay] GameModule.UI is null.");
                return null;
            }

            var prefab = ResourceModule.LoadAsset<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[WoodukuGameplay] Failed to load prefab: {PrefabPath}");
                return null;
            }

            var layer = GameModule.UI.GetLayerRect(PanelLayer.Panel);
            var go = Instantiate(prefab, layer, false);
            go.name = "WoodukuGameplay";
            ConfigureTransform(go);
            ConfigureCanvas(go);

            var view = go.GetComponent<WoodukuGameplayView>();
            if (view == null)
            {
                view = go.AddComponent<WoodukuGameplayView>();
            }

            view.CacheUiRefs();
            go.SetActive(false);
            Instance = view;
            return Instance;
        }

        public void EnterLevel(int levelId)
        {
            CacheUiRefs();
            SetMainMenuVisible(false);
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (winOverlay != null)
            {
                winOverlay.SetActive(false);
            }

            TeardownSession();
            if (!TryLoadSession(levelId))
            {
                ExitToMenu();
                return;
            }

            EnsureSprites();
            BuildBoard();
            BindBoardInput();
            RefreshHud();
            _session.Changed += RefreshHud;
            _session.Cleared += OnCleared;
        }

        public void ExitToMenu()
        {
            TeardownSession();
            gameObject.SetActive(false);
            SetMainMenuVisible(true);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            CacheUiRefs();
            BindButtons();
        }

        private void OnDestroy()
        {
            TeardownSession();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void CacheUiRefs()
        {
            if (_boundUi && boardRoot != null)
            {
                return;
            }

            backButton = FindButton("BackButton");
            winBackButton = FindButton("WinBackButton");
            winNextButton = FindButton("WinNextButton");
            progressLabel = FindTmp("ProgressLabel");
            levelLabel = FindTmp("LevelLabel");
            boardRoot = FindRect("BoardRoot");
            var winTf = FindTransform("WinOverlay");
            winOverlay = winTf != null ? winTf.gameObject : null;
            _boundUi = true;
            BindButtons();
        }

        private void BindButtons()
        {
            if (backButton != null)
            {
                backButton.onClick.RemoveListener(ExitToMenu);
                backButton.onClick.AddListener(ExitToMenu);
            }

            if (winBackButton != null)
            {
                winBackButton.onClick.RemoveListener(ExitToMenu);
                winBackButton.onClick.AddListener(ExitToMenu);
            }

            if (winNextButton != null)
            {
                winNextButton.onClick.RemoveListener(EnterNextLevel);
                winNextButton.onClick.AddListener(EnterNextLevel);
            }
        }

        private void EnterNextLevel()
        {
            if (_session == null)
            {
                return;
            }

            var nextLevelId = _session.LevelId + 1;
            if (nextLevelId > WoodukuLevelProgress.LastLevelId)
            {
                ExitToMenu();
                return;
            }

            EnterLevel(nextLevelId);
        }

        private static void SetMainMenuVisible(bool visible)
        {
            var menu = FindObjectOfType<UIMainMenu>(true);
            if (menu != null)
            {
                menu.gameObject.SetActive(visible);
            }
        }

        private void TeardownSession()
        {
            StopCellMarkAnimations();

            if (_session != null)
            {
                _session.Changed -= RefreshHud;
                _session.Cleared -= OnCleared;
                _session = null;
            }

            if (_flashCo != null)
            {
                StopCoroutine(_flashCo);
                _flashCo = null;
            }

            _boardInput = null;
            _cells = null;
        }

        private bool TryLoadSession(int levelId)
        {
            if (!WoodukuLevelRepository.TryLoadLevel(levelId, out var file))
            {
                Debug.LogError($"[WoodukuGameplay] Failed to load level: {levelId}");
                return false;
            }

            try
            {
                _session = new WoodukuGameSession(file);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[WoodukuGameplay] Parse level failed: {e.Message}");
                return false;
            }
        }

        private void EnsureSprites()
        {
            if (_catSprite == null)
            {
                _catSprite = LoadSprite(CatSpritePath);
            }

            if (_excludeSprite == null)
            {
                _excludeSprite = LoadSprite(ExcludeSpritePath);
            }

            if (_whiteSprite == null)
            {
                var tex = Texture2D.whiteTexture;
                _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
        }

        private static Sprite LoadSprite(string assetPath)
        {
            var sprite = ResourceModule.LoadAsset<Sprite>(assetPath);
            if (sprite != null)
            {
                return sprite;
            }

            var tex = ResourceModule.LoadAsset<Texture2D>(assetPath);
            if (tex == null)
            {
                Debug.LogError($"[WoodukuGameplay] Failed to load sprite: {assetPath}");
                return null;
            }

            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private void BuildBoard()
        {
            if (boardRoot == null || _session == null)
            {
                return;
            }

            for (var i = boardRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(boardRoot.GetChild(i).gameObject);
            }

            var n = _session.Size;
            var grid = boardRoot.GetComponent<GridLayoutGroup>();
            if (grid == null)
            {
                grid = boardRoot.gameObject.AddComponent<GridLayoutGroup>();
            }

            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = n;
            grid.spacing = new Vector2(4f, 4f);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.padding = new RectOffset(0, 0, 0, 0);

            var boardSize = Mathf.Min(boardRoot.rect.width, boardRoot.rect.height);
            if (boardSize < 10f)
            {
                boardSize = 640f;
            }

            var cellSize = (boardSize - 4f * (n - 1)) / n;
            grid.cellSize = new Vector2(cellSize, cellSize);

            _cells = new CellView[n * n];
            var white = _whiteSprite;
            var markPad = cellSize * 0.12f;
            for (var r = 0; r < n; r++)
            {
                for (var c = 0; c < n; c++)
                {
                    var go = new GameObject($"Cell_{r}_{c}", typeof(RectTransform), typeof(Image));
                    var rt = go.GetComponent<RectTransform>();
                    rt.SetParent(boardRoot, false);

                    var bg = go.GetComponent<Image>();
                    bg.sprite = white;
                    bg.color = _session.GetCellColor(r, c);
                    bg.raycastTarget = false;

                    var markGo = new GameObject("Mark", typeof(RectTransform), typeof(Image));
                    var markRt = markGo.GetComponent<RectTransform>();
                    markRt.SetParent(rt, false);
                    markRt.anchorMin = Vector2.zero;
                    markRt.anchorMax = Vector2.one;
                    markRt.offsetMin = new Vector2(markPad, markPad);
                    markRt.offsetMax = new Vector2(-markPad, -markPad);

                    var mark = markGo.GetComponent<Image>();
                    mark.preserveAspect = true;
                    mark.raycastTarget = false;
                    mark.enabled = false;
                    mark.color = Color.white;

                    _cells[r * n + c] = new CellView
                    {
                        Root = rt,
                        Background = bg,
                        Mark = mark,
                        BaseColor = bg.color
                    };
                }
            }

            var boardImage = boardRoot.GetComponent<Image>();
            if (boardImage == null)
            {
                boardImage = boardRoot.gameObject.AddComponent<Image>();
                boardImage.color = new Color(1f, 1f, 1f, 0.01f);
                boardImage.sprite = white;
            }

            boardImage.raycastTarget = true;
            RefreshCellMarks();
        }

        private void BindBoardInput()
        {
            if (boardRoot == null)
            {
                return;
            }

            _boardInput = boardRoot.GetComponent<WoodukuBoardInput>();
            if (_boardInput == null)
            {
                _boardInput = boardRoot.gameObject.AddComponent<WoodukuBoardInput>();
            }

            _boardInput.Bind(_session, ScreenToCell, OnWrongConfirm, PlayExcludeAppear, () =>
            {
                RefreshCellMarks();
                RefreshHud();
            });
        }

        private (int r, int c, bool ok) ScreenToCell(Vector2 screenPos, bool _)
        {
            if (boardRoot == null || _session == null || _cells == null)
            {
                return (0, 0, false);
            }

            var canvas = boardRoot.GetComponentInParent<Canvas>();
            Camera cam = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                cam = canvas.worldCamera;
            }

            var n = _session.Size;
            for (var r = 0; r < n; r++)
            {
                for (var c = 0; c < n; c++)
                {
                    var view = _cells[r * n + c];
                    if (view?.Root == null)
                    {
                        continue;
                    }

                    if (RectTransformUtility.RectangleContainsScreenPoint(view.Root, screenPos, cam))
                    {
                        return (r, c, true);
                    }
                }
            }

            return (0, 0, false);
        }

        private void RefreshHud()
        {
            if (_session == null)
            {
                return;
            }

            if (progressLabel != null)
            {
                progressLabel.text = $"{_session.FoundCount}/{_session.TotalCats}";
            }

            if (levelLabel != null)
            {
                levelLabel.text = $"关卡 {_session.LevelId}";
            }

            RefreshCellMarks();
        }

        private void RefreshCellMarks()
        {
            if (_session == null || _cells == null)
            {
                return;
            }

            var n = _session.Size;
            for (var r = 0; r < n; r++)
            {
                for (var c = 0; c < n; c++)
                {
                    var view = _cells[r * n + c];
                    if (view?.Mark == null)
                    {
                        continue;
                    }

                    StopCellMarkAnimation(view);
                    var mark = _session.GetMark(r, c);
                    switch (mark)
                    {
                        case WoodukuCellMark.Exclude:
                            view.Mark.sprite = _excludeSprite;
                            view.Mark.enabled = _excludeSprite != null;
                            view.Mark.color = Color.white;
                            break;
                        case WoodukuCellMark.Confirmed:
                            view.Mark.sprite = _catSprite;
                            view.Mark.enabled = _catSprite != null;
                            view.Mark.color = Color.white;
                            break;
                        default:
                            view.Mark.sprite = null;
                            view.Mark.enabled = false;
                            break;
                    }

                    view.Background.color = view.BaseColor;
                }
            }
        }

        private void PlayExcludeAppear(int r, int c)
        {
            if (_cells == null || _session == null || _session.GetMark(r, c) != WoodukuCellMark.Exclude)
            {
                return;
            }

            var i = r * _session.Size + c;
            if (i < 0 || i >= _cells.Length || _cells[i]?.Mark == null)
            {
                return;
            }

            var view = _cells[i];
            StopCellMarkAnimation(view);
            view.Mark.rectTransform.localScale = Vector3.zero;
            view.Mark.color = new Color(1f, 1f, 1f, 0f);

            var sequence = DOTween.Sequence()
                .SetUpdate(true)
                .SetLink(view.Mark.gameObject);
            view.MarkTween = sequence;
            sequence.Append(view.Mark.rectTransform.DOScale(Vector3.one, ExcludeAppearSeconds)
                .SetEase(Ease.OutCubic));
            sequence.Join(view.Mark.DOFade(1f, ExcludeAppearSeconds * 0.75f));
            sequence.OnComplete(() =>
            {
                if (view.MarkTween == sequence)
                {
                    view.MarkTween = null;
                }
            });
        }

        private void StopCellMarkAnimations()
        {
            if (_cells == null)
            {
                return;
            }

            foreach (var view in _cells)
            {
                StopCellMarkAnimation(view);
            }
        }

        private static void StopCellMarkAnimation(CellView view)
        {
            if (view?.Mark == null)
            {
                return;
            }

            view.MarkTween?.Kill();
            view.MarkTween = null;
            view.Mark.rectTransform.localScale = Vector3.one;
            view.Mark.color = Color.white;
        }

        private void OnWrongConfirm(int r, int c)
        {
            if (_cells == null || _session == null)
            {
                return;
            }

            var i = r * _session.Size + c;
            if (i < 0 || i >= _cells.Length || _cells[i] == null)
            {
                return;
            }

            if (_flashCo != null)
            {
                StopCoroutine(_flashCo);
            }

            _flashCo = StartCoroutine(FlashCell(_cells[i]));
        }

        private IEnumerator FlashCell(CellView view)
        {
            view.Background.color = new Color(1f, 0.35f, 0.35f, 1f);
            yield return new WaitForSecondsRealtime(0.18f);
            if (view.Background != null)
            {
                view.Background.color = view.BaseColor;
            }

            _flashCo = null;
        }

        private void OnCleared()
        {
            RefreshHud();
            WoodukuLevelProgress.AdvanceAfterClear(_session.LevelId);
            var hasNextLevel = _session.LevelId < WoodukuLevelProgress.LastLevelId;
            if (winNextButton != null)
            {
                winNextButton.gameObject.SetActive(hasNextLevel);
            }

            if (winBackButton != null)
            {
                var backRect = winBackButton.transform as RectTransform;
                if (backRect != null)
                {
                    backRect.offsetMin = hasNextLevel ? new Vector2(-200f, -36f) : new Vector2(-120f, -36f);
                    backRect.offsetMax = hasNextLevel ? new Vector2(-20f, 36f) : new Vector2(120f, 36f);
                }
            }

            if (winOverlay != null)
            {
                winOverlay.SetActive(true);
            }
        }

        private static void ConfigureTransform(GameObject go)
        {
            var rt = go.transform as RectTransform;
            if (rt == null)
            {
                return;
            }

            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();
        }

        private static void ConfigureCanvas(GameObject go)
        {
            var canvas = go.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = go.AddComponent<Canvas>();
            }

            Camera renderCam = GameModule.UI.GetRenderCamera();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = renderCam;
            canvas.planeDistance = 100f;
            canvas.overrideSorting = true;
            canvas.sortingOrder = SortingOrder;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1
                                             | AdditionalCanvasShaderChannels.Normal
                                             | AdditionalCanvasShaderChannels.Tangent;

            if (go.GetComponent<GraphicRaycaster>() == null)
            {
                go.AddComponent<GraphicRaycaster>();
            }
        }

        private TButton FindButton(string objectName)
        {
            var t = FindTransform(objectName);
            return t != null ? t.GetComponent<TButton>() : null;
        }

        private TextMeshProUGUI FindTmp(string objectName)
        {
            var t = FindTransform(objectName);
            return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
        }

        private RectTransform FindRect(string objectName)
        {
            return FindTransform(objectName) as RectTransform;
        }

        private Transform FindTransform(string objectName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == objectName)
                {
                    return t;
                }
            }

            return null;
        }

        private sealed class CellView
        {
            public RectTransform Root;
            public Image Background;
            public Image Mark;
            public Color BaseColor;
            public Tween MarkTween;
        }
    }
}
