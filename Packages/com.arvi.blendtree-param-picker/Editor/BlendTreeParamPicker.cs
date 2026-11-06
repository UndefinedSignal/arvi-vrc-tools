#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

// Ставится через VRChat Creator Companion (пакет com.arvi.blendtree-param-picker)
// или вручную — в любую папку Editor/ внутри Assets.
// Оборачивает стандартный инспектор Blend Tree и добавляет сверху блок
// «Параметры (поиск)»: плоский список Float-параметров с полем поиска
// вместо вложенных подменю по «/». Стандартный инспектор остаётся как был.
namespace ArVi.EditorTools
{
    [CustomEditor(typeof(BlendTree))]
    public class BlendTreeParamPicker : Editor
    {
        const string FoldoutKey = "ArVi.BlendTreeParamPicker.Foldout";

        static readonly Type InnerType =
            FindType("UnityEditor.BlendTreeInspector", "UnityEditor.Graphs.BlendTreeInspector");

        Editor inner;
        readonly AdvancedDropdownState dropdownState = new AdvancedDropdownState();

        void OnEnable()
        {
            if (InnerType != null) inner = CreateEditor(targets, InnerType);
        }

        void OnDisable()
        {
            if (inner != null) DestroyImmediate(inner);
            inner = null;
        }

        // ---------- Наш блок ----------

        public override void OnInspectorGUI()
        {
            if (targets.Length == 1) DrawPicker((BlendTree)target);

            if (inner != null) inner.OnInspectorGUI();
            else base.OnInspectorGUI();
        }

