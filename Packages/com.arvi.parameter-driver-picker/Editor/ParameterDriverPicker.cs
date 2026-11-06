#if UNITY_EDITOR && ARVI_VRCSDK3A
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// Ставится через VRChat Creator Companion (пакет com.arvi.parameter-driver-picker).
// Оборачивает инспектор VRC Avatar Parameter Driver из VRChat SDK и добавляет сверху
// блок «Параметры (поиск)»: для каждой записи драйвера — Destination (и Source у Copy)
// с плоским списком параметров и полем поиска вместо огромного нативного попапа.
// Инспектор SDK остаётся под блоком как был.
//
// Почему этот инспектор, а не SDK-шный: на один тип есть два CustomEditor, и Unity
// (CustomEditorAttributes.SortUnityTypesFirst) берёт первый по string.CompareOrdinal
// полного имени класса. "ArVi.EditorTools.ParameterDriverPicker" < "AvatarParameterDriverEditor"
// ('r' < 'v'), поэтому НЕ переименовывай namespace/класс во что-то, что сортируется позже.
namespace ArVi.EditorTools
{
    [CustomEditor(typeof(VRCAvatarParameterDriver))]
    public class ParameterDriverPicker : Editor
    {
        const string FoldoutKey = "ArVi.ParameterDriverPicker.Foldout";

        static readonly Type InnerType = FindType("AvatarParameterDriverEditor");

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

        public override void OnInspectorGUI()
        {
            if (targets.Length == 1) DrawPicker((VRCAvatarParameterDriver)target);

            if (inner != null) inner.OnInspectorGUI();
            else base.OnInspectorGUI();
        }

        // ---------- Наш блок ----------

        void DrawPicker(VRCAvatarParameterDriver driver)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.inspectorDefaultMargins))
            {
                bool open = EditorPrefs.GetBool(FoldoutKey, true);
                bool now = EditorGUILayout.Foldout(open, "Параметры (поиск)", true, EditorStyles.foldoutHeader);
                if (now != open) EditorPrefs.SetBool(FoldoutKey, now);
                if (!now) return;

                var controller = ResolveController(driver);
                if (controller == null)
                {
                    EditorGUILayout.HelpBox("Не нашёл контроллер этого драйвера. Открой его стейт в окне Animator.", MessageType.Info);
                    return;
                }

                var all = controller.parameters
                    .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var types = new Dictionary<string, AnimatorControllerParameterType>();
                foreach (var p in all) types[p.name] = p.type; // дубликаты имён не роняют инспектор

                serializedObject.Update();
                var list = serializedObject.FindProperty("parameters");
                EditorGUI.indentLevel++;
                if (list == null || list.arraySize == 0)
                    EditorGUILayout.LabelField("Нет записей — добавь их кнопкой Add ниже", EditorStyles.miniLabel);
                else
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var el = list.GetArrayElementAtIndex(i);
                        var type = el.FindPropertyRelative("type");
                        string op = type.enumValueIndex >= 0 && type.enumValueIndex < type.enumNames.Length
                            ? type.enumNames[type.enumValueIndex] : "?";

                        if (op == "Copy")
                            Row($"{i + 1} · Copy · Source", i, "source", el, all, types);
                        Row($"{i + 1} · {op}" + (op == "Copy" ? " · Destination" : ""), i, "name", el, all, types);
                    }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(4);
            }
        }

        void Row(string label, int index, string field, SerializedProperty element,
                 AnimatorControllerParameter[] all, Dictionary<string, AnimatorControllerParameterType> types)
        {
            string current = element.FindPropertyRelative(field).stringValue;

            var rect = EditorGUILayout.GetControlRect();
            var fieldRect = EditorGUI.PrefixLabel(rect, new GUIContent(label, label));

            bool missing = !types.TryGetValue(current ?? "", out var ptype);
            string text = string.IsNullOrEmpty(current) ? "—" : current;
            string tip = missing ? text + "\n⚠ Нет такого параметра в контроллере" : $"{text}\n{ptype}";
            var content = new GUIContent(FitLeft(text, EditorStyles.popup, fieldRect.width - 20f), tip);

            var old = GUI.contentColor;
            if (missing) GUI.contentColor = new Color(1f, 0.55f, 0.55f);
            if (EditorGUI.DropdownButton(fieldRect, content, FocusType.Keyboard, EditorStyles.popup))
                new DriverParamDropdown(dropdownState, all, current, n => Set(index, field, n)).Show(fieldRect);
            GUI.contentColor = old;
        }

        // Длинные имена режем слева: хвост важнее префикса (OSCm/Proxy/FT/v2/…).
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

        void Set(int index, string field, string value)
        {
            if (this == null || target == null) return;
            serializedObject.Update();
            var list = serializedObject.FindProperty("parameters");
            if (list == null || index < 0 || index >= list.arraySize) return;
            list.GetArrayElementAtIndex(index).FindPropertyRelative(field).stringValue = value;
            serializedObject.ApplyModifiedProperties(); // c Undo
            if (inner != null) inner.serializedObject.Update();
            Repaint();
        }

        // ---------- Контроллер ----------

        static AnimatorController ResolveController(VRCAvatarParameterDriver driver)
        {
            var contexts = AnimatorController.FindStateMachineBehaviourContext(driver);
            return contexts != null && contexts.Length > 0 ? contexts[0].animatorController : null;
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null && typeof(Editor).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        // ---------- Проброс в инспектор SDK ----------

        public override bool UseDefaultMargins() => inner != null ? inner.UseDefaultMargins() : base.UseDefaultMargins();
        public override bool RequiresConstantRepaint() => inner != null ? inner.RequiresConstantRepaint() : base.RequiresConstantRepaint();
    }

    // Плоский список всех параметров контроллера с поиском.
    // Тип параметра — частью названия, так что поиск «float eye» тоже работает.
    class DriverParamDropdown : AdvancedDropdown
    {
        readonly AnimatorControllerParameter[] parameters;
        readonly string current;
        readonly Action<string> onPick;

        public DriverParamDropdown(AdvancedDropdownState state, AnimatorControllerParameter[] parameters,
                                   string current, Action<string> onPick) : base(state)
        {
            this.parameters = parameters;
            this.current = current;
            this.onPick = onPick;
            minimumSize = new Vector2(380f, 420f);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Параметры");
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                string shown = (p.name == current ? "● " : "") + p.name + "   · " + p.type;
                root.AddChild(new AdvancedDropdownItem(shown) { id = i });
            }
            if (parameters.Length == 0)
                root.AddChild(new AdvancedDropdownItem("(в контроллере нет параметров)") { enabled = false, id = -1 });
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < parameters.Length) onPick(parameters[item.id].name);
        }
    }
}
#endif
