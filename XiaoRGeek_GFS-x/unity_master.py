#!/usr/bin/env python3
import sys
import rospy
import time
import traceback
import atexit
from geometry_msgs.msg import Twist, Vector3, Quaternion
from std_msgs.msg import Int32, Float32

# Добавляем путь к библиотекам XiaoR Geek
sys.path.append('/root/XiaoRGeek')

print("--- Инициализация XiaoR драйверов ---")

HAS_GPIO = False
try:
    import xr_gpio as gpio
    HAS_GPIO = True
    print("✅ Драйвер моторов (xr_gpio) загружен успешно!")
except Exception as e:
    print("❌ ОШИБКА загрузки xr_gpio:")
    print(traceback.format_exc())

HAS_SENSORS = False
us = None
try:
    from xr_ultrasonic import Ultrasonic
    us = Ultrasonic()
    HAS_SENSORS = True
    print("✅ Драйвер Ultrasonic загружен успешно!")
except Exception as e:
    print("❌ ОШИБКА загрузки xr_ultrasonic:")
    print(traceback.format_exc())

HAS_SERVO = False
try:
    if HAS_SENSORS:
        # Ультразвук уже глобально инициализировал серво в собственном драйвере!
        import xr_ultrasonic
        servo = xr_ultrasonic.servo
        HAS_SERVO = True
        print("✅ Драйвер серво переиспользован из Ultrasonic (защита от двойной I2C)!")
    else:
        from xr_servo import Servo
        servo = Servo()
        HAS_SERVO = True
        print("✅ Драйвер сервомоторов (xr_servo) загружен успешно!")
except Exception as e:
    print("❌ ОШИБКА загрузки xr_servo:")
    print(traceback.format_exc())


# ==========================================
# КОНФИГУРАЦИЯ И ЛОГИКА МОТОРОВ
# ==========================================
L = 0.15  
MAX_SPEED_M_S = 0.5 
PWM_CONVERSION_FACTOR = 100.0 / MAX_SPEED_M_S
MIN_MOTOR_PWM = 35

# --- SOFT-START: Защита от пускового тока и Back-EMF ---
# Максимальное изменение PWM за 1 тик. При 50Hz: разгон 0→100% за ~0.14 сек.
MAX_PWM_STEP = 15
prev_pwm_left = 0.0
prev_pwm_right = 0.0

def clamp_pwm(val):
    return max(min(val, 100.0), -100.0)

def set_motors_pwm(pwm_left, pwm_right):
    global prev_pwm_left, prev_pwm_right
    if not HAS_GPIO: return
    
    # --- SOFT-START: Ограничиваем скорость нарастания ---
    delta_l = pwm_left - prev_pwm_left
    if abs(delta_l) > MAX_PWM_STEP:
        pwm_left = prev_pwm_left + (MAX_PWM_STEP if delta_l > 0 else -MAX_PWM_STEP)
    
    delta_r = pwm_right - prev_pwm_right
    if abs(delta_r) > MAX_PWM_STEP:
        pwm_right = prev_pwm_right + (MAX_PWM_STEP if delta_r > 0 else -MAX_PWM_STEP)
    
    prev_pwm_left = pwm_left
    prev_pwm_right = pwm_right
    
    abs_l = abs(pwm_left)
    if 0 < abs_l < MIN_MOTOR_PWM: abs_l = MIN_MOTOR_PWM
    abs_r = abs(pwm_right)
    if 0 < abs_r < MIN_MOTOR_PWM: abs_r = MIN_MOTOR_PWM

    if int(abs_l) == 0 and int(abs_r) == 0:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 0)
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 0)
        gpio.ena_pwm(0)
        gpio.enb_pwm(0)
        return

    gpio.ena_pwm(int(abs_l))
    gpio.enb_pwm(int(abs_r))

    if pwm_left > 0:
        gpio.digital_write(gpio.IN1, 1)
        gpio.digital_write(gpio.IN2, 0)
    elif pwm_left < 0:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 1)
    else:
        gpio.digital_write(gpio.IN1, 0)
        gpio.digital_write(gpio.IN2, 0)

    if pwm_right > 0:
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 1)
    elif pwm_right < 0:
        gpio.digital_write(gpio.IN3, 1)
        gpio.digital_write(gpio.IN4, 0)
    else:
        gpio.digital_write(gpio.IN3, 0)
        gpio.digital_write(gpio.IN4, 0)