        void DrawPicker(BlendTree tree)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.inspectorDefaultMargins))
            {
                bool open = EditorPrefs.GetBool(FoldoutKey, true);
                bool now = EditorGUILayout.Foldout(open, "Параметры (поиск)", true, EditorStyles.foldoutHeader);
                if (now != open) EditorPrefs.SetBool(FoldoutKey, now);
                if (!now) return;

                var controller = ResolveController(tree);
                if (controller == null)
                {
                    EditorGUILayout.HelpBox("Не нашёл контроллер этого Blend Tree. Открой контроллер в окне Animator.", MessageType.Info);
                    return;
                }

                var floats = controller.parameters
                    .Where(p => p.type == AnimatorControllerParameterType.Float)
                    .Select(p => p.name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var floatSet = new HashSet<string>(floats);

                EditorGUI.indentLevel++;
                switch (tree.blendType)
                {
                    case BlendTreeType.Simple1D:
                        Row("Parameter", tree.blendParameter, floats, floatSet, n => SetBlend(tree, n, false));
                        break;

                    case BlendTreeType.SimpleDirectional2D:
                    case BlendTreeType.FreeformDirectional2D:
                    case BlendTreeType.FreeformCartesian2D:
                        Row("Parameter X", tree.blendParameter, floats, floatSet, n => SetBlend(tree, n, false));
                        Row("Parameter Y", tree.blendParameterY, floats, floatSet, n => SetBlend(tree, n, true));
                        break;

                    case BlendTreeType.Direct:
                        var ch = tree.children;
                        if (ch.Length == 0) EditorGUILayout.LabelField("Нет дочерних моушенов", EditorStyles.miniLabel);
                        for (int i = 0; i < ch.Length; i++)
                        {
                            int idx = i;
                            string label = ch[i].motion != null ? ch[i].motion.name : $"#{i} (None)";
                            Row(label, ch[i].directBlendParameter, floats, floatSet, n => SetDirect(tree, idx, n));
                        }
                        break;
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(4);
            }
        }

        void Row(string label, string current, string[] options, HashSet<string> valid, Action<string> onPick)
        {
            var rect = EditorGUILayout.GetControlRect();
            var field = EditorGUI.PrefixLabel(rect, new GUIContent(label, label));

            bool missing = !valid.Contains(current ?? "");
            string text = string.IsNullOrEmpty(current) ? "—" : current;
            string tip = missing ? text + "\n⚠ Нет такого Float-параметра в контроллере" : text;
            var content = new GUIContent(FitLeft(text, EditorStyles.popup, field.width - 20f), tip);

            var old = GUI.contentColor;
            if (missing) GUI.contentColor = new Color(1f, 0.55f, 0.55f);
            if (EditorGUI.DropdownButton(field, content, FocusType.Keyboard, EditorStyles.popup))
                new ParamDropdown(dropdownState, options, current, onPick).Show(field);
            GUI.contentColor = old;
        }

        // Длинные имена режем слева: хвост (BrowExpressionLeft) важнее префикса (OSCm/Proxy/FT/v2/).
        static string FitLeft(string s, GUIStyle style, float width)
        {
            if (width <= 0f || style.CalcSize(new GUIContent(s)).x <= width) return s;
            for (int cut = 1; cut < s.Length; cut++)
            {
                string t = "…" + s.Substring(cut);
                if (style.CalcSize(new GUIContent(t)).x <= width) return t;
            }
            return s;
        }

        // ---------- Запись ----------

        void SetBlend(BlendTree tree, string name, bool y)
        {
            Undo.RecordObject(tree, "Change Blend Parameter");
            if (y) tree.blendParameterY = name;
            else tree.blendParameter = name;
            Commit(tree);
        }

        void SetDirect(BlendTree tree, int index, string name)
        {
            Undo.RecordObject(tree, "Change Direct Parameter");
            var ch = tree.children;
            if (index < 0 || index >= ch.Length) return;
            ch[index].directBlendParameter = name;
            tree.children = ch;
            Commit(tree);
        }

        void Commit(BlendTree tree)
        {
            EditorUtility.SetDirty(tree);
            if (this != null)
            {
                serializedObject.Update();
                if (inner != null) inner.serializedObject.Update();
                Repaint();
            }
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); // чтобы обновился и граф в Animator
        }

        // ---------- Поиск контроллера ----------

        static AnimatorController ResolveController(BlendTree tree)
        {
            string path = AssetDatabase.GetAssetPath(tree);
            if (!string.IsNullOrEmpty(path))
            {
                var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (c != null) return c;
            }
            // Blend Tree лежит отдельным ассетом — берём контроллер, открытый в окне Animator.
            return GetStatic<AnimatorController>(InnerType, "currentController") ?? GetAnimatorWindowController();
        }

        static AnimatorController GetAnimatorWindowController()
        {
            var toolType = FindType("UnityEditor.Graphs.AnimatorControllerTool");
            var tool = GetStatic<object>(toolType, "tool");
            if (tool == null) return null;
            try
            {
                return toolType.GetProperty("animatorController",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(tool) as AnimatorController;
            }
            catch { return null; }
        }

        static T GetStatic<T>(Type type, string name) where T : class
        {
            if (type == null) return null;
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var f = type.GetField(name, F);
                if (f != null) return f.GetValue(null) as T;
                var p = type.GetProperty(name, F);
                if (p != null) return p.GetValue(null) as T;
            }
            catch { }
            return null;
        }

        static Type FindType(params string[] fullNames)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var n in fullNames)
                {
                    var t = asm.GetType(n, false);
                    if (t != null) return t;
                }
            return null;
        }

        // ---------- Проброс в стандартный инспектор ----------

        protected override void OnHeaderGUI()
        {
            if (inner != null) inner.DrawHeader();
            else base.OnHeaderGUI();
        }

        public override bool UseDefaultMargins() => inner != null ? inner.UseDefaultMargins() : base.UseDefaultMargins();
        public override bool RequiresConstantRepaint() => inner != null ? inner.RequiresConstantRepaint() : base.RequiresConstantRepaint();
        public override bool HasPreviewGUI() => inner != null ? inner.HasPreviewGUI() : base.HasPreviewGUI();
        public override GUIContent GetPreviewTitle() => inner != null ? inner.GetPreviewTitle() : base.GetPreviewTitle();
        public override string GetInfoString() => inner != null ? inner.GetInfoString() : base.GetInfoString();

        public override void OnPreviewSettings()
        {
            if (inner != null) inner.OnPreviewSettings();
            else base.OnPreviewSettings();
        }

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            if (inner != null) inner.OnPreviewGUI(r, background);
            else base.OnPreviewGUI(r, background);
        }

        public override void OnInteractivePreviewGUI(Rect r, GUIStyle background)
        {
            if (inner != null) inner.OnInteractivePreviewGUI(r, background);
            else base.OnInteractivePreviewGUI(r, background);
        }
    }

    // Плоский список с поиском. Поиск по словам через пробел: «proxy brow left».
    class ParamDropdown : AdvancedDropdown
    {
        readonly string[] names;
        readonly string current;
        readonly Action<string> onPick;

        public ParamDropdown(AdvancedDropdownState state, string[] names, string current, Action<string> onPick)
            : base(state)
        {
            this.names = names;
            this.current = current;
            this.onPick = onPick;
            minimumSize = new Vector2(380f, 420f);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Float-параметры");
            for (int i = 0; i < names.Length; i++)
            {
                string shown = names[i] == current ? "● " + names[i] : names[i];
                root.AddChild(new AdvancedDropdownItem(shown) { id = i });
            }
            if (names.Length == 0)
                root.AddChild(new AdvancedDropdownItem("(в контроллере нет Float-параметров)") { enabled = false, id = -1 });
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < names.Length) onPick(names[item.id]);
        }
    }
}
#endif
