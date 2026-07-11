# 📋 TODO: Что ещё не сделано

> Последнее обновление: 2026-06-16

---

## 🔴 Критично (анализ Brain 26)

- [ ] **Проанализировать логи обучения Brain 26** — TensorBoard: GrabSuccess, Cumulative Reward, Gas, Reverse%
- [ ] **Тест в симуляторе** — мяч перед роботом + мяч сбоку → проверить что модель находит и подъезжает
- [ ] **Тест на реальном роботе** — проверить:
  - [ ] Подъезд к мячу → замедление → центровка → захват
  - [ ] Мяч сбоку → робот поворачивается и находит
  - [ ] Захват мяча → робот СТОИТ (hasBall=true → track.Move(0,0))
- [ ] **Сравнить Brain 26 vs Brain 25** — через diagnostic_log: ballSeen%, gas vs dist, steer quality

---

## 🟡 Сделать после успешного теста

- [ ] **Добавить запись видео с YOLO-камеры** — для диагностики bbox, confidence, timing
- [ ] **Логирование PWM в diagnostic_log** — что моторы реально получают
- [ ] **Логирование YOLO FPS** — если < 5 fps, модель работает на устаревших данных
- [ ] **ONNX behavior test для Brain 26** — `onnx_behavior.py` / `compare_models.py`
- [ ] **Проверить повторный запуск** — модель может деградировать без episode reset при inference (LSTM state drift)

---

## 🟢 Улучшения на будущее

- [ ] **Inference episode reset** — LSTM state может деградировать без ресета. Добавить soft reset каждые N шагов
- [ ] **Calibrate RealVision distance curve** — `pixelYToVirtualDistance` AnimationCurve в RealVision.cs должна совпадать с SimulatedYoloCamera distance mapping
- [ ] **Тест с разными мячами** — YOLO распознаёт target class, но domain randomization по цвету/текстуре мяча не делалась
- [ ] **ball_max_distance увеличить до 4-5м** — реальная комната ~5м, но сейчас max=3.0м
- [ ] **Добавить одометрию** — логировать `transform.position.x, z` для визуализации траектории
- [ ] **Multi-ball episodes** — после захвата первого мяча → новый мяч → тренирует поиск

---

## ✅ Сделано (v26)

- [x] 360° спавн мяча (RobotBrain.cs ResetBall)
- [x] Proximity-scaled distance reward (distDelta × 2-6x)
- [x] Speed penalty near ball (dist<0.25, |gas|>0.4 → -0.01)
- [x] Alignment bonus (dist<0.4, |angle|<0.15 → +0.005)
- [x] TURN_K: 0.15 → 0.30 (unity_master.py)
- [x] MAX_LINEAR: 0.25 cap (unity_master.py)
- [x] Удалён ball_max_offset из config.yaml
- [x] Обучение Brain 26 запущено и завершено
- [x] ONNX reverse engineering (Brain 8 vs Brain 25)

---

## ❌ НЕ НУЖНО делать

- ~~Добавить curiosity reward~~ — конфликтует с dense reward
- ~~Добавить OnCollisionEnter~~ — нет коллизий на реальном роботе
- ~~Добавить шум при inference~~ — YOLO и так шумит
- ~~Менять track speed при inference~~ — определяется реальными моторами
- ~~Вернуть VirtualCamera~~ — заменена на SimulatedYoloCamera
- ~~Curriculum learning~~ — пользователь отказался, фиксированные диапазоны
