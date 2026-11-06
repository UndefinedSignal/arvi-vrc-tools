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
// Свой инспектор для VRC Avatar Parameter Driver: поля Destination / Source открывают
// плоский список параметров с поиском вместо огромного нативного попапа SDK.
// Все записи драйвера видны сразу, у каждой свои кнопки ↑ ↓ ×.
// Внизу есть переключатель обратно на стандартный инспектор SDK.
//
// Почему Unity берёт этот инспектор, а не SDK-шный: на один тип два CustomEditor, и Unity
// (CustomEditorAttributes.SortUnityTypesFirst) выбирает первый по string.CompareOrdinal
// полного имени класса. "ArVi.EditorTools.ParameterDriverPicker" < "AvatarParameterDriverEditor"
// ('r' < 'v'), поэтому НЕ переименовывай namespace/класс во что-то, что сортируется позже.
namespace ArVi.EditorTools
{
    [CustomEditor(typeof(VRCAvatarParameterDriver))]
    public class ParameterDriverPicker : Editor
    {
        const string SdkInspectorKey = "ArVi.ParameterDriverPicker.UseSdkInspector";

        const string InfoText =
            "Меняет параметры на этом и всех остальных контроллерах аватара.\n" +
            "По сети синхронизируются только параметры из VRCExpressionParameters; " +
            "у синхронизируемых Int ограничен 0…255, Float −1…1.";

        static readonly Type InnerType = FindType("AvatarParameterDriverEditor");

        Editor inner; // стандартный инспектор SDK — для запасного режима и мультиредактирования
        readonly AdvancedDropdownState dropdownState = new AdvancedDropdownState();

        AnimatorController controller;
        AnimatorControllerParameter[] sortedParams = new AnimatorControllerParameter[0];
        readonly Dictionary<string, AnimatorControllerParameterType> paramTypes =
            new Dictionary<string, AnimatorControllerParameterType>();

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
            bool multi = targets.Length > 1;
            bool useSdk = multi || EditorPrefs.GetBool(SdkInspectorKey, false);

            if (useSdk && inner != null) inner.OnInspectorGUI();
            else DrawOwnInspector();

            if (!multi && inner != null) DrawModeSwitch(useSdk);
        }

