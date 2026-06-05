# 🔧 v17 Changes & Known Issues

> **Дата**: 2026-06-01
> **Статус**: Все фиксы применены, нужно переобучить

---

## Фиксы v17 (уже в коде)

### 1. SimulatedYoloCamera.cs — Camera init order
**Было**: `enabled=false` → `aspect = 4/3` → `fieldOfView = vFOV`
**Стало**: `aspect = 4/3` → `fieldOfView = vFOV` → `enabled=false`
**Почему**: Unity может НЕ обновлять projection matrix на disabled камере → ballSeen=0%

### 2. SimulatedYoloCamera.cs — Auto ballRadius
**Было**: `ballRadius = 0.03f` (хардкод)
**Стало**: `ballRadius = targetBall.lossyScale.x * 0.5f` (каждый кадр)
**Почему**: ball_scale в обучении 0.04-0.07 (×±20% = 0.032-0.084). Хардкод 0.03 давал неправильную дистанцию.

### 3. SimulatedYoloCamera.cs — Автопоиск мяча по тэгу
**Было**: targetBall назначался только через RobotBrain → при inference без ballPrefab = NULL
**Стало**: если targetBall==null, ищет GameObject.FindGameObjectWithTag("TargetBall")
**Почему**: Пользователь тестирует inference, кидая мяч вручную на сцену

### 4. GripperController.cs — hasBall на реальном роботе
**Было**: CloseGripper() ищет Unity-объект мяча → на реале grabbedBall=null → hasBall=false ВСЕГДА
**Стало**: если `useRealSensors && gripperIR==1 && grabbedBall==null` → `hasBall=true` напрямую
**Почему**: Без этого робот никогда не останавливался после захвата

### 5. GripperController.cs — OpenGripper для реала
**Было**: `if (hasBall && grabbedBall != null)` → на реале hasBall не сбрасывался
**Стало**: `if (!hasBall) return;` → обработка grabbedBall отдельно → `hasBall=false` всегда

### 6. RobotBrain.cs — Умная клешня (ballRecentlySeen)
**Было**: `if (!hasBall && gripperSensorActive)` → хватал всё подряд (стулья, стены)
**Стало**: `if (!hasBall && gripperSensorActive && allowGrip)` где `allowGrip = isTraining || ballRecentlySeen`
**Почему**: Ложные срабатывания ИК от ножек стульев → клешня закрывалась на весь эпизод

### 7. RobotBrain.cs — Анти-дребезг ИК (holdWithoutIR)
**Было**: `if (hasBall && !gripperSensorActive)` → `OpenGripper()` мгновенно
**Стало**: `holdWithoutIR++` → ждём 100 тиков (2 сек) → потом OpenGripper
**Почему**: Мяч сдвигается в клешне → ИК теряет → клешня разжималась мгновенно

### 8. ROSBridge.cs — EMA 0.8
**Было**: `emaAlpha = 0.4`
**Стало**: `emaAlpha = 0.8`
**Почему**: Более отзывчивое управление, 0.4 слишком сглаживал

---

## Известные проблемы (НЕ починены)

### 🟡 motorDeadzone = 0.35 vs реальный MIN_MOTOR_PWM = 20
В `TrackController.cs` стоит deadzone 0.35 (от старого значения PWM=35). Реальный робот теперь использует `MIN_MOTOR_PWM = 20`. Возможно нужно уменьшить deadzone до `0.20`, но это требует тестирования.

### 🟡 Inference не ресетит эпизоды
При `isTraining=false` нет `EndEpisode()` ни по таймауту, ни по success. LSTM-state может деградировать. Возможное решение: периодический soft reset observation history.

### 🟡 Inference не имеет latency
Обучение идёт с latency 2-5 шагов, inference без latency. Модель обучена компенсировать задержку, при мгновенном исполнении может перестреливать. Но на реальном роботе ROS вносит естественную задержку.

### 🟡 Config: ball_max_distance 0.5-3.0
Возможно диапазон слишком мал. Реальная комната ~5м. Но curriculum поднимает дистанцию постепенно.

---

## Что проверить после обучения v17

1. **Console лог при запуске**:
   ```
   [SimulatedYoloCamera] Initialized: FOV=30.5° (hFOV=40°), aspect=1.33, ...
   [SimulatedYoloCamera] targetBall найден автоматически: TargetBall_Instance
   ```

2. **TensorBoard**: GrabSuccess должен быть ≥0.7

3. **sim_log.csv**: ballSeen должен быть >0% (раньше был 0%!)

4. **Цифровой инференс**: робот должен ехать к мячу, а не кружиться

5. **Реальный робот**:
   - Захват мяча → робот СТОИТ (hasBall=true → track.Move(0,0))
   - Ножка стула → клешня НЕ закрывается (ballRecentlySeen=false)
   - Console: `[RobotBrain] Захват: gripperIR=1, ballRecentlySeen=True`