SAFETY_STOP_CM = 50  # Локальный стоп если УЗ < 50см и робот едет ВПЕРЁД

def vel_callback(data):
    global filtered_cm, last_cmd_vel_time
    last_cmd_vel_time = time.time()
    
    # ЛОКАЛЬНАЯ ЗАЩИТА: Убрана по запросу пользователя (Hard Stop). Нейросеть должна сама избегать стен.
    
    # ИНВЕРСИЯ РУЛЯ: меняем знаки +/- чтобы Left было Left. 
    # МАГНИТУДА: умножаем на MAX_SPEED_M_S чтобы steering=1.0 полностью раскачивал ШИМ до 100%
    v_left  = data.linear.x - (data.angular.z * MAX_SPEED_M_S)
    v_right = data.linear.x + (data.angular.z * MAX_SPEED_M_S)
    pwm_left = clamp_pwm(v_left * PWM_CONVERSION_FACTOR)
    pwm_right = clamp_pwm(v_right * PWM_CONVERSION_FACTOR)
    
    # Отладка моторов
    if abs(pwm_left) > 1 or abs(pwm_right) > 1:
        print(f"[-] Motor PWM: L={pwm_left:.1f}, R={pwm_right:.1f} (In Linear: {data.linear.x:.2f}, Angular: {data.angular.z:.2f})")
        
    set_motors_pwm(pwm_left, pwm_right)

# --- WATCHDOG: Если Unity отключился, стопим моторы ---
last_cmd_vel_time = time.time()
WATCHDOG_TIMEOUT = 0.5  # секунд без команды → СТОП

def watchdog_callback(event):
    global prev_pwm_left, prev_pwm_right
    if time.time() - last_cmd_vel_time > WATCHDOG_TIMEOUT:
        if abs(prev_pwm_left) > 0 or abs(prev_pwm_right) > 0:
            print("⚠️ WATCHDOG: Нет /cmd_vel %.1f сек! АВАРИЙНАЯ ОСТАНОВКА!" % WATCHDOG_TIMEOUT)
            set_motors_pwm(0, 0)
            prev_pwm_left = 0.0
            prev_pwm_right = 0.0


# ==========================================
# КОНФИГУРАЦИЯ И ЛОГИКА СЕРВОМОТОРОВ
# ==========================================
SERVO_BASE = 1      
SERVO_SHOULDER = 2  
SERVO_ELBOW = 3     
SERVO_CLAW = 4      
SERVO_CAMERA_PAN = 7

ANGLE_BASE_CENTER = 90
ANGLE_SHOULDER_UP = 90
ANGLE_ELBOW_UP = 90
ANGLE_SHOULDER_DOWN = 20
ANGLE_ELBOW_DOWN = 130
ANGLE_CLAW_OPEN = 50    # Калибровка: ниже 43 начинает хрустеть
ANGLE_CLAW_CLOSE = 89   # Калибровка: на 90 с мячом хрустит
current_claw_angle = None # Кэш для предотвращения лишних записей в I2C

def init_arm():
    global current_claw_angle
    if not HAS_SERVO: return
    print("Инициализация начальной позы манипулятора и камеры...")
    servo.set(SERVO_BASE, ANGLE_BASE_CENTER)
    time.sleep(0.3)
    servo.set(SERVO_SHOULDER, ANGLE_SHOULDER_UP)
    time.sleep(0.3)
    servo.set(SERVO_ELBOW, ANGLE_ELBOW_UP)
    time.sleep(0.3)
    servo.set(SERVO_CLAW, ANGLE_CLAW_OPEN) # Клешня открыта при старте!
    current_claw_angle = ANGLE_CLAW_OPEN
    time.sleep(0.3)
    servo.set(SERVO_CAMERA_PAN, 90)
    print("Рука поднята, камера отцентрирована. Робот готов!")