        void DrawModeSwitch(bool useSdk)
        {
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                bool now = GUILayout.Toggle(useSdk, "Стандартный инспектор SDK", EditorStyles.miniButton);
                if (now != useSdk) EditorPrefs.SetBool(SdkInspectorKey, now);
            }
        }

        // ---------- Наш инспектор ----------

        void DrawOwnInspector()
        {
            serializedObject.Update();
            RefreshParameters((VRCAvatarParameterDriver)target);

            EditorGUILayout.HelpBox(InfoText, MessageType.Info);

            var localOnly = serializedObject.FindProperty("localOnly");
            EditorGUILayout.PropertyField(localOnly);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("debugString"));

            var list = serializedObject.FindProperty("parameters");

            bool addOrRandom = false;
            for (int i = 0; i < list.arraySize; i++)
            {
                string op = OpName(list.GetArrayElementAtIndex(i).FindPropertyRelative("type"));
                if (op == "Add" || op == "Random") addOrRandom = true;
            }
            if (addOrRandom && !localOnly.boolValue)
                EditorGUILayout.HelpBox(
                    "Add и Random на удалённых копиях аватара могут дать другой результат. " +
                    "Для них лучше включить Local Only и писать в синхронизируемый параметр.",
                    MessageType.Warning);

            if (controller == null)
                EditorGUILayout.HelpBox(
                    "Не нашёл контроллер этого драйвера — списка параметров нет, имена можно вписать вручную.",
                    MessageType.Info);

            int moveUp = -1, moveDown = -1, remove = -1;
            for (int i = 0; i < list.arraySize; i++)
                DrawEntry(list, i, ref moveUp, ref moveDown, ref remove);

            if (GUILayout.Button("Add"))
            {
                list.arraySize++;
                var el = list.GetArrayElementAtIndex(list.arraySize - 1);
                el.FindPropertyRelative("type").enumValueIndex = 0; // Set
                el.FindPropertyRelative("name").stringValue = "";
                el.FindPropertyRelative("source").stringValue = "";
            }

            if (moveUp > 0) list.MoveArrayElement(moveUp, moveUp - 1);
            else if (moveDown >= 0 && moveDown < list.arraySize - 1) list.MoveArrayElement(moveDown, moveDown + 1);
            else if (remove >= 0) list.DeleteArrayElementAtIndex(remove);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawEntry(SerializedProperty list, int i, ref int moveUp, ref int moveDown, ref int remove)
        {
            var el = list.GetArrayElementAtIndex(i);
            var type = el.FindPropertyRelative("type");
            string op = OpName(type);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"#{i + 1}  {op}", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(i == 0))
                        if (GUILayout.Button(new GUIContent("↑", "Выше"), EditorStyles.miniButtonLeft, GUILayout.Width(24))) moveUp = i;
                    using (new EditorGUI.DisabledScope(i == list.arraySize - 1))
                        if (GUILayout.Button(new GUIContent("↓", "Ниже"), EditorStyles.miniButtonMid, GUILayout.Width(24))) moveDown = i;
                    if (GUILayout.Button(new GUIContent("×", "Удалить"), EditorStyles.miniButtonRight, GUILayout.Width(24))) remove = i;
                }

                EditorGUILayout.PropertyField(type, new GUIContent("Type"));

                switch (op)
                {
                    case "Set": DrawSet(el, i); break;
                    case "Add": DrawAdd(el, i); break;
                    case "Random": DrawRandom(el, i); break;
                    case "Copy": DrawCopy(el, i); break;
                }
            }
        }

        void DrawSet(SerializedProperty el, int i)
        {
            var dest = ParamRow("Destination", el, "name", i, out _);
            var value = el.FindPropertyRelative("value");
            switch (dest)
            {
                case AnimatorControllerParameterType.Bool:
                    value.floatValue = EditorGUILayout.Toggle("Value", value.floatValue != 0f) ? 1f : 0f;
                    break;
                case AnimatorControllerParameterType.Int:
                    value.floatValue = EditorGUILayout.IntField("Value", (int)value.floatValue);
                    break;
                case AnimatorControllerParameterType.Float:
                    value.floatValue = EditorGUILayout.FloatField("Value", value.floatValue);
                    break;
                // Trigger: значения нет, триггер просто взводится
            }
        }

        void DrawAdd(SerializedProperty el, int i)
        {
            var dest = ParamRow("Destination", el, "name", i, out _);
            var value = el.FindPropertyRelative("value");
            switch (dest)
            {
                case AnimatorControllerParameterType.Int:
                    value.floatValue = EditorGUILayout.IntField("Value", (int)value.floatValue);
                    break;
                case AnimatorControllerParameterType.Float:
                    value.floatValue = EditorGUILayout.FloatField("Value", value.floatValue);
                    break;
                default:
                    EditorGUILayout.HelpBox($"Add не работает с параметрами типа {dest}.", MessageType.Warning);
                    break;
            }
        }

        void DrawRandom(SerializedProperty el, int i)
        {
            var dest = ParamRow("Destination", el, "name", i, out _);
            var min = el.FindPropertyRelative("valueMin");
            var max = el.FindPropertyRelative("valueMax");
            switch (dest)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    EditorGUILayout.PropertyField(el.FindPropertyRelative("chance"));
                    break;
                case AnimatorControllerParameterType.Int:
                    min.floatValue = EditorGUILayout.IntField("Min Value", (int)min.floatValue);
                    max.floatValue = EditorGUILayout.IntField("Max Value", (int)max.floatValue);
                    EditorGUILayout.PropertyField(el.FindPropertyRelative("preventRepeats"));
                    break;
                case AnimatorControllerParameterType.Float:
                    min.floatValue = EditorGUILayout.FloatField("Min Value", min.floatValue);
                    max.floatValue = EditorGUILayout.FloatField("Max Value", max.floatValue);
                    break;
            }
        }

        void DrawCopy(SerializedProperty el, int i)
        {
            var src = ParamRow("Source", el, "source", i, out bool srcFound);
            var dest = ParamRow("Destination", el, "name", i, out _);

            if (dest == AnimatorControllerParameterType.Trigger)
            {
                EditorGUILayout.HelpBox("Copy не может писать в Trigger.", MessageType.Warning);
                return;
            }

            if (srcFound)
            {
                if (src == AnimatorControllerParameterType.Trigger)
                    EditorGUILayout.HelpBox("Source не может быть Trigger.", MessageType.Warning);
                else if (src != dest)
                    EditorGUILayout.HelpBox($"Значение будет сконвертировано из {src} в {dest}.", MessageType.Info);
            }

            var convert = el.FindPropertyRelative("convertRange");
            EditorGUILayout.PropertyField(convert);
            if (!convert.boolValue) return;

            EditorGUI.indentLevel++;
            RangeRow("Source", el.FindPropertyRelative("sourceMin"), el.FindPropertyRelative("sourceMax"));
            RangeRow("Destination", el.FindPropertyRelative("destMin"), el.FindPropertyRelative("destMax"));
            EditorGUI.indentLevel--;
        }

        static void RangeRow(string label, SerializedProperty min, SerializedProperty max)
        {
            var rect = EditorGUILayout.GetControlRect();
            var field = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float half = field.width / 2f;
            float oldLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 30f;
            min.floatValue = EditorGUI.FloatField(new Rect(field.x, field.y, half - 2f, field.height), "Min", min.floatValue);
            max.floatValue = EditorGUI.FloatField(new Rect(field.x + half + 2f, field.y, half - 2f, field.height), "Max", max.floatValue);
            EditorGUIUtility.labelWidth = oldLabel;
            EditorGUI.indentLevel = indent;
        }

        // Строка выбора параметра: кнопка с поиском + текстовое поле для ручного ввода.
        // Возвращает тип параметра (Float, если его нет в контроллере — как в SDK).
        AnimatorControllerParameterType ParamRow(string label, SerializedProperty el, string field, int index, out bool found)
        {
            var prop = el.FindPropertyRelative(field);
            string current = prop.stringValue ?? "";
            found = paramTypes.TryGetValue(current, out var ptype);

            var rect = EditorGUILayout.GetControlRect();
            var fieldRect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            float half = fieldRect.width / 2f;
            var buttonRect = new Rect(fieldRect.x, fieldRect.y, half - 2f, fieldRect.height);
            var textRect = new Rect(fieldRect.x + half + 2f, fieldRect.y, half - 2f, fieldRect.height);

            bool missing = !found && controller != null;
            string text = string.IsNullOrEmpty(current) ? "—" : current;
            string tip = missing ? text + "\n⚠ Нет такого параметра в контроллере" : (found ? $"{text}\n{ptype}" : text);
            var content = new GUIContent(FitLeft(text, EditorStyles.popup, buttonRect.width - 20f), tip);

            var oldColor = GUI.contentColor;
            if (missing) GUI.contentColor = new Color(1f, 0.55f, 0.55f);
            using (new EditorGUI.DisabledScope(controller == null))
                if (EditorGUI.DropdownButton(buttonRect, content, FocusType.Keyboard, EditorStyles.popup))
                    new DriverParamDropdown(dropdownState, sortedParams, current, n => SetName(index, field, n)).Show(buttonRect);
            GUI.contentColor = oldColor;

            prop.stringValue = EditorGUI.DelayedTextField(textRect, current);
            EditorGUI.indentLevel = indent;

            if (missing && !string.IsNullOrEmpty(current))
                EditorGUILayout.HelpBox($"Параметра «{current}» нет в контроллере. Добавь его во вкладке Parameters окна Animator.", MessageType.Warning);

            return found ? ptype : AnimatorControllerParameterType.Float;
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

        static string OpName(SerializedProperty type)
        {
            int i = type.enumValueIndex;
            return i >= 0 && i < type.enumNames.Length ? type.enumNames[i] : "?";
        }

        // ---------- Запись из выпадающего списка ----------

        void SetName(int index, string field, string value)
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

        void RefreshParameters(VRCAvatarParameterDriver driver)
        {
            var contexts = AnimatorController.FindStateMachineBehaviourContext(driver);
            controller = contexts != null && contexts.Length > 0 ? contexts[0].animatorController : null;

            paramTypes.Clear();
            if (controller == null)
            {
                sortedParams = new AnimatorControllerParameter[0];
                return;
            }
            var ps = controller.parameters;
            foreach (var p in ps) paramTypes[p.name] = p.type; // дубликаты имён не роняют инспектор
            sortedParams = ps.OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToArray();
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
