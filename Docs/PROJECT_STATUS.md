# 🤖 GFS-X Ball Grasping — Project Status

> **Последнее обновление**: 2026-06-03
> **Текущая версия кода**: v18 (фиксы hard-stop + angular deadzone)
> **Последнее обучение**: nvidiarunv9 (50M шагов, 4.8 часов) — GrabSuccess=85.5%
> **Статус**: v18 фиксы багов инфраструктуры (EMA, motor ramp, angular deadzone) — тестируем на реальном роботе

---

## 1. Что это за проект

Гусеничный робот **XiaoRGeek GFS-X** (Raspberry Pi 4B) должен **найти мяч в комнате и схватить его клешнёй**. Обучение через **Unity ML-Agents (PPO)** в симуляции, затем перенос на реального робота через ROS 1.

### Задача агента
1. **Найти мяч** — может быть где угодно, даже за пределами FOV
2. **Подъехать к мячу** — не врезаясь в стены
3. **Схватить клешнёй** — камера не видит клешню, используется ИК датчик
4. **Остановиться** — после захвата робот должен стоять и не двигаться

---

## 2. Архитектура системы

```
┌─────────────────────────────────────────────────────────┐
│                    WINDOWS PC                           │
│                                                         │
│  Unity (ML-Agents)          YOLO (30-40 FPS)           │
│  ├── RobotBrain.cs          ├── Распознаёт мяч         │
│  ├── VirtualSensors.cs      └── UDP → RealVision.cs    │
│  ├── SimulatedYoloCamera.cs ← для тренировки           │
│  ├── RealVision.cs          ← для инференса (YOLO)     │
│  ├── TrackController.cs     TensorBoard (метрики)       │
│  ├── GripperController.cs                               │
│  └── ROSBridge.cs ──────────────┐                       │
│                                  │ ROS TCP              │
└──────────────────────────────────┼───────────────────────┘
                                   │
┌──────────────────────────────────┼───────────────────────┐
│              RASPBERRY PI 4B     │                       │
│                                  ▼                       │
│  unity_master.py (ROS node)                              │
│  ├── /cmd_vel → L298N моторы (PWM 0-100%)               │
│  ├── /cmd_gripper → Сервоприводы MG995 (#1-4)           │
│  ├── /cmd_camera_pan → Серво камеры (#7)                │
│  └── /sensor/data → УЗ + ИК (публикация 10 Hz)         │
│                                                          │
│  unity_gripper_ir.py (отдельный ROS node)               │
│  └── /sensor/gripper_ir → ИК клешни (20 Hz)             │
└──────────────────────────────────────────────────────────┘
```

### Ключевые числа реального робота (из unity_master.py)
| Параметр | Значение | Где в коде |
|---|---|---|
| MAX_SPEED | 0.5 м/с | unity_master.py:57 |
| MIN_MOTOR_PWM | **35** (мёртвая зона) | unity_master.py:59 |
| MAX_PWM_STEP | **15** (рампа разгона) | unity_master.py:63 |
| MAX_CAMERA_STEP | **15°/тик** (серво камеры) | unity_master.py:211 |
| Sensor poll | **10 Hz** | unity_master.py:301 |
| Масса | ~2.5 кг | Документация |
| УЗ датчик | HC-SR04, конус ~30° | Документация |
| Серво клешни | MG995, 0.17 сек/60° | Документация |

---

## 3. Observation Space (15 значений)

| # | Наблюдение | Тип | Диапазон | Добавлено |
|---|---|---|---|---|
| 0 | Ultrasonic distance | float | 0-1 (норм.) | v1 |
| 1 | Left IR | int | 0/1 | v1 |
| 2 | Right IR | int | 0/1 | v1 |
| 3 | Gripper IR | int | 0/1 | v1 |
| 4 | Ball angle | float | -1..1 | v4 |
| 5 | Ball distance | float | 0-1 (норм.) | v4 |
| 6 | Last known direction | float | -1..1 | v4 |
| 7 | Ball visible | float | 0/1 | v4 |
| 8 | Camera yaw | float | -1..1 | v6 |
| 9 | Has ball | float | 0/1 | v3 |
| 10 | Displacement X (ego) | float | -1..1 | v16 |
| 11 | Displacement Z (ego) | float | -1..1 | v16 |
| 12 | Heading | float | 0..1 | v16 |
| 13 | Speed (ego) | float | 0..1 | v16 |
| 14 | Time since ball seen | float | 0..1 | v16 |

