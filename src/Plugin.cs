using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NixiItemInspect
{
    /// <summary>
    /// Item Inspect — осмотр предмета в руках по нажатию клавиши (в стиле inspect из CS2)
    /// и визуальный взмах ножом по ЛКМ.
    /// Анимация процедурная: мод поворачивает и смещает предмет по ключевым кадрам,
    /// а руки персонажа следуют за ним через штатный IK игры.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "nixi.iteminspect";
        public const string Name = "Item Inspect";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        // Пишем и в лог BepInEx, и в консоль Unity, чтобы сообщения были видны в любой консоли.
        internal static new void Info(string m) { Log?.LogInfo(m); Debug.Log("[ItemInspect] " + m); }
        internal static void Error(string m) { Log?.LogError(m); Debug.LogError("[ItemInspect] " + m); }

        // --- Настройки ---
        private ConfigEntry<string> _key;
        private ConfigEntry<bool> _loopHold;
        private ConfigEntry<bool> _bringCloser;
        private ConfigEntry<string> _style;
        private ConfigEntry<string> _itemStyles;
        private ConfigEntry<float> _speed;
        private ConfigEntry<float> _intensity;
        private ConfigEntry<float> _smoothing;
        private ConfigEntry<string> _oneHandItems;
        private ConfigEntry<bool> _oneHandSmall;
        private ConfigEntry<bool> _swingEnabled;
        internal static ConfigEntry<string> SwingItems;
        internal static ConfigEntry<bool> ShowPrompt;
        internal static ConfigEntry<string> PromptText;
        internal static ConfigEntry<string> SwingPromptText;
        internal static string KeyName = "F";

        // --- Состояние, которое читают патчи ---
        internal static bool Active;            // анимация влияет на предмет
        internal static object InspectedItem;   // какой предмет осматриваем
        internal static Quaternion LocalRot = Quaternion.identity;
        internal static Vector3 LocalPos = Vector3.zero;

        // --- Проигрывание ---
        private InspectAnim _anim;   // текущая анимация (null — предмет возвращается в руку)
        private float _time;         // секунды с начала анимации
        private int _lastStyle = -1;
        private Vector3 _eul, _eulVel, _pos, _posVel;   // сглаженная поза

        private const float BridgeTime = 0.8f;  // плавный переход на начало круга
        private float _bridge = -1f;
        private Vector3 _bridgeEul, _bridgePos;

        private InputReader _input;
        private MouseReader _mouse;
        private bool _ready, _errorLogged;

        private void Awake()
        {
            Log = Logger;

            _key = Config.Bind("General", "InspectKey", "F",
                "Клавиша осмотра: буква или цифра (F, G, V, 5), Alt, Ctrl, Shift, Tab, Space, Backquote, Mouse3 (колесо), Mouse4, Mouse5 — или любое имя из UnityEngine.InputSystem.Key");
            _loopHold = Config.Bind("General", "LoopWhileHeld", true,
                "Пока клавиша зажата — осмотр повторяется по кругу");
            _bringCloser = Config.Bind("General", "BringToCenter", true,
                "Подносить предмет к центру экрана во время осмотра");

            _style = Config.Bind("Animation", "Style", "Auto",
                new ConfigDescription("Вариант осмотра. Auto — свой для каждого предмета, Random — случайный",
                    new AcceptableValueList<string>("Auto", "Random", "Classic", "Flip", "Toss", "Gentle", "Lift", "Heavy", "Blade")));
            _itemStyles = Config.Bind("Animation", "ItemStyles", "",
                "Свои правила для режима Auto: часть_названия=Стиль через запятую. Пример: compass=Toss, rope=Gentle");
            _speed = Config.Bind("Animation", "Speed", 1f,
                new ConfigDescription("Скорость анимации (2 = вдвое быстрее)", new AcceptableValueRange<float>(0.3f, 3f)));
            _intensity = Config.Bind("Animation", "RotationIntensity", 1f,
                new ConfigDescription("Множитель углов поворота", new AcceptableValueRange<float>(0.2f, 2f)));
            _smoothing = Config.Bind("Animation", "Smoothing", 0.3f,
                new ConfigDescription("Сглаживание движений (0 = точно по кривой, 2 = очень мягко)", new AcceptableValueRange<float>(0f, 2f)));

            _oneHandItems = Config.Bind("Hands", "OneHandItems",
                "berry, mushroom, shroom, egg, apple, nut, seed, granola, candy, cookie, marshmallow, honey, pepper, shell, stone, compass, flare, dart, banana",
                "Предметы, которые при осмотре берутся в одну (правую) руку: части названий через запятую");
            _oneHandSmall = Config.Bind("Hands", "OneHandSmallItems", true,
                "Брать в одну руку любые маленькие предметы (до 30 см), даже если их нет в списке");

            _swingEnabled = Config.Bind("Swing", "EnableSwing", true,
                "Взмах ножом по ЛКМ (только визуально и только у предметов без своего действия на ЛКМ)");
            SwingItems = Config.Bind("Swing", "SwingItems", "dagger, ritual, knife, sword, blade",
                "Какими предметами можно взмахивать: части названий через запятую");

            ShowPrompt = Config.Bind("Prompt", "ShowPrompt", true,
                "Показывать подсказки «осмотр» и «взмах» в списке действий с предметом");
            PromptText = Config.Bind("Prompt", "PromptText", "осмотр", "Текст подсказки осмотра");
            SwingPromptText = Config.Bind("Prompt", "SwingPromptText", "взмах", "Текст подсказки взмаха");
            KeyName = _key.Value;

            try
            {
                Refs.Init();
                _input = new InputReader(_key.Value);
                _mouse = new MouseReader();
                var harmony = new Harmony(Guid);
                harmony.PatchAll(typeof(Patches.HoldForward));
                harmony.PatchAll(typeof(Patches.HoldUp));
                harmony.PatchAll(typeof(Patches.AnimationItemTransform));
                harmony.PatchAll(typeof(Patches.LeftHandIK));
                harmony.PatchAll(typeof(Patches.BeforeUnattach));
                try { harmony.PatchAll(typeof(Patches.ItemPrompts)); }
                catch (Exception e) { Error("Подсказки недоступны: " + e.Message); }
                _ready = true;
                Info($"{Name} {Version} загружен. Клавиша осмотра: {_key.Value}");
            }
            catch (Exception e)
            {
                Error($"{Name}: не удалось инициализироваться, мод отключён.\n{e}");
            }
        }

        private void Update()
        {
            if (!_ready) return;
            try { Tick(); }
            catch (Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; Error($"{Name}: ошибка в Update (дальнейшие не логируются):\n{e}"); }
                ResetAll();
            }
        }

        private void Tick()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            object character = Refs.LocalCharacter();
            object item = character != null ? Refs.CurrentItem(character) : null;

            // Осмотр по клавише. Повторное нажатие — осмотр заново, с плавным переходом из текущей позы.
            if (_input.WasPressedThisFrame() && item != null && CanStart(character, item))
            {
                bool restart = Active && ReferenceEquals(item, InspectedItem);
                InspectedItem = item;
                _anim = PickAnimation(item);
                Active = true;
                if (restart)
                {
                    _eul = Wrap(_eul);
                    _bridgeEul = _eul; _bridgePos = _pos;
                    _time = _anim.LoopStart;
                    _bridge = 0f;
                }
                else { _time = 0f; _bridge = -1f; }
                Hands.Begin(character, item, ItemStyles.WantsOneHand((Component)item, _oneHandItems.Value, _oneHandSmall.Value));
            }

            // Взмах ножом по ЛКМ: каждый клик — взмах с самого начала.
            if (_swingEnabled.Value && _mouse.LeftPressedThisFrame() && item != null && CanStart(character, item)
                && ItemStyles.IsSwingItem((Component)item, SwingItems.Value))
                StartSwing(item);

            Hands.Tick(dt);
            if (!Active) return;

            // Отмена: убираем анимацию — предмет плавно вернётся в руку.
            if (_anim != null && (item == null || !ReferenceEquals(item, InspectedItem) || ShouldCancel(character, item)))
                _anim = null;
            if (item == null || !ReferenceEquals(item, InspectedItem)) { ResetAll(); return; }

            bool held = _loopHold.Value && _input.IsHeld();
            Vector3 targetEul = Vector3.zero, targetPos = Vector3.zero;
            if (_anim != null)
            {
                if (_bridge >= 0f)
                {
                    _bridge += dt * _speed.Value / BridgeTime;
                    _anim.Evaluate(_time, out Vector3 e, out Vector3 p);
                    float k = InspectAnim.Smooth(_bridge);
                    targetEul = Vector3.Lerp(_bridgeEul, e * _intensity.Value, k);
                    targetPos = Vector3.Lerp(_bridgePos, p, k);
                    if (_bridge >= 1f) _bridge = -1f;
                }
                else
                {
                    _time += dt * _speed.Value;
                    if (_anim.IsSwing && _time >= _anim.Duration)
                    {
                        // ЛКМ зажата — следующий взмах подряд.
                        if (_swingEnabled.Value && _mouse.LeftHeld()) StartSwing(item);
                        else { _anim = null; _eul = Wrap(_eul); }
                        if (_anim != null) { _anim.Evaluate(_time, out targetEul, out targetPos); targetEul *= _intensity.Value; }
                    }
                    else if (!_anim.IsSwing && held && _time >= _anim.LoopEnd)
                    {
                        // Дошли до возврата в руку, а клавиша всё ещё зажата — новый круг.
                        _anim.Evaluate(_anim.LoopEnd, out _bridgeEul, out _bridgePos);
                        _bridgeEul = Wrap(_bridgeEul * _intensity.Value);
                        _eul = Wrap(_eul);
                        _time = _anim.LoopStart;
                        _bridge = 0f;
                        targetEul = _bridgeEul; targetPos = _bridgePos;
                    }
                    else if (_time >= _anim.Duration) { _anim = null; _eul = Wrap(_eul); }
                    else { _anim.Evaluate(_time, out targetEul, out targetPos); targetEul *= _intensity.Value; }
                }

                // Еле заметное «дыхание» руки, чтобы в паузах осмотра предмет не застывал намертво.
                if (_anim != null && !_anim.IsSwing)
                {
                    float weight = _bridge >= 0f ? 1f : _anim.Envelope(_time);
                    float tt = Time.time;
                    targetEul += weight * new Vector3(
                        (Mathf.PerlinNoise(tt * 0.35f, 0.1f) - 0.5f) * 2f,
                        (Mathf.PerlinNoise(0.3f, tt * 0.3f) - 0.5f) * 2.5f,
                        (Mathf.PerlinNoise(tt * 0.25f, 7.7f) - 0.5f) * 2f);
                }
            }

            // Левая рука (если отпускали) начинает возвращаться заранее, на финальном отрезке.
            if (_anim == null || (!held && _bridge < 0f && _time > _anim.Duration - 0.7f))
                Hands.BeginReturn();

            // Сглаживание через SmoothDamp: устойчиво при любом FPS, без перелётов и дрожи.
            float smoothTime = _anim == null ? 0.18f : _anim.IsSwing ? 0.012f : 0.02f + _smoothing.Value * 0.1f;
            _eul = Vector3.SmoothDamp(_eul, targetEul, ref _eulVel, smoothTime, Mathf.Infinity, dt);
            _pos = Vector3.SmoothDamp(_pos, targetPos, ref _posVel, smoothTime, Mathf.Infinity, dt);

            LocalRot = Quaternion.Euler(_eul);
            Vector3 defaultPos = Refs.DefaultPos(item);
            LocalPos = _bringCloser.Value
                ? new Vector3(-defaultPos.x * (Hands.OneHanded ? 0.45f : 0.9f) * _pos.x, _pos.y, _pos.z)
                : Vector3.zero;

            // Анимация закончилась, предмет вернулся, руки на месте — выключаемся.
            if (_anim == null && !Hands.Busy && _eul.sqrMagnitude < 0.01f && _eulVel.sqrMagnitude < 0.01f
                && _pos.sqrMagnitude < 1e-6f && _posVel.sqrMagnitude < 1e-6f)
                ResetAll();
        }

        private void StartSwing(object item)
        {
            InspectedItem = item;
            _eul = Wrap(_eul);
            _anim = InspectAnim.Swing;
            _time = 0f;
            _bridge = -1f;
            Active = true;
        }

        // Углы в диапазон -180..180 (поворот тот же, но без лишних оборотов при возврате).
        private static Vector3 Wrap(Vector3 e) =>
            new Vector3(Mathf.DeltaAngle(0f, e.x), Mathf.DeltaAngle(0f, e.y), Mathf.DeltaAngle(0f, e.z));

        private void ResetAll()
        {
            Hands.Restore();
            Active = false;
            InspectedItem = null;
            _anim = null;
            _eul = _eulVel = _pos = _posVel = Vector3.zero;
            _bridge = -1f;
            LocalRot = Quaternion.identity;
            LocalPos = Vector3.zero;
        }

        private InspectAnim PickAnimation(object item)
        {
            string style = _style.Value;
            if (style == "Auto")
            {
                style = ItemStyles.Choose((Component)item, _itemStyles.Value, out string reason);
                Log.LogDebug($"{((Component)item).name}: анимация {style} ({reason})");
            }

            InspectAnim anim = InspectAnim.ByName(style);
            if (anim == null)
            {
                // Random: не два одинаковых подряд.
                int n = InspectAnim.All.Length;
                int idx = UnityEngine.Random.Range(0, n);
                if (idx == _lastStyle) idx = (idx + 1 + UnityEngine.Random.Range(0, n - 1)) % n;
                _lastStyle = idx;
                anim = InspectAnim.All[idx];
            }
            return anim;
        }

        private static bool CanStart(object character, object item)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return false; // открыто меню/чат/инвентарь
            return !ShouldCancel(character, item);
        }

        private static bool ShouldCancel(object character, object item)
        {
            if (Refs.IsUsing(item)) return true;
            object data = Refs.Data(character);
            if (data == null) return true;
            return Refs.GetBool(Refs.F_isClimbing, data)
                || Refs.GetBool(Refs.F_passedOut, data)
                || Refs.GetBool(Refs.F_fullyPassedOut, data)
                || Refs.GetBool(Refs.P_dead, data);
        }
    }

    // ---------------- Анимации осмотра ----------------
    // Ключевые кадры: время (сек), углы (pitch, yaw, roll) и смещение
    // (x — доля пути к центру экрана, y — вверх, z — к камере).
    // Каждый отрезок между кадрами идёт с плавным разгоном и торможением (как в первой версии).
    // LoopStart/LoopEnd — участок, который повторяется, пока клавиша зажата.

    internal sealed class InspectAnim
    {
        internal struct Key
        {
            public float t; public Vector3 euler; public Vector3 pos;
            public Key(float t, float pitch, float yaw, float roll, float cx, float up, float fwd)
            { this.t = t; euler = new Vector3(pitch, yaw, roll); pos = new Vector3(cx, up, fwd); }
        }

        private readonly Key[] _keys;
        public readonly string Name;
        public readonly float LoopStart, LoopEnd;
        public bool IsSwing;    // взмах ножом (ЛКМ), а не осмотр
        public float Duration => _keys[_keys.Length - 1].t;

        // loopFrom — номер кадра, с которого начинается новый круг; круг заканчивается на предпоследнем кадре.
        public InspectAnim(string name, int loopFrom, params Key[] keys)
        {
            Name = name; _keys = keys;
            LoopStart = keys[loopFrom].t;
            LoopEnd = keys[keys.Length - 2].t;
        }

        public void Evaluate(float t, out Vector3 euler, out Vector3 pos)
        {
            int i = 0;
            while (i < _keys.Length - 2 && t > _keys[i + 1].t) i++;
            Key a = _keys[i], b = _keys[i + 1];
            float u = Smooth(Mathf.InverseLerp(a.t, b.t, t));
            euler = Vector3.LerpUnclamped(a.euler, b.euler, u);
            pos = Vector3.LerpUnclamped(a.pos, b.pos, u);
        }

        public float Envelope(float t) => Mathf.Clamp01(Mathf.Min(t / 0.5f, (Duration - t) / 0.6f));

        // Smootherstep: мягче обычного smoothstep, без рывка на стыках отрезков.
        public static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * x * (x * (x * 6f - 15f) + 10f); }

        // Повороты — в пределах того, что может рука: поворот предплечья (roll) до ~90°,
        // наклон кисти (pitch) до ~50°, поворот вбок (yaw) до ~40°. Остальное — движение всей руки.

        // Classic: как inspect в CS — показать один бок, верх, другой бок, низ.
        private static readonly InspectAnim Classic = new InspectAnim("Classic", 2,
            new Key(0.00f,   0,   0,   0,  0,    0,     0),
            new Key(0.50f,  -8,  10,  10,  0.8f, 0.07f, -0.07f),   // подняли, лёгкий замах
            new Key(1.20f, -12, -38, -78,  1f,   0.10f, -0.12f),   // предплечье повернули — левый бок
            new Key(1.90f, -15, -40, -84,  1f,   0.10f, -0.12f),   //   разглядываем
            new Key(2.60f, -48, -12, -20,  1f,   0.12f, -0.12f),   // наклон кисти — верх
            new Key(3.20f, -50, -10, -16,  1f,   0.12f, -0.12f),   //   разглядываем
            new Key(4.00f,   8,  38,  84,  1f,   0.10f, -0.12f),   // другой бок
            new Key(4.70f,  10,  40,  88,  1f,   0.10f, -0.12f),   //   разглядываем
            new Key(5.35f,  35,  12,  25,  1f,   0.09f, -0.11f),   // низ
            new Key(5.90f,  32,  10,  22,  1f,   0.09f, -0.11f),   //   разглядываем
            new Key(6.60f,   0,   0,   0,  0,    0,     0));

        // Flip: повернуть изнанкой, затем ребром — для книг, компаса и плоских предметов.
        private static readonly InspectAnim Flip = new InspectAnim("Flip", 1,
            new Key(0.00f,   0,   0,    0,  0,    0,     0),
            new Key(0.50f, -10, -10,  -15,  1f,   0.09f, -0.10f),
            new Key(1.30f, -30, -18,  -90,  1f,   0.11f, -0.12f),  // изнанка
            new Key(2.00f, -32, -20,  -95,  1f,   0.11f, -0.12f),  //   разглядываем
            new Key(2.80f,  -8,  36,   55,  1f,   0.10f, -0.12f),  // ребро
            new Key(3.50f,  -6,  38,   60,  1f,   0.10f, -0.12f),  //   разглядываем
            new Key(4.30f,  38,  10,   10,  1f,   0.09f, -0.11f),  // лицевая сторона снизу
            new Key(4.90f,  35,   8,    8,  1f,   0.09f, -0.11f),  //   разглядываем
            new Key(5.50f,   0,   0,    0,  0,    0,     0));

        // Toss: для мелочи — сразу поднести к лицу верхушкой к себе, затем бока и низ.
        private static readonly InspectAnim Toss = new InspectAnim("Toss", 1,
            new Key(0.00f,   0,   0,   0,  0,    0,      0),
            new Key(0.70f, -36,   0,   0,  1f,   0.11f, -0.12f),   // верхушкой к себе (хвостик, листик)
            new Key(1.40f, -38,   4,   3,  1f,   0.11f, -0.12f),   //   разглядываем
            new Key(2.20f, -16, -32, -80,  1f,   0.11f, -0.12f),   // один бок
            new Key(2.90f, -18, -34, -84,  1f,   0.11f, -0.12f),
            new Key(3.80f,  10,  32,  80,  1f,   0.11f, -0.12f),   // другой бок
            new Key(4.50f,  12,  34,  84,  1f,   0.11f, -0.12f),
            new Key(5.20f,  30,   0,   0,  1f,   0.10f, -0.11f),   // снизу
            new Key(5.70f,  28,   2,   2,  1f,   0.10f, -0.11f),
            new Key(6.40f,   0,   0,   0,  0,    0,      0));

        // Gentle: левый бок — наклон, верх, затем правый бок — положить полностью на бок.
        private static readonly InspectAnim Gentle = new InspectAnim("Gentle", 1,
            new Key(0.00f,   0,   0,   0,  0,    0,     0),
            new Key(0.90f,  -8, -30, -35,  0.8f, 0.06f, -0.07f),   // левый бок
            new Key(1.80f, -10, -32, -38,  0.8f, 0.06f, -0.07f),
            new Key(2.80f, -35,  -8, -10,  0.8f, 0.07f, -0.07f),   // сверху
            new Key(3.50f, -33,  -6,  -8,  0.8f, 0.07f, -0.07f),
            new Key(4.60f,   6,  22,  88,  0.8f, 0.06f, -0.07f),   // лёг на правый бок
            new Key(5.80f,   8,  24,  92,  0.8f, 0.06f, -0.07f),   //   разглядываем
            new Key(6.80f,   0,   0,   0,  0,    0,     0));

        // Blade: клинки (ритуальный кинжал) — как Gentle, но на правом боку клинок дополнительно
        // поворачивается вокруг своей оси, чтобы было видно плоскую сторону лезвия, а не ребро.
        // Поворот на бок (roll) применяется первым, поэтому наклон (pitch) после него крутит предмет
        // вокруг уже горизонтального клинка.
        private static readonly InspectAnim Blade = new InspectAnim("Blade", 1,
            new Key(0.00f,   0,   0,   0,  0,    0,     0),
            new Key(0.90f,   0, -30, -35,  0.6f, 0.04f,  0.03f),   // левый бок (нож дальше от лица — не упирается в голову)
            new Key(1.80f,  -2, -32, -38,  0.6f, 0.04f,  0.03f),
            new Key(2.80f, -15,  -8, -10,  0.6f, 0.05f,  0.03f),   // сверху (небольшой наклон — остриё не смотрит в камеру)
            new Key(3.50f, -14,  -6,  -8,  0.6f, 0.05f,  0.03f),
            new Key(4.40f, -30,  20,  88,  0.8f, 0.07f, -0.08f),   // лёг на правый бок...
            new Key(5.10f, -72,  18,  90,  0.8f, 0.08f, -0.09f),   // ...и развернулся плоскостью клинка к нам
            new Key(6.10f, -75,  16,  92,  0.8f, 0.08f, -0.09f),   //   разглядываем лезвие
            new Key(7.10f,   0,   0,   0,  0,    0,     0));

        // Heavy: длинные двуручные предметы (Антизука и т.п.) — только небольшие наклоны,
        // иначе руки выкручиваются на другое плечо.
        private static readonly InspectAnim Heavy = new InspectAnim("Heavy", 1,
            new Key(0.00f,   0,   0,   0,  0,    0,     0),
            new Key(1.00f,  -8, -16, -18,  0.5f, 0.04f, -0.04f),   // чуть повернули влево
            new Key(2.00f, -10, -18, -20,  0.5f, 0.04f, -0.04f),
            new Key(3.00f, -20,   0,   0,  0.5f, 0.05f, -0.04f),   // приподняли — сверху
            new Key(3.70f, -18,   2,   2,  0.5f, 0.05f, -0.04f),
            new Key(4.70f,   6,  16,  18,  0.5f, 0.04f, -0.04f),   // вправо
            new Key(5.50f,   8,  18,  20,  0.5f, 0.04f, -0.04f),
            new Key(6.30f,   0,   0,   0,  0,    0,     0));

        // Lift: живность и игрушки — поднести к лицу, «посмотреть в глаза», наклонить в стороны.
        private static readonly InspectAnim Lift = new InspectAnim("Lift", 1,
            new Key(0.00f,   0,   0,   0,  0,    0,     0),
            new Key(0.90f, -12,   0,   0,  1f,   0.14f, -0.14f),   // подняли на уровень глаз
            new Key(1.80f, -14,   4,   2,  1f,   0.15f, -0.15f),   //   смотрим
            new Key(2.60f, -10, -22, -30,  1f,   0.14f, -0.14f),   // наклон влево
            new Key(3.30f, -12, -24, -34,  1f,   0.14f, -0.14f),
            new Key(4.20f, -10,  22,  30,  1f,   0.14f, -0.14f),   // наклон вправо
            new Key(4.90f, -12,  24,  34,  1f,   0.14f, -0.14f),
            new Key(5.60f,  15,   0,   0,  1f,   0.11f, -0.12f),   // заглянуть снизу
            new Key(6.10f,  14,   0,   0,  1f,   0.11f, -0.12f),
            new Key(6.80f,   0,   0,   0,  0,    0,     0));

        internal static readonly InspectAnim[] All = { Classic, Flip, Toss, Gentle, Lift, Heavy, Blade };

        // Взмах ножом (ЛКМ): горизонтальный рез справа налево на уровне груди.
        // x — доля пути к центру экрана (больше 1 — дальше на левую сторону, меньше 0 — правее обычного),
        // y — вверх/вниз, z — вперёд (плюс) / к себе (минус).
        internal static readonly InspectAnim Swing = new InspectAnim("Swing", 1,
            new Key(0.00f,   0,   0,   0,  0,     0,     0),
            new Key(0.18f, -10,  45,  55, -0.6f,  0.03f,  0.00f),  // замах вправо
            new Key(0.30f,  -5,   0,  70,  0.8f,  0.01f,  0.14f),  // горизонтальный рез через центр
            new Key(0.40f,   0, -40,  70,  1.7f,  0.00f,  0.08f),  // проводка влево
            new Key(0.75f,   0,   0,   0,  0,     0,     0)) { IsSwing = true };

        internal static InspectAnim ByName(string name)
        {
            foreach (var a in All) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }

    // ---------------- Подбор анимации под предмет ----------------

    internal static class ItemStyles
    {
        // Ключевые слова ищутся в имени объекта, названии предмета и именах его компонентов.
        private static readonly (string[] words, string style)[] Rules =
        {
            (new[] { "amulet", "rescuehook", "hook" }, "Gentle"),
            (new[] { "idol", "statue" }, "Lift"),
            (new[] { "energy", "drink", "soda", "bottle", "flask", "potion", "jar" }, "Toss"),
            (new[] { "antizooka", "shovel", "chain", "pan", "cannon", "launcher" }, "Heavy"),
            (new[] { "dagger", "ritual", "knife", "sword", "blade" }, "Blade"),
            (new[] { "frog", "bingbongplush", "plush", "toy", "chicken", "bird" }, "Lift"),
            (new[] { "guidebook", "book", "map", "scroll", "passport", "compass", "frisbee", "cure", "bandage", "clothes", "card" }, "Flip"),
            (new[] { "basketball", "ball", "coconut", "egg", "apple", "berry", "mushroom", "shroom", "marshmallow",
                     "nut", "stone", "rock", "shell", "seed", "granola", "bar", "banana", "cactus", "bingbong", "honey",
                     "pepper", "candy", "cookie", "dart", "bean" }, "Toss"),
            (new[] { "lantern", "rope", "parasol", "umbrella", "backpack",
                     "kiln", "tent", "balloon", "ladder" }, "Gentle"),
            (new[] { "bugle", "binocular", "flare", "torch", "axe", "pick", "stick", "piton", "sling" }, "Classic"),
        };

        private static readonly System.Collections.Generic.Dictionary<string, string> KnownComponents =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "Guidebook", "guidebook" }, { "Lantern", "lantern" }, { "Flare", "flare" }, { "Frisbee", "frisbee" },
                { "Parasol", "parasol" }, { "Basketball", "basketball" }, { "BingBong", "bingbong" },
                { "Rope", "rope" }, { "BugleSFX", "bugle" }, { "CompassPointer", "compass" }, { "CactusBall", "cactus" },
                { "Snowball", "stone" },
            };

        public static string Choose(Component item, string userRules, out string reason)
        {
            string id = Describe(item);

            // 1. Правила игрока из конфига — главнее всего.
            foreach (var rule in (userRules ?? "").Split(','))
            {
                var kv = rule.Split('=');
                if (kv.Length != 2) continue;
                string word = kv[0].Trim().ToLowerInvariant(), style = kv[1].Trim();
                if (word.Length > 0 && id.Contains(word) && InspectAnim.ByName(style) != null)
                { reason = $"правило '{word}' из конфига"; return InspectAnim.ByName(style).Name; }
            }

            // 2. Встроенные правила по названию.
            foreach (var (words, style) in Rules)
                foreach (var w in words)
                    if (id.Contains(w)) { reason = $"название содержит '{w}'"; return style; }

            // 3. По форме и размеру модели.
            if (TryGetSize(item, out Vector3 size))
            {
                float max = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                float min = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                if (max > 0.7f) { reason = $"большой ({max:0.00} м)"; return "Gentle"; }
                if (min < max * 0.3f) { reason = "плоский/вытянутый"; return max < 0.35f ? "Flip" : "Classic"; }
                if (max < 0.25f) { reason = $"маленький ({max:0.00} м)"; return "Toss"; }
            }
            reason = "по умолчанию";
            return "Classic";
        }

        // Взмах — только у клинков из списка и только если у предмета нет своего действия на ЛКМ.
        public static bool IsSwingItem(Component item, string list)
        {
            if (Refs.HasPrimaryAction(item)) return false;
            string id = Describe(item);
            foreach (var w in (list ?? "").Split(','))
            {
                string word = w.Trim().ToLowerInvariant();
                if (word.Length > 0 && id.Contains(word)) return true;
            }
            return false;
        }

        private static readonly string[] BuiltInOneHand = { "bean", "berry", "mushroom", "shroom", "egg", "nut", "seed" };
        // Эти предметы всегда осматриваются двумя руками, даже если маленькие (панацея и т.п.).
        private static readonly string[] BuiltInTwoHands = { "cure", "panacea", "remedy" };

        public static bool WantsOneHand(Component item, string list, bool small)
        {
            if (Refs.RightHandOnly(item)) return false; // и так одноручный (как ритуальный кинжал)
            string id = Describe(item);
            foreach (var w in BuiltInTwoHands)
                if (id.Contains(w)) return false;
            foreach (var w in BuiltInOneHand)
                if (id.Contains(w)) return true;
            foreach (var w in (list ?? "").Split(','))
            {
                string word = w.Trim().ToLowerInvariant();
                if (word.Length > 0 && id.Contains(word)) return true;
            }
            if (small && TryGetSize(item, out Vector3 size))
                return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 0.3f;
            return false;
        }

        private static string Describe(Component item)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(item.gameObject.name).Append(' ');
            try
            {
                object ui = AccessTools.Field(item.GetType(), "UIData")?.GetValue(item);
                object n = ui != null ? AccessTools.Field(ui.GetType(), "itemName")?.GetValue(ui) : null;
                if (n != null) sb.Append(n).Append(' ');
            }
            catch { }
            // Компоненты игры — только из точного списка, чтобы служебные (Canvas, Pickup...) не давали ложных совпадений.
            foreach (var c in item.GetComponents<Component>())
                if (c != null && KnownComponents.TryGetValue(c.GetType().Name, out string word)) sb.Append(word).Append(' ');
            return sb.ToString().Replace("(Clone)", "").Replace("_", "").ToLowerInvariant();
        }

        private static bool TryGetSize(Component item, out Vector3 size)
        {
            size = Vector3.zero;
            bool any = false;
            Bounds b = default;
            foreach (var r in item.GetComponentsInChildren<Renderer>())
            {
                // Только видимые меши: эффекты, линии и следы дают ложный «огромный» размер.
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                string tn = r.GetType().Name;
                if (tn != "MeshRenderer" && tn != "SkinnedMeshRenderer") continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (any) size = b.size;
            return any;
        }
    }

    // ---------------- Левая рука при осмотре мелочи ----------------
    // Кисти прикреплены к предмету физическими суставами (FixedJoint) и поворачиваются вместе с ним.
    // Мелочь (ягоды, грибы и т.п.) осматривается одной рукой, как ритуальный кинжал: левая рука
    // плавно отпускает предмет, а в конце плавно тянется обратно. Сустав ставится, только когда
    // кисть уже у предмета, — без телепорта и рывка.

    internal static class Hands
    {
        private enum State { None, Released, Returning }
        private static State _state;
        private static object _character, _item;
        private static float _ikL = 1f, _retTime;

        public static bool Busy => _state != State.None;
        public static bool OneHanded => _state != State.None;
        public static bool Controls(object character) => _state != State.None && ReferenceEquals(character, _character);
        public static float LeftIK => _ikL;

        public static void Begin(object character, object item, bool oneHand)
        {
            if (_state != State.None && !ReferenceEquals(item, _item)) Restore();
            if (!oneHand) return;
            if (_state == State.None)
            {
                Rigidbody itemRig = Refs.ItemRig(item);
                if (itemRig == null || !RemoveJoints(Refs.HandRig(character, "Hand_L"), itemRig)) return;
                _character = character; _item = item;
                _ikL = 1f;
            }
            _state = State.Released;
        }

        public static void BeginReturn()
        {
            if (_state == State.Released) { _state = State.Returning; _retTime = 0f; }
        }

        public static void Tick(float dt)
        {
            if (_state == State.None) return;
            if (!ReferenceEquals(Refs.CurrentItem(_character), _item)) { Restore(); return; }

            if (_state == State.Released)
            {
                _ikL = Mathf.MoveTowards(_ikL, 0f, dt / 0.4f);   // рука плавно отпускает предмет
            }
            else
            {
                _retTime += dt;
                _ikL = Mathf.MoveTowards(_ikL, 1f, dt / 0.5f);   // рука плавно тянется обратно
                if (_ikL >= 1f && (LeftHandAtGrip(0.05f) || (_retTime > 1.5f && LeftHandAtGrip(0.15f)) || _retTime > 3f))
                    Restore();
            }
        }

        // Вернуть сустав левой руки (если предмет всё ещё в руках).
        public static void Restore()
        {
            if (_state == State.None) return;
            _state = State.None;
            _ikL = 1f;
            object character = _character, item = _item;
            _character = _item = null;
            try
            {
                if (character == null || item == null || !ReferenceEquals(Refs.CurrentItem(character), item)) return;
                Rigidbody itemRig = Refs.ItemRig(item);
                Rigidbody hand = Refs.HandRig(character, "Hand_L");
                if (itemRig != null && hand != null) hand.gameObject.AddComponent<FixedJoint>().connectedBody = itemRig;
            }
            catch (Exception e) { Plugin.Error("Не удалось вернуть левую руку на предмет: " + e); }
        }

        private static bool LeftHandAtGrip(float maxDist)
        {
            Rigidbody rig = Refs.HandRig(_character, "Hand_L");
            if (rig == null) return true;
            Vector3 p = Refs.LeftGripWorld(_character, _item);
            return (rig.position - p).sqrMagnitude <= maxDist * maxDist;
        }

        private static bool RemoveJoints(Rigidbody hand, Rigidbody itemRig)
        {
            if (hand == null) return false;
            bool any = false;
            foreach (var j in hand.GetComponents<FixedJoint>())
                if (j != null && j.connectedBody == itemRig) { UnityEngine.Object.DestroyImmediate(j); any = true; }
            return any;
        }
    }

    // ---------------- Подсказка «осмотр [F]» ----------------
    // Клонируем строку «бросить [Q]», чтобы шрифт, размер и стиль совпадали с остальными подсказками,
    // меняем текст и иконку клавиши (иконку берём из набора спрайтов самой игры).

    internal static class InspectPrompt
    {
        private static GameObject _row, _spacer, _throwRow, _swingRow;
        private static Component _owner;
        private static bool _failed;

        public static void Update(Component gui)
        {
            if (_failed) return;
            GameObject drop = (Refs.GetField(gui, "itemPromptDrop") as Component)?.gameObject;
            if (drop == null) return;

            if (_row == null || !ReferenceEquals(_owner, gui)) Create(gui, drop);
            if (_row == null) return;

            bool show = Plugin.ShowPrompt.Value && HasItem();
            if (_row.activeSelf != show) _row.SetActive(show);
            if (_swingRow != null)
            {
                object c = Refs.LocalCharacter();
                object it = c != null ? Refs.CurrentItem(c) : null;
                bool swing = show && it != null && ItemStyles.IsSwingItem((Component)it, Plugin.SwingItems.Value);
                if (_swingRow.activeSelf != swing) _swingRow.SetActive(swing);
            }
            // Отступ нужен, только когда над нами строка «метнуть» с подписью «удерживать».
            if (_spacer != null)
            {
                bool gap = show && _throwRow != null && _throwRow.activeInHierarchy;
                if (_spacer.activeSelf != gap) _spacer.SetActive(gap);
            }
        }

        private static bool HasItem()
        {
            object c = Refs.LocalCharacter();
            return c != null && Refs.CurrentItem(c) != null;
        }

        private static void Create(Component gui, GameObject drop)
        {
            _owner = gui;
            Type iconType = Refs.GameType("InputIcon");

            // Строка = сам текст «бросить», если иконка внутри него, иначе его родитель-строка.
            GameObject source = drop;
            if (drop.GetComponentInChildren(iconType, true) == null && drop.transform.parent != null
                && drop.transform.parent.GetComponentsInChildren(iconType, true).Length == 1)
                source = drop.transform.parent.gameObject;

            _row = UnityEngine.Object.Instantiate(source, source.transform.parent);
            _row.name = "ItemInspectPrompt";
            _row.transform.SetAsLastSibling();
            _throwRow = (Refs.GetField(gui, "itemPromptThrow") as Component)?.gameObject;
            if (_throwRow != null && source != drop && _throwRow.transform.parent != null
                && _throwRow.transform.parent.parent == source.transform.parent)
                _throwRow = _throwRow.transform.parent.gameObject;
            MakeSpacer(source);

            // Убираем локализацию (иначе она вернёт «бросить») и иконку действия «Drop».
            foreach (var c in _row.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                if (tn == "LocalizedText") UnityEngine.Object.DestroyImmediate(c);
            }

            string keyTag = InputReader.IconTag(Plugin.KeyName);
            foreach (var icon in _row.GetComponentsInChildren(iconType, true))
            {
                var hold = AccessTools.Field(iconType, "hold")?.GetValue(icon) as GameObject;
                if (hold != null) hold.SetActive(false);
                GameObject iconGo = ((Component)icon).gameObject;
                UnityEngine.Object.DestroyImmediate(icon);
                SetText(iconGo, keyTag);
            }

            // Основной текст — первый текстовый компонент, который не иконка.
            foreach (var c in _row.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !IsText(c)) continue;
                string cur = GetText(c);
                if (cur != null && cur.Contains("<sprite")) continue;
                SetText(c.gameObject, Plugin.PromptText.Value);
                break;
            }
            // Вторая строка — «взмах [ЛКМ]»: копия уже очищенной строки «осмотр» (без локализации и иконки игры).
            _swingRow = UnityEngine.Object.Instantiate(_row, _row.transform.parent);
            _swingRow.name = "ItemSwingPrompt";
            _swingRow.transform.SetAsLastSibling();
            if (_swingRow != null)
            {
                string mouseTag = MouseTag();
                bool textSet = false;
                foreach (var c in _swingRow.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || !IsText(c)) continue;
                    string cur = GetText(c);
                    if (cur != null && cur.Contains("<sprite")) c.GetType().GetProperty("text", typeof(string)).SetValue(c, mouseTag);
                    else if (!textSet) { c.GetType().GetProperty("text", typeof(string)).SetValue(c, Plugin.SwingPromptText.Value); textSet = true; }
                }
                _swingRow.SetActive(false);
            }
            Plugin.Info("Подсказка «осмотр» добавлена в интерфейс");
        }

        // Пустой элемент перед нашей строкой, чтобы подпись «удерживать» под «метнуть» не налезала на «осмотр».
        private static void MakeSpacer(GameObject source)
        {
            var parent = source.transform.parent as RectTransform;
            if (parent == null) return;
            float h = (source.transform as RectTransform)?.rect.height ?? 20f;
            if (h <= 1f) h = 20f;

            bool hasLayout = false;
            foreach (var c in parent.GetComponents<Component>())
                if (c != null && c.GetType().Name.Contains("LayoutGroup")) hasLayout = true;

            if (hasLayout)
            {
                _spacer = new GameObject("ItemInspectPromptSpacer", typeof(RectTransform));
                _spacer.transform.SetParent(parent, false);
                _spacer.transform.SetSiblingIndex(_row.transform.GetSiblingIndex());
                Type le = Type.GetType("UnityEngine.UI.LayoutElement, UnityEngine.UI");
                if (le != null)
                {
                    Component el = _spacer.AddComponent(le);
                    le.GetProperty("minHeight")?.SetValue(el, h * 0.6f);
                    le.GetProperty("preferredHeight")?.SetValue(el, h * 0.6f);
                }
                ((RectTransform)_spacer.transform).sizeDelta = new Vector2(1f, h * 0.6f);
            }
            else if (_row.transform is RectTransform rt)
            {
                // Без layout-группы просто сдвигаем строку вниз.
                rt.anchoredPosition += new Vector2(0f, -h * 0.6f);
            }
        }

        private static string MouseTag() => InputReader.SpriteTag("inputPathToSpriteTagMouse", "leftButton") ?? "[ЛКМ]";

        private static bool IsText(Component c) => c.GetType().GetProperty("text", typeof(string)) != null && c.GetType().Name.Contains("Text");
        private static string GetText(Component c) => c.GetType().GetProperty("text", typeof(string))?.GetValue(c) as string;

        private static void SetText(GameObject go, string text)
        {
            foreach (var c in go.GetComponents<Component>())
                if (c != null && IsText(c)) { c.GetType().GetProperty("text", typeof(string)).SetValue(c, text); return; }
        }

        public static void Fail(Exception e)
        {
            if (_failed) return;
            _failed = true;
            if (_row != null) UnityEngine.Object.Destroy(_row);
            Plugin.Error("Подсказка «осмотр» отключена из-за ошибки: " + e);
        }
    }

    // ---------------- Harmony-патчи ----------------

    internal static class Patches
    {
        private static bool _failed;
        internal static void Fail(Exception e)
        {
            Plugin.Active = false;
            if (_failed) return;
            _failed = true;
            Plugin.Error($"{Plugin.Name}: ошибка в патче (дальнейшие не логируются):\n{e}");
        }

        private static bool AppliesTo(object characterItemsOrRagdoll, out object character)
        {
            character = null;
            if (!Plugin.Active || characterItemsOrRagdoll == null) return false;
            character = Refs.GetField(characterItemsOrRagdoll, "character");
            if (character == null || !ReferenceEquals(character, Refs.LocalCharacter())) return false;
            return ReferenceEquals(Refs.CurrentItem(character), Plugin.InspectedItem);
        }

        private static Quaternion TargetRotation(object character)
        {
            object data = Refs.Data(character);
            Vector3 f = (Vector3)Refs.F_lookDirection.GetValue(data);
            Vector3 u = (Vector3)Refs.F_lookDirectionUp.GetValue(data);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            if (u.sqrMagnitude < 1e-6f) u = Vector3.up;
            return Quaternion.LookRotation(f, u) * Plugin.LocalRot;
        }

        // Физика предмета тянет его к GetItemHoldForward / GetItemHoldUp — подмешиваем поворот осмотра.
        [HarmonyPatch]
        internal static class HoldForward
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("CharacterItems"), "GetItemHoldForward");
            private static void Postfix(object __instance, ref Vector3 __result)
            {
                try { if (AppliesTo(__instance, out var c)) __result = TargetRotation(c) * Vector3.forward; }
                catch (Exception e) { Patches.Fail(e); }
            }
        }

        [HarmonyPatch]
        internal static class HoldUp
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("CharacterItems"), "GetItemHoldUp");
            private static void Postfix(object __instance, ref Vector3 __result)
            {
                try { if (AppliesTo(__instance, out var c)) __result = TargetRotation(c) * Vector3.up; }
                catch (Exception e) { Patches.Fail(e); }
            }
        }

        // Пока осматриваем одной рукой — IK левой руки выключен (как у rightHandOnly-предметов).
        [HarmonyPatch]
        internal static class LeftHandIK
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("CharacterAnimations"), "HandleIK");
            private static void Postfix(object __instance)
            {
                try
                {
                    if (!Hands.Busy) return;
                    object c = Refs.GetField(__instance, "character");
                    if (Hands.Controls(c)) Refs.ScaleLeftIKWeight(c, Hands.LeftIK);
                }
                catch (Exception e) { Patches.Fail(e); }
            }
        }

        // Игра снимает предмет с рук (выбросили, сменили слот и т.п.) — заранее возвращаем сустав левой руки,
        // иначе её код снятия не уберёт и сустав правой.
        [HarmonyPatch]
        internal static class BeforeUnattach
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("CharacterItems"), "UnAttachItem");
            private static void Prefix(object __instance)
            {
                try
                {
                    object c = Refs.GetField(__instance, "character");
                    if (Hands.Controls(c)) Hands.Restore();
                }
                catch (Exception e) { Patches.Fail(e); }
            }
        }

        // Подсказка «осмотр [F]» в списке действий с предметом.
        [HarmonyPatch]
        internal static class ItemPrompts
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("GUIManager"), "UpdateItemPrompts");
            private static void Postfix(object __instance)
            {
                try { InspectPrompt.Update((Component)__instance); }
                catch (Exception e) { InspectPrompt.Fail(e); }
            }
        }

        // animationItemTransform задаёт точку удержания предмета и цели IK для рук.
        // Смещаем и поворачиваем его — предмет подъезжает к центру, а руки следуют за ним.
        [HarmonyPatch]
        internal static class AnimationItemTransform
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Refs.GameType("CharacterRagdoll"), "SaveAdditionalTransformPositions");
            private static void Postfix(object __instance)
            {
                try { Apply(__instance); } catch (Exception e) { Patches.Fail(e); }
            }

            private static void Apply(object __instance)
            {
                if (!AppliesTo(__instance, out var c)) return;
                object refs = Refs.F_refs.GetValue(c);
                var itemT = (Transform)Refs.F_animationItemTransform.GetValue(refs);
                var lookT = (Transform)Refs.F_animationLookTransform.GetValue(refs);
                if (itemT == null || lookT == null) return;
                itemT.position += lookT.TransformVector(Plugin.LocalPos);
                itemT.rotation = itemT.rotation * Plugin.LocalRot;
            }
        }
    }

    // ---------------- Доступ к полям игры через рефлексию ----------------
    // Так мод не зависит от Photon и прочих сборок на этапе компиляции.

    internal static class Refs
    {
        // Поле или свойство игры: в разных версиях PEAK одно и то же бывает и тем, и другим.
        internal sealed class Member
        {
            private readonly FieldInfo _f; private readonly PropertyInfo _p;
            public Member(FieldInfo f, PropertyInfo p) { _f = f; _p = p; }
            public object GetValue(object obj) => _f != null ? _f.GetValue(obj) : _p.GetValue(obj);
        }

        internal static Member F_localCharacter, F_data, F_refs, F_currentItem;
        internal static Member F_lookDirection, F_lookDirectionUp, F_isClimbing, F_passedOut, F_fullyPassedOut, P_dead;
        internal static Member P_usingPrimary, P_usingSecondary;
        internal static Member F_animationItemTransform, F_animationLookTransform, F_defaultPos;

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal static Assembly Game;

        // Ищем типы строго в Assembly-CSharp: в других модах тоже могут быть классы Item/Character.
        internal static Type GameType(string name)
        {
            if (Game == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Assembly-CSharp") { Game = a; break; }
            if (Game == null) throw new Exception("Assembly-CSharp не найдена");
            return Game.GetType(name, false) ?? throw new Exception($"Тип {name} не найден в Assembly-CSharp");
        }

        internal static Member Get(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                var f = cur.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return new Member(f, null);
                var p = cur.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.GetGetMethod(true) != null) return new Member(null, p);
            }
            throw new Exception($"{t.Name}.{name} не найдено (игра обновилась?)");
        }

        internal static void Init()
        {
            Type character = GameType("Character");
            Type data = GameType("CharacterData");
            Type refs = character.GetNestedType("CharacterRefs", All) ?? throw new Exception("Character.CharacterRefs не найден");
            Type item = GameType("Item");

            F_localCharacter = Get(character, "localCharacter");
            F_data = Get(character, "data");
            F_refs = Get(character, "refs");
            F_currentItem = Get(data, "currentItem");
            F_lookDirection = Get(data, "lookDirection");
            F_lookDirectionUp = Get(data, "lookDirection_Up");
            F_isClimbing = Get(data, "isClimbing");
            F_passedOut = Get(data, "passedOut");
            F_fullyPassedOut = Get(data, "fullyPassedOut");
            P_dead = Get(data, "dead");
            F_animationItemTransform = Get(refs, "animationItemTransform");
            F_animationLookTransform = Get(refs, "animationLookTransform");
            F_defaultPos = Get(item, "defaultPos");
            P_usingPrimary = Get(item, "isUsingPrimary");
            P_usingSecondary = Get(item, "isUsingSecondary");
        }

        internal static object LocalCharacter() => AsAlive(F_localCharacter.GetValue(null));
        internal static object Data(object character) => character == null ? null : AsAlive(F_data.GetValue(character));
        internal static object CurrentItem(object character)
        {
            object d = Data(character);
            return d == null ? null : AsAlive(F_currentItem.GetValue(d));
        }

        internal static Vector3 DefaultPos(object item) => item == null ? Vector3.zero : (Vector3)F_defaultPos.GetValue(item);

        internal static bool IsUsing(object item) =>
            item != null && ((bool)P_usingPrimary.GetValue(item) || (bool)P_usingSecondary.GetValue(item));

        private static MethodInfo _getBodypartRig, _posLeftWorld;
        private static Member _items, _ikLeft, _rig, _rightHandOnly;
        private static PropertyInfo _weight;
        private static Type _bodypartType;

        internal static Rigidbody HandRig(object character, string part)
        {
            if (_getBodypartRig == null)
            {
                _bodypartType = GameType("BodypartType");
                _getBodypartRig = AccessTools.Method(GameType("Character"), "GetBodypartRig", new[] { _bodypartType });
            }
            return _getBodypartRig.Invoke(character, new[] { Enum.Parse(_bodypartType, part) }) as Rigidbody;
        }

        internal static Rigidbody ItemRig(object item) =>
            (_rig ??= Get(GameType("Item"), "rig")).GetValue(item) as Rigidbody;

        internal static bool RightHandOnly(object item) =>
            (bool)(_rightHandOnly ??= Get(GameType("Item"), "rightHandOnly")).GetValue(item);

        // Точка, где левая кисть держит предмет (в мировых координатах).
        internal static Vector3 LeftGripWorld(object character, object item)
        {
            object items = (_items ??= Get(GameType("Character").GetNestedType("CharacterRefs", All), "items")).GetValue(F_refs.GetValue(character));
            _posLeftWorld ??= AccessTools.Method(GameType("CharacterItems"), "GetItemPosLeftWorld");
            return (Vector3)_posLeftWorld.Invoke(items, new[] { item });
        }

        // Умножаем вес IK левой руки, выставленный игрой, на наш (0..1) — плавное отпускание/возврат руки.
        internal static void ScaleLeftIKWeight(object character, float k)
        {
            object ik = (_ikLeft ??= Get(GameType("Character").GetNestedType("CharacterRefs", All), "ikLeft")).GetValue(F_refs.GetValue(character));
            if (ik == null) return;
            _weight ??= ik.GetType().GetProperty("weight", BindingFlags.Public | BindingFlags.Instance);
            if (_weight == null) return;
            _weight.SetValue(ik, (float)_weight.GetValue(ik) * k);
        }

        private static MethodInfo _canUsePrimary;
        internal static bool HasPrimaryAction(object item)
        {
            try
            {
                object ui = AccessTools.Field(item.GetType(), "UIData")?.GetValue(item);
                object has = ui != null ? AccessTools.Field(ui.GetType(), "hasMainInteract")?.GetValue(ui) : null;
                if (has is bool b && !b) return false;
                _canUsePrimary ??= AccessTools.Method(GameType("Item"), "CanUsePrimary");
                return _canUsePrimary == null || (bool)_canUsePrimary.Invoke(item, null);
            }
            catch { return true; } // не уверены — не перехватываем ЛКМ
        }

        internal static bool GetBool(Member m, object obj) => (bool)m.GetValue(obj);

        internal static object GetField(object obj, string name) =>
            obj == null ? null : AsAlive(AccessTools.Field(obj.GetType(), name)?.GetValue(obj));

        // Уничтоженные Unity-объекты не равны null для C#, проверяем через оператор Unity.
        private static object AsAlive(object o) => o is UnityEngine.Object u ? (u ? o : null) : o;
    }

    // ---------------- Чтение клавиши ----------------
    // PEAK использует новый Input System; читаем его через рефлексию, со старым Input как запасным вариантом.

    internal class MouseReader
    {
        private readonly PropertyInfo _current;
        private PropertyInfo _left, _pressed, _held;

        public MouseReader()
        {
            Type mouse = AccessTools.TypeByName("UnityEngine.InputSystem.Mouse");
            _current = mouse?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
        }

        private object Button()
        {
            object m = _current?.GetValue(null);
            if (m == null) return null;
            _left ??= m.GetType().GetProperty("leftButton");
            return _left?.GetValue(m);
        }

        public bool LeftPressedThisFrame()
        {
            try
            {
                object b = Button();
                if (b == null) return Input.GetMouseButtonDown(0);
                _pressed ??= b.GetType().GetProperty("wasPressedThisFrame");
                return (bool)_pressed.GetValue(b);
            }
            catch { return false; }
        }

        public bool LeftHeld()
        {
            try
            {
                object b = Button();
                if (b == null) return Input.GetMouseButton(0);
                _held ??= b.GetType().GetProperty("isPressed");
                return (bool)_held.GetValue(b);
            }
            catch { return false; }
        }
    }

    internal class InputReader
    {
        // Понятные имена → кнопки. Alt/Ctrl/Shift срабатывают и на левую, и на правую.
        private static readonly System.Collections.Generic.Dictionary<string, string[]> KeyAliases =
            new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Alt", new[] { "LeftAlt", "RightAlt" } },
                { "Ctrl", new[] { "LeftCtrl", "RightCtrl" } },
                { "Control", new[] { "LeftCtrl", "RightCtrl" } },
                { "Shift", new[] { "LeftShift", "RightShift" } },
                { "LAlt", new[] { "LeftAlt" } }, { "RAlt", new[] { "RightAlt" } },
                { "LCtrl", new[] { "LeftCtrl" } }, { "RCtrl", new[] { "RightCtrl" } },
                { "LShift", new[] { "LeftShift" } }, { "RShift", new[] { "RightShift" } },
                { "Tilde", new[] { "Backquote" } }, { "Ё", new[] { "Backquote" } },
            };

        private static readonly System.Collections.Generic.Dictionary<string, string> MouseAliases =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mouse3", "middleButton" }, { "MiddleMouse", "middleButton" }, { "MMB", "middleButton" },
                { "Mouse4", "backButton" }, { "MouseBack", "backButton" },
                { "Mouse5", "forwardButton" }, { "MouseForward", "forwardButton" },
            };

        private static readonly System.Collections.Generic.Dictionary<string, KeyCode> LegacyAliases =
            new System.Collections.Generic.Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase)
            {
                { "Alt", KeyCode.LeftAlt }, { "Ctrl", KeyCode.LeftControl }, { "Control", KeyCode.LeftControl },
                { "Shift", KeyCode.LeftShift }, { "LeftCtrl", KeyCode.LeftControl }, { "RightCtrl", KeyCode.RightControl },
                { "Mouse3", KeyCode.Mouse2 }, { "Mouse4", KeyCode.Mouse3 }, { "Mouse5", KeyCode.Mouse4 },
                { "Backquote", KeyCode.BackQuote },
            };

        private readonly System.Collections.Generic.List<object> _keys = new System.Collections.Generic.List<object>();
        private readonly string _mouseButton;
        private readonly PropertyInfo _keyboard, _indexer, _mouse;
        private readonly KeyCode _legacy;
        private readonly System.Collections.Generic.Dictionary<string, PropertyInfo> _props =
            new System.Collections.Generic.Dictionary<string, PropertyInfo>();

        public InputReader(string keyName)
        {
            keyName = (keyName ?? "F").Trim();
            Type kb = AccessTools.TypeByName("UnityEngine.InputSystem.Keyboard");
            Type keyEnum = AccessTools.TypeByName("UnityEngine.InputSystem.Key");
            _mouse = AccessTools.TypeByName("UnityEngine.InputSystem.Mouse")?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

            if (MouseAliases.TryGetValue(keyName, out string button)) _mouseButton = button;
            else if (kb != null && keyEnum != null)
            {
                _keyboard = kb.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _indexer = kb.GetProperty("Item", new[] { keyEnum });
                string[] names = KeyAliases.TryGetValue(keyName, out var list) ? list : new[] { keyName };
                foreach (var n in names)
                {
                    try { _keys.Add(Enum.Parse(keyEnum, n, true)); }
                    catch { Plugin.Error($"Неизвестная клавиша '{n}' — используется F"); }
                }
                if (_keys.Count == 0) _keys.Add(Enum.Parse(keyEnum, "F"));
            }

            if (!LegacyAliases.TryGetValue(keyName, out _legacy) && !Enum.TryParse(keyName, true, out _legacy))
                _legacy = KeyCode.F;
        }

        public bool WasPressedThisFrame() => Read("wasPressedThisFrame", () => Input.GetKeyDown(_legacy));
        public bool IsHeld() => Read("isPressed", () => Input.GetKey(_legacy));

        private bool Read(string prop, Func<bool> legacy)
        {
            try
            {
                if (_mouseButton != null)
                {
                    object mouse = _mouse?.GetValue(null);
                    if (mouse == null) return legacy();
                    return Check(mouse.GetType().GetProperty(_mouseButton)?.GetValue(mouse), prop);
                }
                if (_keyboard != null && _indexer != null && _keys.Count > 0)
                {
                    object keyboard = _keyboard.GetValue(null);
                    if (keyboard == null) return false;
                    foreach (var k in _keys)
                        if (Check(_indexer.GetValue(keyboard, new[] { k }), prop)) return true;
                    return false;
                }
                return legacy();
            }
            catch { return false; }
        }

        private bool Check(object control, string prop)
        {
            if (control == null) return false;
            if (!_props.TryGetValue(prop, out var pi)) _props[prop] = pi = control.GetType().GetProperty(prop);
            return pi != null && (bool)pi.GetValue(control);
        }

        // Иконка клавиши для подсказки — из набора спрайтов самой игры; если нет, просто [Имя].
        public static string IconTag(string keyName)
        {
            keyName = (keyName ?? "F").Trim();
            if (MouseAliases.TryGetValue(keyName, out string button))
                return SpriteTag("inputPathToSpriteTagMouse", button) ?? "[" + keyName + "]";
            string first = KeyAliases.TryGetValue(keyName, out var list) ? list[0] : keyName;
            return SpriteTag("inputPathToSpriteTagKeyboard", first) ?? "[" + keyName + "]";
        }

        internal static string SpriteTag(string dictName, string key)
        {
            try
            {
                Type isd = Refs.GameType("InputSpriteData");
                object inst = null;
                for (Type t = isd; t != null && inst == null; t = t.BaseType)
                    inst = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                if (AccessTools.Field(isd, dictName)?.GetValue(inst) is System.Collections.Generic.Dictionary<string, string> dict)
                    foreach (var kv in dict)
                        if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            catch (Exception e) { Plugin.Error("Не нашёл иконку клавиши: " + e.Message); }
            return null;
        }
    }
}