def gripper_callback(data):
    global current_claw_angle
    cmd = data.data
    if not HAS_SERVO: return
    if cmd == 1:
        # Prepare to grab
        servo.set(SERVO_SHOULDER, ANGLE_SHOULDER_DOWN)
        time.sleep(0.2)
        servo.set(SERVO_ELBOW, ANGLE_ELBOW_DOWN)
        time.sleep(0.2)
        servo.set(SERVO_CLAW, ANGLE_CLAW_OPEN)
        current_claw_angle = ANGLE_CLAW_OPEN
    elif cmd == 2:
        # Grab
        servo.set(SERVO_CLAW, ANGLE_CLAW_CLOSE)
        current_claw_angle = ANGLE_CLAW_CLOSE
        time.sleep(0.5)
        servo.set(SERVO_ELBOW, ANGLE_ELBOW_UP)
        time.sleep(0.2)
        servo.set(SERVO_SHOULDER, ANGLE_SHOULDER_UP)
    elif cmd == 3:
        # Init
        init_arm()

# --- Плавное слежение камеры ---
current_camera_angle = 90  # Текущий угол серво
MAX_CAMERA_STEP = 15       # Макс градусов за один тик (плавное движение)

def camera_callback(data):
    global current_camera_angle
    if not HAS_SERVO: return
    yaw = data.data
    # ИНВЕРСИЯ: Если в Unity камера в одну сторону, а в реальности в другую — меняем знак здесь
    target = 90 - (yaw * 90)
    target = max(0, min(180, target))
    
    # Плавное движение: не больше MAX_CAMERA_STEP градусов за шаг
    diff = target - current_camera_angle
    if abs(diff) > MAX_CAMERA_STEP:
        diff = MAX_CAMERA_STEP if diff > 0 else -MAX_CAMERA_STEP
    
    current_camera_angle += diff
    current_camera_angle = max(0, min(180, current_camera_angle))
    servo.set(SERVO_CAMERA_PAN, int(current_camera_angle))

# ==========================================
# ЧТЕНИЕ СЕНСОРОВ В ТАЙМЕРЕ
# ==========================================
# --- Фильтрация датчиков ---
us_history = [100.0, 100.0, 100.0]
filtered_cm = 500.0 # Глобальная переменная для safety check
# Уменьшаем окно с 5 до 2 кадров для минимальной задержки
ir_l_history = [0, 0]
ir_r_history = [0, 0]
ir_m_history = [0, 0] # История для датчика клешни