**LSTM память**: sequence_length=64, memory_size=256

### Action Space (3 continuous)
| # | Действие | Диапазон |
|---|---|---|
| 0 | Gas (вперёд/назад) | -1..1 |
| 1 | Steering (лево/право) | -1..1 |
| 2 | Camera yaw | -1..1 |

**Клешня НЕ в action space** — автоматический захват по ИК датчику.

---

## 4. Reward Structure (8 сигналов)

### ⚠️ КРИТИЧЕСКИ ВАЖНО: Только 8 сигналов!

Предыдущие версии (v1-v8) имели 19-27 reward-сигналов, что приводило к дёрганью и нерешительности. **НЕ ДОБАВЛЯЙ новые reward-сигналы без крайней необходимости.**

| # | Сигнал | Значение | Зачем | Добавлено |
|---|---|---|---|---|
| 1 | **Distance delta** | `delta × 2.0` (оба знака!) | Единственная навигация | v9 |
| 2 | **Terminal grab** | `+5.0` (при hold ≥ 50 тиков) | Главная цель | v3 |
| 3 | **Hold per step** | `+0.02/шаг` | Не бросай мяч | v3 |
| 4 | **Sensor proximity** | `-0.03×sonarProx` и `-0.01×IR` | Избегай стен по ДАТЧИКАМ | v9 |
| 5 | **Action Rate Penalty** | `-0.05 × ‖aₜ - aₜ₋₁‖²` | Плавность движений | v9 |
| 6 | **Mild reverse penalty** | `-0.005` (gas<-0.1, не retry, не у стены) | Не езди назад без причины | v15 |
| 7 | **Proximity slow-down** | `+0.005` (dist<0.3, gas 0.01-0.3) | Замедляйся перед мячом | v15 |
| 8 | **Blind crawl** | `+0.003` (wasClose, gas 0.01-0.3) | Ползи вперёд в слепой зоне | v15 |

### Что УБРАНО (и почему НЕ НАДО возвращать)
- ❌ OnCollisionEnter wall penalty — на реальном роботе нет коллизий, только датчики
- ❌ Phase-based rewards — alignment, crawl, speed penalties
- ❌ Reverse ramp penalty — action rate penalty уже это покрывает
- ❌ Camera jump penalty — camera step limit + action rate penalty
- ❌ Curiosity reward — конфликтует с dense reward, добавляет хаос

---

## 5. Sim-to-Real механизмы

### SimulatedYoloCamera.cs (v17)
- **Проекция камеры**: Camera.WorldToViewportPoint() → идентично YOLO
- **Горизонтальный FOV**: 40° (откалибровано линейкой) → vFOV пересчитывается автоматически
- **Aspect**: 4:3 (640×480) — фиксированный, не зависит от Editor/Build
- **ballRadius**: v17 — автоматически из `lossyScale.x * 0.5` (раньше хардкод 0.03!)
- **Автопоиск мяча**: по тэгу TargetBall если targetBall не назначен
- **Окклюзия**: raycast с исключением слоёв робота
- **Debounce**: 0.3s (имитация BoT-SORT трекера YOLO)

### В симуляции (TrackController.cs)
- **Мёртвая зона**: `motorDeadzone = 0.35` (реальный MIN_MOTOR_PWM=35 — матчит!)
- **Angular deadzone**: v18 — применяется и к повороту (раньше только linear)
- **Рампа ускорения**: `maxAccelPerStep = 0.15` (реальный MAX_PWM_STEP=15)
- **Инерция**: `linearDamping = 8`

### В симуляции (RobotBrain.cs)
- **Camera step limit**: макс 15°/тик (реальный MAX_CAMERA_STEP)
- **Burst dropout**: YOLO теряет мяч на 3-8 кадров подряд (не покадрово)
- **Раздельный шум**: angle ±noiseAmp, distance ±noiseAmp×3
- **Стартовая ротация**: ±180°
- **Latency**: 2-5 шагов задержки действий, 1 шаг задержки сенсоров

