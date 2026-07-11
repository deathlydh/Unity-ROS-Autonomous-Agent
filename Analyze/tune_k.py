PWM_F = 200; MIN_PWM = 35; DZ = 10

def test(K, gas, steer):
    lin = gas * 0.5
    ang = steer * 1.0
    vl = lin + ang * K
    vr = lin - ang * K
    pl = max(min(vl * PWM_F, 100), -100)
    pr = max(min(vr * PWM_F, 100), -100)
    pl = -pl  # left inversion
    al = abs(pl); ar = abs(pr)
    if al < DZ: al = 0
    elif al < MIN_PWM: al = MIN_PWM
    if ar < DZ: ar = 0
    elif ar < MIN_PWM: ar = MIN_PWM
    phys_l = 'B' if pl > 0 else ('F' if pl < 0 else 'S')
    phys_r = 'F' if pr > 0 else ('B' if pr < 0 else 'S')
    spin = (phys_l == 'B' and phys_r == 'F') or (phys_l == 'F' and phys_r == 'B')
    return al, ar, spin, phys_l, phys_r

cases = [(0.4, 0.2, "soft"), (0.4, 0.4, "med"), (0.4, 0.6, "hard"),
         (0.3, 0.8, "v.hard"), (0.5, 0.3, "fwd+"), (0.0, 1.0, "spin")]

for K in [0.075, 0.10, 0.15, 0.20, 0.25, 0.30]:
    print(f"K={K:.3f}:")
    for g, s, label in cases:
        al, ar, spin, pl, pr = test(K, g, s)
        if spin:
            tag = "SPIN!"
        elif pl == 'F' and pr == 'F':
            tag = f"diff={al-ar:+.0f} ({100*(al-ar)/max(ar,1):.0f}%)"
        else:
            tag = f"{pl}{al:.0f}/{pr}{ar:.0f}"
        print(f"  g={g} s={s} ({label:6s}): L={pl}{al:.0f} R={pr}{ar:.0f}  {tag}")
    print()
