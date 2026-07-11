# 🔧 v17 Changes & Known Issues

> **Дата**: 2026-06-01
> **Статус**: Обучение v17 проведено (nvidiarunv9, GrabSuccess=85.5%). Версия УСТАРЕЛА, заменена v26.

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

### 4. GripperController.cs — hasBall на реальном роботе
**Было**: CloseGripper() ищет Unity-объект мяча → на реале grabbedBall=null → hasBall=false ВСЕГДА
**Стало**: если `useRealSensors && gripperIR==1 && grabbedBall==null` → `hasBall=true` напрямую

### 5. GripperController.cs — OpenGripper для реала
**Было**: `if (hasBall && grabbedBall != null)` → на реале hasBall не сбрасывался
**Стало**: `if (!hasBall) return;` → обработка grabbedBall отдельно → `hasBall=false` всегда

### 6. RobotBrain.cs — Умная клешня (ballRecentlySeen)
**Было**: `if (!hasBall && gripperSensorActive)` → хватал всё подряд (стулья, стены)
**Стало**: `if (!hasBall && gripperSensorActive && allowGrip)` где `allowGrip = isTraining || ballRecentlySeen`

### 7. RobotBrain.cs — Анти-дребезг ИК (holdWithoutIR)
**Было**: `if (hasBall && !gripperSensorActive)` → `OpenGripper()` мгновенно
**Стало**: `holdWithoutIR++` → ждём 100 тиков (2 сек) → потом OpenGripper

### 8. ROSBridge.cs — EMA 0.8
**Было**: `emaAlpha = 0.4`
**Стало**: `emaAlpha = 0.8`

---

## v18 Changes (2026-06-03) — инфраструктурные фиксы

### 1. ROSBridge.cs — Hard-stop EMA override
Если gas=0 и steer=0, EMA сбрасывается в 0 мгновенно (без остатков).

### 2. unity_master.py — Hard-stop ramp override
Если целевой PWM=0, PWM обнуляется мгновенно (без плавного снижения).

### 3. TrackController.cs — Angular deadzone 0.15
Smoothed angular < 0.15 → angular = 0. Мягче чем linear (0.35), т.к. модель даёт тонкие steer-коррекции.

---

## v26 Changes (2026-06-15) — переобучение Brain 26

### Motor pipeline fixes (unity_master.py)
1. **TURN_K**: 0.15 → **0.30** — усиление дифференциала поворота
2. **MAX_LINEAR**: **0.25** — cap скорости (50%) чтобы не проезжать мимо мяча

### RobotBrain.cs — 360° ball spawning
**Было**: `transform.forward * randomZ + transform.right * randomX` (только перед роботом)
**Стало**: `Quaternion.Euler(0, Random.Range(0,360), 0) * Vector3.forward * spawnDist` (любое направление)
- Добавлена проверка Raycast вниз (наличие пола — не за границей арены)
- OverlapSphere проверка стен/объектов сохранена

### RobotBrain.cs — Reward system (4 изменения)
1. **Distance delta proximity-scaled**: `distDelta * 2.0` → `distDelta * (2.0 + 4.0*(1-dist))` — множитель 2x→6x
2. **Speed penalty (NEW)**: `-0.01` при dist<0.25 и |gas|>0.4 — не таранить мяч
3. **Alignment bonus (NEW)**: `+0.005` при dist<0.4 и |angle|<0.15 — центрировка
4. Slow-down bonus (v15) сохранён без изменений

### config.yaml
- Удалён `ball_max_offset` (не нужен с 360° спавном)
- `ball_max_distance`: 0.5 - 3.0 (без изменений)
- `episode_length`: 1500 (без изменений)