### В симуляции (VirtualSensors.cs)
- **Конусный УЗ**: 5 лучей (0°, ±7°, ±15°) — матчит HC-SR04 конус 30°

### На реальном роботе (ROSBridge.cs)
- **EMA smoothing**: `α=0.8` на командах перед отправкой в ROS

### Domain Randomization (config.yaml)
| Параметр | Диапазон |
|---|---|
| moveSpeed | 0.3 - 0.7 |
| turnSpeed | 80 - 160 |
| smoothing | 0.01 - 0.25 |
| mass | 1.0 - 4.0 |
| ball_scale | 0.04 - 0.07 (×Random ±20%) |
| ball_mass | 0.05 - 0.2 |
| vision_noise | 0.02 - 0.06 |
| vision_dropout | 0.05 - 0.15 |

---

## 6. Training Config (config.yaml)

- **Trainer**: PPO
- **Network**: 256 hidden × 2 layers + LSTM (64 seq, 256 mem)
- **Batch**: 2048, Buffer: 40960
- **Learning rate**: 2.5e-4 (linear schedule)
- **Decision Period**: 5 (в Inspector DecisionRequester, не в yaml)
- **Max steps**: 50M
- **Parallel**: 40 комнат × 16 envs = **640 агентов**
- **Environment params**: ball_max_distance (0.5-3.0), ball_max_offset (0-1.2), episode_length (1500)

### Команда запуска
```bash
mlagents-learn config.yaml --run-id=v17run --env=Buildv22/ROS_test.exe --num-envs=16 --no-graphics --env-args -logFile NUL
```

---

## 7. Логика клешни (v17)

### Автоматический захват (не через action space)
```
ИК сработал (gripperIR=1)?
├── Мяч был виден камерой за последнюю 1 сек? (ballRecentlySeen)
│   ├── ДА → CloseGripper → hasBall=true → РОБОТ СТОИТ
│   │       └── ИК потом пропал?
│   │           └── Ждём 2 сек → если не вернулся → OpenGripper
│   └── НЕТ → Ложное срабатывание (стул, стена) → клешню ОТКРЫТЬ
└── В тренировке: всегда разрешаем (мяч гарантированно есть)
```

### GripperController.cs (v17)
- **CloseGripper**: в симуляции ищет Unity-объект мяча; на реальном роботе (`useRealSensors=true`) → `hasBall=true` напрямую по ИК
- **OpenGripper**: корректно работает и в симуляции, и на реале (даже без grabbedBall)
- **Авто-захват**: Update() вызывает CloseGripper() когда gripperIR=1

### RobotBrain.cs gripper logic (v17)
- **allowGrip**: `isTraining || ballRecentlySeen` — клешня НЕ закроется на стул
- **holdWithoutIR**: 100 тиков (2 сек) — анти-дребезг ИК при сдвиге мяча
- **hasBall=true**: робот стоит, PublishCommand(0,0), PublishGripperCmd(2)
- **holdTicks ≥ 50**: при тренировке → +5.0 reward + EndEpisode

---

## 8. История обучений

| Версия | Изменения | Результат |
|---|---|---|
| v1-v3 | Базовая навигация | Работал в симуляции |
| v4-v5 | Добавлены фазы, retry, gripper | Захват в симуляции работал |
| v6 | Dense rewards (19 сигналов) | Дёрганье на реальном роботе |
| v7 | Упрощение (неудачное) | Робот ехал только назад |
| v8 | Восстановление v4-v5 логики | Частично работало |
| v9 | NVIDIA overhaul: 5 rewards, motor model, cone UZ, EMA | GrabSuccess=0.45, curriculum регрессия |
| v10 | Fixed DR (без curriculum), blind crawl | GrabSuccess=0.50, реал: задний ход |
| v11 | Reverse penalty, diagnostics | Sim стабильно, реал: задний ход |
| v12 | **FIX: `pwm_left = -pwm_left`** | Левый мотор инвертирован! Все v6-v11 были бесполезны |
| v13 | SimulatedYoloCamera, vision_dropout 5-15% | YOLO-идентичные наблюдения |
| v14-v16 | Camera FOV фикс, EMA 0.8, sim log анализ | GrabSuccess=84%, но ballSeen=0% при inference! |
| **v17** | **Camera init order, auto ballRadius, hasBall для реала, smart gripper, автопоиск мяча** | **nvidiarunv9: GrabSuccess=85.5%, Reward=5.27** |
| **v18** | **Hard-stop EMA (ROSBridge), hard-stop ramp (unity_master), angular deadzone (TrackController)** | **Фиксы инфраструктуры, не требуют переобучения** |

