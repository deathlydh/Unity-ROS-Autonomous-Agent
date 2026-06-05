# 📋 TODO: Что ещё не сделано

> Последнее обновление: 2026-06-01

---

## 🔴 Критично (сделать перед обучением)

- [ ] **Сбилдить проект** — все v17 фиксы только в коде, нужен новый Build
- [ ] **Проверить в Editor** — запустить, убедиться что Console показывает:
  - `FOV=30.5° (hFOV=40°)`
  - `targetBall=...` (не NULL)
  - ballSeen > 0% в sim_log.csv
- [ ] **Проверить motorDeadzone** — сейчас 0.35 в TrackController.cs, но MIN_MOTOR_PWM=20 на реале. Возможно нужно 0.20.

---

## 🟡 Сделать после обучения

- [ ] **Тест цифрового инференса** — робот должен ехать к мячу, не кружиться
- [ ] **Тест реального робота** — проверить:
  - [ ] Захват мяча → робот стоит
  - [ ] Ложный ИК (стул) → клешня НЕ закрывается
  - [ ] Console: `[RobotBrain] Захват: gripperIR=1, ballRecentlySeen=True`
  - [ ] Console: `[RobotBrain] Ложное срабатывание ИК: мяч не был виден ...`
- [ ] **Сравнить sim_log.csv и real_log.csv** через analyze_logs.py
- [ ] **MIN_MOTOR_PWM = 20** — проверить что уже применено на Raspberry Pi (`R:\unity_master.py`)

---

## 🟢 Улучшения на будущее

- [ ] **Inference episode reset** — LSTM state может деградировать без ресета. Добавить soft reset каждые N шагов
- [ ] **Calibrate RealVision distance curve** — `pixelYToVirtualDistance` AnimationCurve в RealVision.cs должна совпадать с SimulatedYoloCamera distance mapping
- [ ] **BlindZonePenalty** — раньше был скрипт, который штрафовал за загон мяча в слепые зоны. Сейчас не используется. Возможно стоит вернуть через distance delta
- [ ] **Curriculum learning** — сейчас отключён (фиксированные диапазоны). Можно вернуть для постепенного увеличения сложности
- [ ] **Тест с разными мячами** — YOLO распознаёт target class, но domain randomization по цвету/текстуре мяча не делалась
- [ ] **Оценка скорости подъезда** — реальный робот едет слишком быстро, мяч часто в слепых зонах. Возможно нужен speed penalty при близкой дистанции

---

## ❌ НЕ НУЖНО делать

- ~~Добавить curiosity reward~~ — конфликтует с dense reward
- ~~Добавить OnCollisionEnter~~ — нет коллизий на реальном роботе
- ~~Добавить шум при inference~~ — YOLO и так шумит
- ~~Менять track speed при inference~~ — определяется реальными моторами
- ~~Вернуть VirtualCamera~~ — заменена на SimulatedYoloCamera