def sensor_timer_callback(event):
    global us_history, ir_l_history, ir_r_history, ir_m_history, filtered_cm
    if not HAS_SENSORS or sensor_pub is None: return
    try:
        msg = Quaternion()
        # 1. Ультразвук с медианным фильтром (окно 3)
        dist_cm = us.get_distance()
        if dist_cm <= 0 or dist_cm > 500: dist_cm = 500.0
        
        us_history.pop(0)
        us_history.append(dist_cm)
        
        # Медиана убирает одиночные "вылеты" (0 или 500)
        sorted_us = sorted(us_history)
        filtered_cm = sorted_us[1]
        msg.x = filtered_cm / 100.0
        
        # 2. ИК сенсоры (ИНВЕРСИЯ: Свапнуты L/R для верного отображения)
        ir_l = 1 if gpio.digital_read(gpio.IRF_R) == 0 else 0
        ir_r = 1 if gpio.digital_read(gpio.IRF_L) == 0 else 0
        
        ir_l_history.pop(0)
        ir_l_history.append(ir_l)
        ir_r_history.pop(0)
        ir_r_history.append(ir_r)
        
        # Смягченный фильтр: мяч круглый, ИК-луч отражается очень нестабильно и мерцает.
        # Теперь достаточно, чтобы датчик моргнул препятствием хотя бы 1 раз из 3-х последних тиков.
        msg.y = float(1 if any(v == 1 for v in ir_l_history) else 0)
        msg.z = float(1 if any(v == 1 for v in ir_r_history) else 0)
        
        # 3. ИК КЛЕШНИ: переменной IO_3 в драйвере нет! Доступны: IR_M, IR_R, IR_L, IRF_R, IRF_L.
        ir_m = 1 if gpio.digital_read(gpio.IR_M) == 0 else 0
        ir_m_history.pop(0)
        ir_m_history.append(ir_m)
        msg.w = float(1 if any(v == 1 for v in ir_m_history) else 0)
        
        # ЛОКАЛЬНЫЙ АВТО-ЗАХВАТ (Hardware-level)
        # Если ИК в клешне сработал — закрываем мгновенно, не дожидаясь ROS-команды
        if HAS_SERVO and msg.w > 0:
            if current_claw_angle != ANGLE_CLAW_CLOSE:
                servo.set(SERVO_CLAW, ANGLE_CLAW_CLOSE)
                current_claw_angle = ANGLE_CLAW_CLOSE
        
        # Отладка ИК (если сработал хоть один)
        if msg.y > 0 or msg.z > 0 or msg.w > 0:
            print(f"[-] SENSORS: L={msg.y}, R={msg.z}, CLAW={msg.w}")
        
        sensor_pub.publish(msg)
    except Exception as e:
        import traceback
        print(f"❌ ОШИБКА В ТАЙМЕРЕ СЕНСОРОВ: {e}")
        traceback.print_exc()

# ==========================================
# ГЛАВНЫЙ БЛОК ROS
# ==========================================
# --- Heartbeat timer для отладки связи ---
def heartbeat_callback(event):
    print(f"[-] Heartbeat: ROS connection active. Time: {time.time()}")

def listener():
    global sensor_pub
    rospy.init_node('unity_robot_master', anonymous=True)
    
    rospy.Subscriber('/cmd_vel', Twist, vel_callback)
    print("[-] Подписка на /cmd_vel оформлена")
    
    if HAS_SERVO:
        rospy.Subscriber('/cmd_gripper', Int32, gripper_callback)
        rospy.Subscriber('/cmd_camera_pan', Float32, camera_callback)
        print("[-] Подписки на клешню и камеру оформлены")
        init_arm() # Вызываем инициализацию при старте!
        
    if HAS_SENSORS:
        sensor_pub = rospy.Publisher('/sensor/data', Quaternion, queue_size=10)
        rospy.Timer(rospy.Duration(0.1), sensor_timer_callback) # 10 Гц
        print("[-] Публикация /sensor/data запущена (10 Гц)")

    # Таймер пульса для проверки связи (2.0с)
    rospy.Timer(rospy.Duration(2.0), heartbeat_callback)
    
    # Watchdog: каждые 0.2 сек проверяем, жив ли /cmd_vel
    rospy.Timer(rospy.Duration(0.2), watchdog_callback)

    print("=== ЕДИНЫЙ МИКРОСЕРВИС РОБОТА ЗАПУЩЕН И ГОТОВ К РАБОТЕ ===")
    rospy.spin()

# --- АВАРИЙНАЯ ОСТАНОВКА МОТОРОВ ПРИ ВЫХОДЕ ---
def emergency_stop():
    print("🛑 АВАРИЙНАЯ ОСТАНОВКА: Скрипт завершается, моторы СТОП!")
    try:
        if HAS_GPIO:
            gpio.digital_write(gpio.IN1, 0)
            gpio.digital_write(gpio.IN2, 0)
            gpio.digital_write(gpio.IN3, 0)
            gpio.digital_write(gpio.IN4, 0)
            gpio.ena_pwm(0)
            gpio.enb_pwm(0)
    except:
        pass

atexit.register(emergency_stop)

if __name__ == '__main__':
    try:
        listener()
    except rospy.ROSInterruptException:
        pass
    finally:
        emergency_stop()