> **⚠️ КРИТИЧЕСКАЯ НАХОДКА (v12)**: Робот "ехал назад" не из-за плохой политики, а из-за инвертированного мотора. Фикс: `pwm_left = -pwm_left` в `unity_master.py`.

> **⚠️ КРИТИЧЕСКАЯ НАХОДКА (v16)**: Все модели v13-v16 обучены с БИТОЙ камерой: FOV=26° вместо 30.5°, ballRadius=0.03 хардкод при ball_scale=0.04-0.07, ballSeen=0% при inference. v17 всё исправлен.

> **⚠️ КРИТИЧЕСКАЯ НАХОДКА (v18)**: Робот "крутился после захвата" не из-за плохой политики, а из-за цепочки багов: EMA-остатки в ROSBridge → MIN_MOTOR_PWM бустил PWM=6 до 35 → робот крутился на PWM=35. Фикс: hard-stop override в ROSBridge + unity_master + angular deadzone в TrackController.

---

## 9. Файловая структура

### Unity (Windows PC)
```
C:\Users\Admin\Unity\ROS_test\
├── Assets/
│   ├── RobotBrain.cs          ← ML-Agent (reward, observations, actions)
│   ├── SimulatedYoloCamera.cs ← YOLO-идентичное зрение для тренировки (v17)
│   ├── RealVision.cs          ← YOLO зрение для инференса
│   ├── TrackController.cs     ← Физика моторов (deadzone, ramp, damping)
│   ├── VirtualSensors.cs      ← УЗ конус + ИК (рейкасты или ROS)
│   ├── GripperController.cs   ← Авто-захват по ИК (v17: работает на реале)
│   ├── ROSBridge.cs           ← ROS коммуникация + EMA 0.8
│   └── VirtualCamera.cs       ← УСТАРЕЛ, НЕ ИСПОЛЬЗУЕТСЯ
├── config.yaml                ← PPO + DR параметры
├── sim_log.csv                ← Логи цифрового робота
├── real_log.csv               ← Логи реального робота
└── PROJECT_STATUS.md          ← ЭТОТ ФАЙЛ
```

### Raspberry Pi (R:\)
```
R:\ (home директория pi@raspberrypi)
├── unity_master.py            ← Главный ROS node (моторы, серво, сенсоры)
├── unity_gripper_ir.py        ← ИК клешни (20 Hz)
├── start_robot.sh             ← Запуск Docker + ROS nodes
└── XiaoRGeek/                 ← Драйверы GPIO/моторов/серво
```

---

## 10. Правила для AI-ассистента

### 🔴 НЕ ДЕЛАЙ
1. **НЕ добавляй reward-сигналы** — 5 сигналов это осознанное решение
2. **НЕ возвращай `rb.linearVelocity = Vector3.zero`** — убивает инерцию
3. **НЕ возвращай OnCollisionEnter для стен** — нет коллизий на реальном роботе
4. **НЕ меняй observation space без веской причины** — ломает модели
5. **НЕ добавляй curiosity reward** — конфликтует с dense reward
6. **НЕ убирай EMA smoothing** — без него реальный робот трясётся
7. **НЕ убирай мёртвую зону мотора** — мотор не крутится при PWM<20
8. **НЕ добавляй шум/dropout при inference** — реальный YOLO и так шумит
9. **НЕ меняй track speed при inference** — реальная скорость определяется моторами

### 🟢 ДЕЛАЙ
1. **Спрашивай метрики TensorBoard** перед изменениями
2. **Сравнивай с реальным поведением** — sim может выглядеть хорошо, а real плохо
3. **Меняй по одному параметру** — иначе непонятно что помогло
4. **Сохраняй чекпоинты** — checkpoint_interval=500000
5. **Проверяй Inspector** — код может задать значение, но Inspector перезапишет
6. **Анализируй sim_log.csv и real_log.csv** — сравнивай ballSeen, gas, steering
7. **Слушай пользователя** — если он говорит "клешня не разжимается", не пиши что разжимается
