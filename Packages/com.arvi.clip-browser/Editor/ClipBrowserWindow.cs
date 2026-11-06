// Clip Browser — удобный переключатель клипов для окна Animation.
// Ставится через VRChat Creator Companion (пакет com.arvi.clip-browser)
// или вручную — в любую папку Editor внутри Assets (например Assets/Editor/ClipBrowser/).
// Открыть: Window > Animation > Clip Browser.
// Хоткеи (меняются в Edit > Shortcuts > Clip Browser):
//   Alt+Down / Alt+Up             — следующий / предыдущий клип в группе
//   Alt+Shift+Down / Alt+Shift+Up — следующая / предыдущая группа
// Проверено на API Unity 2022.3 (VRChat).

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace ArViTools.ClipBrowser
{
    public enum GroupMode
    {
        NamePrefix,
        AnimatorLayer,
        Folder,
    }

    internal sealed class ClipGroup
    {
        public string key;
        public string title;
        public string tooltip;
        public bool shortLabels; // показывать на плитке только суффикс ("3" вместо "Skin3")
        public readonly List<AnimationClip> clips = new List<AnimationClip>();
    }

    internal sealed class ClipInfo
    {
        public readonly List<Material> materials = new List<Material>();
        public string tooltip;
        public string searchText;
    }

    // ------------------------------------------------------------------------
    // Ядро: список клипов, группировка, переключение, избранное.
    // Используется и окном, и хоткеями (хоткеи работают даже без открытого окна).
    // ------------------------------------------------------------------------
    [InitializeOnLoad]
    internal static class ClipBrowserCore
    {
        const string Prefs = "ArViTools.ClipBrowser.";
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Regex TrailingNumber = new Regex(@"^(.*?)[\s_\-\.]*(\d+)$", RegexOptions.Compiled);
        static readonly char[] Separators = { '_', ' ', '-', '.' };

        static readonly Dictionary<AnimationClip, ClipInfo> infoCache = new Dictionary<AnimationClip, ClipInfo>();
        static readonly Dictionary<string, AnimationClip> lastInGroup = new Dictionary<string, AnimationClip>();
        static string currentGroupKey;

        static AnimationWindow cachedAnimWindow;
        static FieldInfo animEditorField;
        static PropertyInfo stateProp, rootProp;
        static bool reflectionFailed;

        static HashSet<string> favorites;

        public static event Action Changed;

        static ClipBrowserCore()
        {
            EditorApplication.projectChanged += () => { infoCache.Clear(); RaiseChanged(); };
            AnimationUtility.onCurveWasModified += OnCurveModified;
        }

        static void OnCurveModified(AnimationClip clip, EditorCurveBinding binding, AnimationUtility.CurveModifiedType type)
        {
            if (clip != null && infoCache.Remove(clip)) RaiseChanged();
        }

        public static void RaiseChanged() => Changed?.Invoke();

        public static void ClearCache()
        {
            infoCache.Clear();
            RaiseChanged();
        }

        // ---- settings ----

        public static GroupMode Mode
        {
            get => (GroupMode)EditorPrefs.GetInt(Prefs + "Mode", (int)GroupMode.NamePrefix);
            set => EditorPrefs.SetInt(Prefs + "Mode", (int)value);
        }

        public static bool AutoPreview
        {
            get => EditorPrefs.GetBool(Prefs + "AutoPreview", true);
            set => EditorPrefs.SetBool(Prefs + "AutoPreview", value);
        }

        // ---- Animation window ----

        public static AnimationWindow FindAnimationWindow()
        {
            if (cachedAnimWindow != null) return cachedAnimWindow;
            var all = Resources.FindObjectsOfTypeAll<AnimationWindow>();
            cachedAnimWindow = all.Length > 0 ? all[0] : null;
            return cachedAnimWindow;
        }

        /// Объект с аниматором, который сейчас показывает окно Animation (учитывает замок окна).
        /// Если внутреннее API недоступно — берём ближайший Animator от выделения.
        public static GameObject GetActiveRoot(AnimationWindow w)
        {
            if (w != null && !reflectionFailed)
            {
                try
                {
                    if (animEditorField == null) animEditorField = typeof(AnimationWindow).GetField("m_AnimEditor", AnyInstance);
                    object editor = animEditorField?.GetValue(w);
                    if (editor != null)
                    {
                        if (stateProp == null) stateProp = editor.GetType().GetProperty("state", AnyInstance);
                        object state = stateProp?.GetValue(editor);
                        if (state != null)
                        {
                            if (rootProp == null) rootProp = state.GetType().GetProperty("activeRootGameObject", AnyInstance);
                            if (rootProp?.GetValue(state) is GameObject go && go != null) return go;
                        }
                    }
                }
                catch (Exception)
                {
                    reflectionFailed = true;
                }
            }

            var sel = Selection.activeGameObject;
            if (sel == null) return null;
            var animator = sel.GetComponentInParent<Animator>(true);
            if (animator != null) return animator.gameObject;
            var legacy = sel.GetComponentInParent<Animation>(true);
            return legacy != null ? legacy.gameObject : null;
        }

        public static bool Switch(AnimationClip clip, ClipGroup group, bool openWindowIfMissing, string notification)
        {
            if (clip == null) return false;
            var w = FindAnimationWindow();
            if (w == null)
            {
                if (!openWindowIfMissing)
                {
                    Notify("Open the Animation window first");
                    return false;
                }
                w = EditorWindow.GetWindow<AnimationWindow>();
                cachedAnimWindow = w;
            }

            w.animationClip = clip;
            if (AutoPreview && w.canPreview && !w.previewing) w.previewing = true;

            if (group != null)
            {
                currentGroupKey = group.key;
                lastInGroup[group.key] = clip;
            }

            w.Repaint();
            SceneView.RepaintAll();
            if (notification != null) Notify(notification);
            RaiseChanged();
            return true;
        }

        /// Хоткеи: dir = +1 / -1; acrossGroups = прыжок между группами.
        public static void Step(int dir, bool acrossGroups)
        {
            var w = FindAnimationWindow();
            if (w == null)
            {
                Notify("Open the Animation window first");
                return;
            }

            var root = GetActiveRoot(w);
            var groups = BuildGroups(root, Mode);
            if (groups.Count == 0)
            {
                Notify("No clips on the selected animator");
                return;
            }

            var cur = w.animationClip;
            int gi = groups.FindIndex(g => g.key == currentGroupKey && g.clips.Contains(cur));
            if (gi < 0) gi = groups.FindIndex(g => g.clips.Contains(cur));
            if (gi < 0)
            {
                var first = groups[0];
                Switch(first.clips[0], first, false, Label(first, first.clips[0]));
                return;
            }

            if (!acrossGroups)
            {
                var g = groups[gi];
                int n = g.clips.Count;
                int ci = g.clips.IndexOf(cur);
                int ni = Wrap(ci + dir, n);
                Switch(g.clips[ni], g, false, $"{g.clips[ni].name}    {ni + 1}/{n}");
            }
            else
            {
                lastInGroup[groups[gi].key] = cur;
                var g = groups[Wrap(gi + dir, groups.Count)];
                var target = lastInGroup.TryGetValue(g.key, out var last) && g.clips.Contains(last) ? last : g.clips[0];
                Switch(target, g, false, Label(g, target));
            }
        }

        static string Label(ClipGroup g, AnimationClip c) => $"{g.title}:  {c.name}";

        static int Wrap(int i, int n) => ((i % n) + n) % n;

        static void Notify(string text)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv != null) sv.ShowNotification(new GUIContent(text), 0.8);
        }

        // ---- grouping ----

        public static List<AnimationClip> GetClips(GameObject root)
        {
            if (root == null) return new List<AnimationClip>();
            return AnimationUtility.GetAnimationClips(root).Where(c => c != null).Distinct().ToList();
        }

        public static List<ClipGroup> BuildGroups(GameObject root, GroupMode mode)
        {
            var result = new List<ClipGroup>();
            var clips = GetClips(root);
            if (clips.Count == 0) return result;

            switch (mode)
            {
                case GroupMode.AnimatorLayer:
                    if (!BuildByLayer(root, clips, result)) BuildByPrefix(clips, result);
                    break;
                case GroupMode.Folder:
                    BuildByFolder(clips, result);
                    break;
                default:
                    BuildByPrefix(clips, result);
                    break;
            }

            foreach (var g in result) g.clips.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));
            return result;
        }

        /// "Skin12" -> "Skin" + "12";  "Hair_Pink" -> "Hair" + "Pink";  "Idle" -> null.
        public static string SplitName(string name, out string suffix)
        {
            var m = TrailingNumber.Match(name);
            if (m.Success && m.Groups[1].Length > 0)
            {
                suffix = m.Groups[2].Value;
                return m.Groups[1].Value.Trim();
            }

            int i = name.LastIndexOfAny(Separators);
            if (i > 0 && i < name.Length - 1)
            {
                suffix = name.Substring(i + 1);
                return name.Substring(0, i).Trim();
            }

            suffix = name;
            return null;
        }

        static void BuildByPrefix(List<AnimationClip> clips, List<ClipGroup> result)
        {
            var map = new Dictionary<string, ClipGroup>(StringComparer.OrdinalIgnoreCase);
            var other = new ClipGroup { key = "prefix:~other", title = "Other" };

            foreach (var clip in clips)
            {
                var prefix = SplitName(clip.name, out _);
                if (string.IsNullOrEmpty(prefix))
                {
                    other.clips.Add(clip);
                    continue;
                }

                if (!map.TryGetValue(prefix, out var g))
                {
                    g = new ClipGroup { key = "prefix:" + prefix.ToLowerInvariant(), title = prefix, shortLabels = true };
                    map[prefix] = g;
                }
                g.clips.Add(clip);
            }

            var groups = new List<ClipGroup>();
            foreach (var g in map.Values)
            {
                if (g.clips.Count > 1) groups.Add(g);
                else other.clips.AddRange(g.clips); // группы из одного клипа не нужны
            }

            groups.Sort((a, b) => EditorUtility.NaturalCompare(a.title, b.title));
            result.AddRange(groups);
            if (other.clips.Count > 0) result.Add(other);
        }

        static void BuildByFolder(List<AnimationClip> clips, List<ClipGroup> result)
        {
            var map = new Dictionary<string, ClipGroup>();
            foreach (var clip in clips)
            {
                var path = AssetDatabase.GetAssetPath(clip);
                var dir = string.IsNullOrEmpty(path) ? "(not an asset)" : (Path.GetDirectoryName(path) ?? "").Replace('\\', '/');
                if (!map.TryGetValue(dir, out var g))
                {
                    var parts = dir.Split('/');
                    var title = parts.Length >= 2 ? parts[parts.Length - 2] + "/" + parts[parts.Length - 1] : dir;
                    g = new ClipGroup { key = "folder:" + dir, title = title, tooltip = dir };
                    map[dir] = g;
                }
                g.clips.Add(clip);
            }

            var groups = map.Values.ToList();
            groups.Sort((a, b) => EditorUtility.NaturalCompare(a.tooltip, b.tooltip));
            result.AddRange(groups);
        }

        static bool BuildByLayer(GameObject root, List<AnimationClip> all, List<ClipGroup> result)
        {
            var animator = root.GetComponent<Animator>();
            RuntimeAnimatorController rac = animator != null ? animator.runtimeAnimatorController : null;

            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            while (rac is AnimatorOverrideController aoc)
            {
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(list);
                foreach (var kv in list)
                    if (kv.Key != null && kv.Value != null && !overrides.ContainsKey(kv.Key))
                        overrides[kv.Key] = kv.Value;
                rac = aoc.runtimeAnimatorController;
            }

            var controller = rac as AnimatorController;
            if (controller == null) return false;

            var allowed = new HashSet<AnimationClip>(all);
            var seen = new HashSet<AnimationClip>();
            var layers = controller.layers;

            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                bool synced = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < layers.Length;
                var sm = synced ? layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;

                var g = new ClipGroup { key = "layer:" + i + ":" + layer.name, title = layer.name };
                CollectStateMachine(sm, synced ? layer : null, g.clips, overrides, allowed);
                if (g.clips.Count == 0) continue;

                result.Add(g);
                seen.UnionWith(g.clips);
            }

            var rest = all.Where(c => !seen.Contains(c)).ToList();
            if (rest.Count > 0)
            {
                var other = new ClipGroup { key = "layer:~other", title = "Other" };
                other.clips.AddRange(rest);
                result.Add(other);
            }
            return true;
        }

        static void CollectStateMachine(AnimatorStateMachine sm, AnimatorControllerLayer syncedLayer, List<AnimationClip> into,
            Dictionary<AnimationClip, AnimationClip> overrides, HashSet<AnimationClip> allowed)
        {
            if (sm == null) return;
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                var motion = syncedLayer != null ? syncedLayer.GetOverrideMotion(cs.state) : cs.state.motion;
                CollectMotion(motion, into, overrides, allowed);
            }
            foreach (var child in sm.stateMachines)
                CollectStateMachine(child.stateMachine, syncedLayer, into, overrides, allowed);
        }

        static void CollectMotion(Motion motion, List<AnimationClip> into, Dictionary<AnimationClip, AnimationClip> overrides,
            HashSet<AnimationClip> allowed)
        {
            if (motion is AnimationClip clip)
            {
                if (overrides.TryGetValue(clip, out var o)) clip = o;
                if (allowed.Contains(clip) && !into.Contains(clip)) into.Add(clip);
            }
            else if (motion is BlendTree tree)
            {
                foreach (var child in tree.children) CollectMotion(child.motion, into, overrides, allowed);
            }
        }

        // ---- clip analysis (материалы для превью) ----

        public static ClipInfo GetInfo(AnimationClip clip)
        {
            if (infoCache.TryGetValue(clip, out var info)) return info;

            info = new ClipInfo();
            var tip = new StringBuilder(clip.name);
            var search = new StringBuilder(clip.name);

            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!b.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) continue;
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (keys == null || keys.Length == 0) continue;
                var mat = keys[0].value as Material;
                if (mat == null) continue;

                if (!info.materials.Contains(mat))
                {
                    info.materials.Add(mat);
                    search.Append(' ').Append(mat.name);
                }
                tip.Append('\n').Append(string.IsNullOrEmpty(b.path) ? "(root)" : b.path).Append("  ->  ").Append(mat.name);
            }

            info.tooltip = tip.ToString();
            info.searchText = search.ToString().ToLowerInvariant();
            infoCache[clip] = info;
            return info;
        }

        public static Texture GetMainTexture(Material m)
        {
            foreach (var prop in new[] { "_MainTex", "_BaseMap", "_BaseColorMap" })
            {
                if (!m.HasProperty(prop)) continue;
                var t = m.GetTexture(prop);
                if (t != null) return t;
            }
            return null;
        }

        // ---- favorites (на проект, по GUID) ----

        static string FavKey => Prefs + "Favorites." + Application.dataPath;

        static HashSet<string> Favorites
        {
            get
            {
                if (favorites == null)
                    favorites = new HashSet<string>(EditorPrefs.GetString(FavKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                return favorites;
            }
        }

        static string FavId(AnimationClip c) => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(c)) + ":" + c.name;

        public static bool IsFavorite(AnimationClip c) => c != null && Favorites.Contains(FavId(c));

        public static void ToggleFavorite(AnimationClip c)
        {
            var id = FavId(c);
            if (!Favorites.Remove(id)) Favorites.Add(id);
            EditorPrefs.SetString(FavKey, string.Join(";", Favorites));
            RaiseChanged();
        }
    }

    // ------------------------------------------------------------------------
    // Хоткеи
    // ------------------------------------------------------------------------
    internal static class ClipBrowserShortcuts
    {
        [Shortcut("Clip Browser/Next Clip In Group", KeyCode.DownArrow, ShortcutModifiers.Alt)]
        static void NextClip() => ClipBrowserCore.Step(+1, false);

        [Shortcut("Clip Browser/Previous Clip In Group", KeyCode.UpArrow, ShortcutModifiers.Alt)]
        static void PrevClip() => ClipBrowserCore.Step(-1, false);

        [Shortcut("Clip Browser/Next Group", KeyCode.DownArrow, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        static void NextGroup() => ClipBrowserCore.Step(+1, true);

        [Shortcut("Clip Browser/Previous Group", KeyCode.UpArrow, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        static void PrevGroup() => ClipBrowserCore.Step(-1, true);
    }

    // ------------------------------------------------------------------------
    // Окно
    // ------------------------------------------------------------------------
    public sealed class ClipBrowserWindow : EditorWindow
    {
        const float Spacing = 4f;
        const float LabelHeight = 16f;
        const float Padding = 6f;

        [SerializeField] string search = "";
        [SerializeField] float tileSize = 72f;
        [SerializeField] bool textureMode;
        [SerializeField] List<string> collapsed = new List<string>();

        Vector2 scroll;
        GameObject root;
        AnimationClip lastSeenClip;
        List<ClipGroup> groups = new List<ClipGroup>();
        bool dirty = true;
        bool waitingPreviews;

        GUIStyle labelStyle, activeLabelStyle, foldoutStyle, activeFoldoutStyle;

        static Color BgColor => EditorGUIUtility.isProSkin ? new Color(0.17f, 0.17f, 0.17f) : new Color(0.72f, 0.72f, 0.72f);
        static readonly Color ActiveColor = new Color(0.27f, 0.62f, 1f);
        static readonly Color HoverColor = new Color(1f, 1f, 1f, 0.35f);
        static readonly Color FavColor = new Color(1f, 0.8f, 0.2f);

        [MenuItem("Window/Animation/Clip Browser")]
        static void Open()
        {
            var w = GetWindow<ClipBrowserWindow>();
            w.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Clip Browser", EditorGUIUtility.IconContent("AnimationClip Icon").image);
            wantsMouseMove = true;
            AssetPreview.SetPreviewTextureCacheSize(256);
            ClipBrowserCore.Changed += MarkDirty;
        }

        void OnDisable() => ClipBrowserCore.Changed -= MarkDirty;
        void OnSelectionChange() => MarkDirty();
        void OnFocus() => MarkDirty();

        void MarkDirty()
        {
            dirty = true;
            Repaint();
        }

        void OnInspectorUpdate()
        {
            var aw = ClipBrowserCore.FindAnimationWindow();
            var r = ClipBrowserCore.GetActiveRoot(aw);
            var c = aw != null ? aw.animationClip : null;
            if (r != root) dirty = true;
            if (r != root || c != lastSeenClip || waitingPreviews || AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        void InitStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            activeLabelStyle = new GUIStyle(labelStyle) { fontStyle = FontStyle.Bold };
            activeLabelStyle.normal.textColor = ActiveColor;
            foldoutStyle = new GUIStyle(EditorStyles.foldout);
            activeFoldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
        }

        void OnGUI()
        {
            InitStyles();
            var e = Event.current;
            if (e.type == EventType.MouseMove) Repaint();
            if (e.type == EventType.Repaint) waitingPreviews = false;

            var aw = ClipBrowserCore.FindAnimationWindow();
            var newRoot = ClipBrowserCore.GetActiveRoot(aw);
            if (newRoot != root)
            {
                root = newRoot;
                dirty = true;
            }
            if (dirty && e.type == EventType.Layout)
            {
                groups = ClipBrowserCore.BuildGroups(root, ClipBrowserCore.Mode);
                dirty = false;
            }

            DrawToolbar();

            if (aw == null)
            {
                EditorGUILayout.HelpBox("Open the Animation window (Ctrl+6).", MessageType.Info);
                if (GUILayout.Button("Open Animation Window")) EditorApplication.ExecuteMenuItem("Window/Animation/Animation");
                return;
            }
            if (root == null)
            {
                EditorGUILayout.HelpBox("Select an object with an Animator (e.g. your avatar root).", MessageType.Info);
                return;
            }

            var current = aw.animationClip;
            lastSeenClip = current;
            EditorGUILayout.LabelField($"{root.name}   >   {(current != null ? current.name : "-")}", EditorStyles.miniBoldLabel);

            if (groups.Count == 0)
            {
                EditorGUILayout.HelpBox("This animator has no clips.", MessageType.Info);
                return;
            }

            var tokens = (search ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float width = position.width - 14f - Padding * 2f; // минус вертикальный скроллбар

            scroll = EditorGUILayout.BeginScrollView(scroll);

            var favGroup = new ClipGroup { key = "~favorites", title = "Favorites" };
            foreach (var g in groups)
                foreach (var c in g.clips)
                    if (!favGroup.clips.Contains(c) && ClipBrowserCore.IsFavorite(c))
                        favGroup.clips.Add(c);
            if (favGroup.clips.Count > 0)
            {
                favGroup.clips.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));
                DrawGroup(favGroup, null, current, tokens, width);
            }

            foreach (var g in groups) DrawGroup(g, g, current, tokens, width);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField("Alt+Up/Down: clip in group    Alt+Shift+Up/Down: group", EditorStyles.centeredGreyMiniLabel);
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                var mode = (GroupMode)EditorGUILayout.EnumPopup(ClipBrowserCore.Mode, EditorStyles.toolbarPopup, GUILayout.Width(105));
                if (EditorGUI.EndChangeCheck())
                {
                    ClipBrowserCore.Mode = mode;
                    dirty = true;
                }

                textureMode = GUILayout.Toggle(textureMode,
                    new GUIContent("Tex", "Show the material's main texture instead of a rendered sphere"),
                    EditorStyles.toolbarButton, GUILayout.Width(34));

                bool auto = ClipBrowserCore.AutoPreview;
                bool newAuto = GUILayout.Toggle(auto,
                    new GUIContent("Preview", "Turn on Animation window preview when switching clips"),
                    EditorStyles.toolbarButton, GUILayout.Width(56));
                if (newAuto != auto) ClipBrowserCore.AutoPreview = newAuto;

                search = GUILayout.TextField(search ?? "", EditorStyles.toolbarSearchField, GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));

                tileSize = GUILayout.HorizontalSlider(tileSize, 40f, 160f, GUILayout.Width(70));

                if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Refresh").image, "Rebuild list and previews"),
                        EditorStyles.toolbarButton, GUILayout.Width(26)))
                {
                    ClipBrowserCore.ClearCache();
                    dirty = true;
                }
            }
        }

        void DrawGroup(ClipGroup g, ClipGroup switchGroup, AnimationClip current, string[] tokens, float width)
        {
            List<AnimationClip> clips = g.clips;
            if (tokens.Length > 0)
            {
                clips = g.clips.Where(c =>
                {
                    var text = ClipBrowserCore.GetInfo(c).searchText;
                    return tokens.All(t => text.Contains(t));
                }).ToList();
            }
            if (clips.Count == 0) return;

            bool searching = tokens.Length > 0;
            bool isCollapsed = !searching && collapsed.Contains(g.key);
            bool hasCurrent = current != null && clips.Contains(current);

            var header = new GUIContent($"{g.title}   ({clips.Count})", g.tooltip);
            bool open = EditorGUILayout.Foldout(!isCollapsed, header, true, hasCurrent ? activeFoldoutStyle : foldoutStyle);
            if (!searching)
            {
                if (open && isCollapsed) collapsed.Remove(g.key);
                else if (!open && !isCollapsed) collapsed.Add(g.key);
            }
            if (!open) return;

            int perRow = Mathf.Max(1, Mathf.FloorToInt((width + Spacing) / (tileSize + Spacing)));
            int rows = (clips.Count + perRow - 1) / perRow;
            for (int r = 0; r < rows; r++)
            {
                var rowRect = GUILayoutUtility.GetRect(width, tileSize + LabelHeight + Spacing, GUILayout.ExpandWidth(true));
                for (int k = 0; k < perRow; k++)
                {
                    int idx = r * perRow + k;
                    if (idx >= clips.Count) break;
                    var rect = new Rect(rowRect.x + Padding + k * (tileSize + Spacing), rowRect.y, tileSize, tileSize + LabelHeight);
                    DrawTile(rect, clips[idx], g, switchGroup, current);
                }
            }
            GUILayout.Space(Spacing);
        }

        void DrawTile(Rect rect, AnimationClip clip, ClipGroup g, ClipGroup switchGroup, AnimationClip current)
        {
            var info = ClipBrowserCore.GetInfo(clip);
            var e = Event.current;
            var thumb = new Rect(rect.x, rect.y, rect.width, rect.width);
            var labelRect = new Rect(rect.x - 2, thumb.yMax, rect.width + 4, LabelHeight);
            bool hover = rect.Contains(e.mousePosition);
            bool active = clip == current;

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(thumb, BgColor);

                if (info.materials.Count > 0)
                {
                    DrawMaterial(thumb, info.materials[0]);
                    if (info.materials.Count > 1)
                    {
                        // второй материал (например голова) — маленькой вставкой в углу
                        float s = Mathf.Round(thumb.width * 0.38f);
                        var inset = new Rect(thumb.xMax - s - 2, thumb.yMax - s - 2, s, s);
                        EditorGUI.DrawRect(new Rect(inset.x - 1, inset.y - 1, inset.width + 2, inset.height + 2), BgColor);
                        DrawMaterial(inset, info.materials[1]);
                    }
                }
                else
                {
                    var icon = AssetPreview.GetMiniThumbnail(clip);
                    if (icon != null)
                    {
                        float s = Mathf.Min(32f, thumb.width * 0.5f);
                        GUI.DrawTexture(new Rect(thumb.center.x - s / 2, thumb.center.y - s / 2, s, s), icon, ScaleMode.ScaleToFit);
                    }
                }

                if (ClipBrowserCore.IsFavorite(clip)) EditorGUI.DrawRect(new Rect(thumb.x + 3, thumb.y + 3, 7, 7), FavColor);

                if (active) DrawOutline(thumb, ActiveColor, 2f);
                else if (hover) DrawOutline(thumb, HoverColor, 1f);
            }

            string text = clip.name;
            if (g.shortLabels) ClipBrowserCore.SplitName(clip.name, out text);
            GUI.Label(labelRect, text, active ? activeLabelStyle : labelStyle);
            GUI.Label(rect, new GUIContent(string.Empty, info.tooltip), GUIStyle.none);

            if (e.type == EventType.MouseDown && hover)
            {
                if (e.button == 0)
                {
                    if (e.clickCount == 2) EditorGUIUtility.PingObject(clip);
                    else ClipBrowserCore.Switch(clip, switchGroup, true, null);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    ShowContextMenu(clip, info, switchGroup);
                    e.Use();
                }
            }
        }

        void DrawMaterial(Rect r, Material m)
        {
            Texture tex = textureMode ? ClipBrowserCore.GetMainTexture(m) : null;
            if (tex == null)
            {
                tex = AssetPreview.GetAssetPreview(m);
                if (tex == null)
                {
                    waitingPreviews = true;
                    tex = AssetPreview.GetMiniThumbnail(m);
                }
            }
            if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.ScaleAndCrop);
        }

        static void DrawOutline(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        void ShowContextMenu(AnimationClip clip, ClipInfo info, ClipGroup switchGroup)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Switch To"), false, () => ClipBrowserCore.Switch(clip, switchGroup, true, null));
            menu.AddItem(new GUIContent(ClipBrowserCore.IsFavorite(clip) ? "Remove from Favorites" : "Add to Favorites"), false,
                () => ClipBrowserCore.ToggleFavorite(clip));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Ping Clip"), false, () => EditorGUIUtility.PingObject(clip));
            for (int i = 0; i < info.materials.Count; i++)
            {
                var m = info.materials[i];
                menu.AddItem(new GUIContent($"Ping Material/{i + 1}. {m.name}"), false, () => EditorGUIUtility.PingObject(m));
            }
            menu.ShowAsContext();
        }
    }
}
#endif
