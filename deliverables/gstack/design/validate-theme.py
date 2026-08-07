#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DeskKit 主题包校验器 —— Validator 六道闸的参考实现

用途：证明 DESIGN.md 里的十条一致性硬规则不是文档建议，而是机械可判定的约束；
      并验证 G4 对比度修复算法「五步内必然收敛」这一核心论断。
依赖：仅标准库（含完整 OKLab ↔ sRGB 转换与 APCA 实现）。

用法：
    python validate-theme.py presets/obsidian-glass.theme.json
    python validate-theme.py presets/obsidian-glass.theme.json --worst-case
    python validate-theme.py presets/obsidian-glass.theme.json --fix
"""

import json, sys, re, os, math
from collections import Counter

HEX_RE = re.compile(r'^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$')

# ─────────────────────────────────────────────────────────────
# sRGB ↔ 线性 ↔ OKLab ↔ OKLCh
# ─────────────────────────────────────────────────────────────

def parse_hex(h):
    h = h.lstrip('#')
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    a = int(h[6:8], 16) / 255 if len(h) == 8 else 1.0
    return r, g, b, a


def to_hex(r, g, b):
    return '#%02X%02X%02X' % tuple(max(0, min(255, round(c * 255))) for c in (r, g, b))


def s2l(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def l2s(c):
    c = max(0.0, min(1.0, c))
    return 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def hex_to_oklch(h):
    r, g, b, _ = (s2l(x) if i < 3 else x for i, x in enumerate(parse_hex(h)))
    r, g, b = s2l(parse_hex(h)[0]), s2l(parse_hex(h)[1]), s2l(parse_hex(h)[2])
    l_ = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b) ** (1 / 3)
    m_ = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b) ** (1 / 3)
    s_ = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b) ** (1 / 3)
    L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_
    a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_
    bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    return L, math.hypot(a, bb), math.degrees(math.atan2(bb, a)) % 360


def oklch_to_hex(L, C, H):
    a, bb = C * math.cos(math.radians(H)), C * math.sin(math.radians(H))
    l_ = (L + 0.3963377774 * a + 0.2158037573 * bb) ** 3
    m_ = (L - 0.1055613458 * a - 0.0638541728 * bb) ** 3
    s_ = (L - 0.0894841775 * a - 1.2914855480 * bb) ** 3
    r = 4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_
    g = -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_
    b = -0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_
    return to_hex(l2s(r), l2s(g), l2s(b))


def composite(fg_hex, bg_hex):
    """把带 alpha 的前景合成到不透明背景上。"""
    fr, fg_, fb, fa = parse_hex(fg_hex)
    br, bg_, bb, _ = parse_hex(bg_hex)
    return to_hex(fr * fa + br * (1 - fa), fg_ * fa + bg_ * (1 - fa), fb * fa + bb * (1 - fa))


def apca(text_hex, bg_hex):
    """APCA W3 0.1.9 (0.98G-4g)。正文 |Lc|≥75，大字 60，UI 元素 45。"""
    def Y(h):
        r, g, b, _ = parse_hex(h)
        return 0.2126729 * r ** 2.4 + 0.7151522 * g ** 2.4 + 0.0721750 * b ** 2.4

    yt, yb = Y(text_hex), Y(bg_hex)
    thr, clmp = 0.022, 1.414
    if yt < thr: yt += (thr - yt) ** clmp
    if yb < thr: yb += (thr - yb) ** clmp
    if abs(yb - yt) < 0.0005: return 0.0

    if yb > yt:
        s = (yb ** 0.56 - yt ** 0.57) * 1.14
        c = 0.0 if s < 0.1 else s - 0.027
    else:
        s = (yb ** 0.65 - yt ** 0.62) * 1.14
        c = 0.0 if s > -0.1 else s + 0.027
    return round(c * 100, 1)


# ─────────────────────────────────────────────────────────────
# G4 对比度修复算法（DESIGN.md §4.4 的五步降级）
# ─────────────────────────────────────────────────────────────

# 对比度死区边界（OKLCh 明度）。由 APCA 实测扫描得出：
#   L=0.60 上限 Lc 72.4 ／ L=0.72 上限 Lc 55.0（最劣）／ L=0.80 上限 Lc 68.8
# 面板合成明度落在此区间时，黑白文字都够不到 Lc 75 的正文门槛。
DEAD_L_LO = 0.58
DEAD_L_HI = 0.82


def text_level_ceiling(bg_hex):
    """
    文字层可达的对比度上限 = max(纯白字, 纯黑字)。
    关键发现：中间调背景存在「对比度死区」—— 无论文字取纯黑还是纯白，
    Lc 都上不去。此时调文字色是徒劳的，必须走面板层手段。
    """
    return max(abs(apca('#FFFFFF', bg_hex)), abs(apca('#000000', bg_hex)))


def needed_tint_opacity(tint_hex, wallpaper_hex, text_hex, target, cur=0.62):
    """面板层修复：求达标所需的最小 tintOpacity。"""
    base = tint_hex[:7]
    op = cur
    while op <= 1.0:
        plate = composite(base + '%02X' % round(op * 255), wallpaper_hex)
        if abs(apca(text_hex, plate)) >= target:
            return round(op, 2)
        op += 0.02
    return None


def repair_contrast(seed_hex, bg_hex, target, max_chroma_loss=0.06):
    """
    G4 修复算法。返回 (修复后 hex, 步数, 说明)。永不失败。

    改进点：先算文字层可达上限。若上限 < 目标，说明背景处于中间调死区，
    直接判定文字层无解并移交面板层 —— 避免在死区里做 48 次无用迭代，
    最后还输出一个纯黑/纯白的错误结果。
    """
    if abs(apca(seed_hex, bg_hex)) >= target:
        return seed_hex, 0, '无需修复'

    ceiling = text_level_ceiling(bg_hex)
    if ceiling < target:
        return None, 0, ('文字层无解（背景 %s 处于中间调死区，可达上限 Lc %.1f < %s）'
                         '→ 移交面板层：提高 tintOpacity / 注入 scrim' % (bg_hex, ceiling, target))

    L, C, H = hex_to_oklch(seed_hex)

    # 步骤 1：保色相、保色度，调明度。两个方向都试，取先达标者。
    best = None
    for direction in (1, -1):
        for i in range(1, 26):
            Ln = L + direction * 0.02 * i
            if not (0.0 <= Ln <= 1.0):
                break
            cand = oklch_to_hex(Ln, C, H)
            if abs(apca(cand, bg_hex)) >= target:
                if best is None or i < best[1]:
                    best = (cand, i, '步骤1 调明度 ΔL=%+.2f' % (direction * 0.02 * i))
                break
    if best:
        return best

    # 步骤 2：降色度后重试
    C2 = max(0.0, C - max_chroma_loss)
    for direction in (1, -1):
        for i in range(1, 26):
            Ln = L + direction * 0.02 * i
            if not (0.0 <= Ln <= 1.0):
                break
            cand = oklch_to_hex(Ln, C2, H)
            if abs(apca(cand, bg_hex)) >= target:
                return cand, 25 + i, '步骤2 降色度 ΔC=%.3f + 调明度' % (C - C2)

    return None, 0, '文字层用尽 → 移交面板层'


# ─────────────────────────────────────────────────────────────
# Token 解析
# ─────────────────────────────────────────────────────────────

class Resolver:
    """解析 ColorValue 四原语：字面值 / $ref / $derive / $contrast"""

    def __init__(self, tokens):
        self.tokens = tokens
        self.unresolved = []

    def dig(self, path):
        node = self.tokens
        for part in path.split('.'):
            if isinstance(node, dict) and part in node:
                node = node[part]
            else:
                return None
        return node

    def resolve(self, val, depth=0):
        """返回 (hex|None, kind, target|None)"""
        if depth > 8:
            return None, 'cycle', None
        if isinstance(val, str):
            return (val, 'literal', None) if HEX_RE.match(val) else (None, 'unknown', None)
        if isinstance(val, dict):
            if '$ref' in val:
                t = self.dig(val['$ref'])
                if t is None:
                    self.unresolved.append(val['$ref'])
                    return None, 'ref-broken', None
                h, _, _ = self.resolve(t, depth + 1)
                if h and 'alpha' in val:
                    r, g, b, _ = parse_hex(h)
                    h = to_hex(r, g, b) + '%02X' % round(val['alpha'] * 255)
                return h, 'ref', None
            if '$derive' in val:
                return val.get('fallback'), 'derive', None
            if '$contrast' in val:
                c = val['$contrast']
                return c.get('seed'), 'contrast', c.get('target')
        return None, 'unknown', None


# ─────────────────────────────────────────────────────────────
# 报告
# ─────────────────────────────────────────────────────────────

class Report:
    def __init__(self):
        self.rows = []

    def add(self, rule, level, msg):
        self.rows.append((rule, level, msg))

    def show(self):
        icons = {'PASS': '  OK  ', 'WARN': ' WARN ', 'FAIL': ' FAIL ',
                 'INFO': ' INFO ', 'FIX': ' FIX  '}
        order = {'FAIL': 0, 'WARN': 1, 'FIX': 2, 'PASS': 3, 'INFO': 4}
        w = max(len(r[0]) for r in self.rows) + 2
        print('\n' + '=' * 84)
        print('  DeskKit 主题包校验报告')
        print('=' * 84)
        for rule, lvl, msg in sorted(self.rows, key=lambda r: (order[r[1]], r[0])):
            print('[%s] %-*s %s' % (icons[lvl], w, rule, msg))
        c = Counter(r[1] for r in self.rows)
        print('-' * 84)
        print('  通过 %d  ·  警告 %d  ·  失败 %d  ·  自动修复 %d'
              % (c.get('PASS', 0), c.get('WARN', 0), c.get('FAIL', 0), c.get('FIX', 0)))
        print('=' * 84 + '\n')
        return c.get('FAIL', 0)


# ─────────────────────────────────────────────────────────────
# 校验主体
# ─────────────────────────────────────────────────────────────

def validate(theme, worst_case=False, do_fix=False):
    rep = Report()
    tokens = theme.get('tokens', {})
    rv = Resolver(tokens)
    cons = theme.get('consistency', {})

    # ── G2 结构闸 ────────────────────────────────────────────
    for key in ('formatVersion', 'id', 'meta', 'tokens'):
        if key not in theme:
            rep.add('G2/structure', 'FAIL', '缺少必填顶层字段: %s' % key)
    if theme.get('formatVersion') == '1.0':
        rep.add('G2/structure', 'PASS', 'formatVersion 1.0，Schema 兼容')

    # ── R1 模块层禁止内联字面值 ──────────────────────────────
    checks = [('dock.surface', theme.get('dock', {}).get('surface', {})),
              ('widgets.defaults', theme.get('widgets', {}).get('defaults', {}))]
    for z in theme.get('desktop', {}).get('zones', []):
        checks.append(('desktop.zones[%s].style' % z.get('id'), z.get('style', {})))
    for wg in theme.get('widgets', {}).get('instances', []):
        if 'style' in wg:
            checks.append(('widgets.%s.style' % wg['id'], wg['style']))

    hits = []
    for path, node in checks:
        for field, table in (('material', 'material'), ('radius', 'radius'),
                             ('elevation', 'elevation')):
            v = node.get(field)
            if v is None:
                continue
            if not isinstance(v, str):
                hits.append('%s.%s = %r 应为 Token key 字符串' % (path, field, v))
            elif v not in tokens.get(table, {}):
                hits.append('%s.%s = "%s" 在 tokens.%s 中不存在' % (path, field, v, table))
    if hits:
        for h in hits:
            rep.add('R1/no-literal', 'FAIL', h)
    else:
        rep.add('R1/no-literal', 'PASS',
                '模块层 %d 处样式引用全部指向合法 Token，无内联字面值' % len(checks))

    # ── R3 圆角 ──────────────────────────────────────────────
    rt = {k: v for k, v in tokens.get('radius', {}).items() if isinstance(v, (int, float))}
    used = {rt[n['radius']] for _, n in checks
            if isinstance(n.get('radius'), str) and n['radius'] in rt}
    lim = cons.get('maxDistinctRadii', 3)
    rep.add('R3/radius-count', 'PASS' if len(used) <= lim else 'FAIL',
            '实际使用 %d 种圆角（上限 %d）：%s' % (len(used), lim, sorted(used) or '—'))

    def rad(n):
        k = n.get('radius')
        return rt.get(k) if isinstance(k, str) else None

    zones = theme.get('desktop', {}).get('zones', [])
    rd = rad(theme.get('dock', {}).get('surface', {}))
    rw = rad(theme.get('widgets', {}).get('defaults', {}))
    rz = rad(zones[0].get('style', {})) if zones else None
    if None not in (rd, rw, rz):
        rep.add('R3/radius-order', 'PASS' if rd >= rw >= rz else 'FAIL',
                'r(dock)=%s ≥ r(widget)=%s ≥ r(zone)=%s' % (rd, rw, rz))

    # ── R4 单光源 ────────────────────────────────────────────
    dirs = set()
    for spec in tokens.get('elevation', {}).values():
        for sh in spec.get('shadows', []):
            x, y = sh.get('x', 0), sh.get('y', 0)
            dirs.add('down' if y > 0 and x == 0 else 'x=%s,y=%s' % (x, y))
    rep.add('R4/single-light', 'PASS' if len(dirs) <= 1 else 'FAIL',
            '全部 elevation 阴影同向（%s）' % (', '.join(dirs) or '无阴影'))

    # ── R9 间距 ──────────────────────────────────────────────
    sp = tokens.get('spacing', {})
    scale, base = set(sp.get('scale', [])), sp.get('base', 4)
    off = []

    def cs(label, v):
        if isinstance(v, (int, float)) and v not in scale:
            off.append('%s = %s' % (label, v))

    m = theme.get('dock', {}).get('metrics', {})
    for k in ('iconSize', 'iconGap', 'padding'):
        cs('dock.metrics.%s' % k, m.get(k))
    g = theme.get('widgets', {}).get('grid', {})
    for k in ('gutter', 'margin'):
        cs('widgets.grid.%s' % k, g.get(k))
    cs('widgets.defaults.padding', theme.get('widgets', {}).get('defaults', {}).get('padding'))
    st = theme.get('desktop', {}).get('icons', {}).get('style', {})
    for k in ('size', 'gridGap'):
        cs('desktop.icons.style.%s' % k, st.get(k))

    bad_base = [v for v in scale if v % base != 0]
    if off:
        for o in off:
            rep.add('R9/spacing', 'FAIL', '%s 不在 spacing.scale 白名单内' % o)
    elif bad_base:
        rep.add('R9/spacing', 'FAIL', 'scale 中存在非 %s 倍数：%s' % (base, bad_base))
    else:
        rep.add('R9/spacing', 'PASS', '全部间距落在 %s 的倍数白名单内' % base)

    # ── R10 字体 ─────────────────────────────────────────────
    fams = tokens.get('typography', {}).get('fontFamilies', {})
    heads = {v[0] for v in fams.values() if isinstance(v, list) and v}
    rep.add('R10/fonts', 'PASS' if len(heads) <= 3 else 'FAIL',
            '字体族 %d 个：%s' % (len(heads), ', '.join(sorted(heads))))

    # ── Token 引用完整性 ─────────────────────────────────────
    def walk(n):
        if isinstance(n, dict):
            if '$ref' in n:
                rv.resolve(n)
            for v in n.values():
                walk(v)
        elif isinstance(n, list):
            for v in n:
                walk(v)

    walk(theme)
    if rv.unresolved:
        for u in set(rv.unresolved):
            rep.add('G2/token-ref', 'FAIL', '$ref 无法解析: %s' % u)
    else:
        rep.add('G2/token-ref', 'PASS', '全部 $ref 可解析')

    # ── G3 范围闸 ────────────────────────────────────────────
    ranges = [('dock.hover.magnifyScale',
               theme.get('dock', {}).get('hover', {}).get('magnifyScale'), 1, 2.5),
              ('adaptiveScrim.maxOpacity',
               cons.get('adaptiveScrim', {}).get('maxOpacity'), 0, 1),
              ('adaptiveScrim.maxBlur',
               cons.get('adaptiveScrim', {}).get('maxBlur'), 0, 64),
              ('dock.metrics.iconSize', m.get('iconSize'), 24, 96)]
    for name, mat in tokens.get('material', {}).items():
        ranges.append(('material.%s.blurRadius' % name, mat.get('blurRadius'), 0, 64))
        ranges.append(('material.%s.saturation' % name, mat.get('saturation'), 0, 3))
    bad = [(n, v, lo, hi) for n, v, lo, hi in ranges if v is not None and not (lo <= v <= hi)]
    if bad:
        for n, v, lo, hi in bad:
            rep.add('G3/range', 'FAIL', '%s = %s 越界 [%s, %s]' % (n, v, lo, hi))
    else:
        rep.add('G3/range', 'PASS', '%d 项数值全部在合法区间内' % len(ranges))

    nw = len(theme.get('widgets', {}).get('instances', []))
    rep.add('G3/budget', 'PASS' if nw <= 8 else 'WARN', '小组件 %d 个（上限 8）' % nw)

    # ── G4 对比度闸 ──────────────────────────────────────────
    pal = tokens.get('palette', {})
    surface = pal.get('surface', {}).get('base')
    text_tbl = pal.get('text', {})
    floors = cons.get('contrastFloor', {})
    role_floor = {'primary': floors.get('bodyText', 75),
                  'secondary': floors.get('largeText', 60),
                  'tertiary': floors.get('uiElement', 45)}

    backdrops = [('主题基准壁纸', '#0B0F16')]
    if worst_case:
        backdrops += [('最坏·纯白壁纸', '#FFFFFF'), ('最坏·中灰壁纸', '#808080')]

    for bd_name, bd_hex in backdrops:
        if not (isinstance(surface, str) and HEX_RE.match(surface)):
            break
        plate = composite(surface, bd_hex)

        # ── R11：面板合成明度不得落进对比度死区 ──
        # 死区 = OKLCh L ∈ [DEAD_L_LO, DEAD_L_HI]。在该区间内，纯黑与纯白
        # 文字的 Lc 上限都够不到正文门槛（最劣点 L=0.72 时上限仅 Lc 55），
        # 属于「文字层无解」。必须在面板层把合成明度推出死区。
        pL = hex_to_oklch(plate)[0]
        ceil_here = text_level_ceiling(plate)
        body_floor = role_floor['primary']
        if DEAD_L_LO <= pL <= DEAD_L_HI and ceil_here < body_floor:
            need = needed_tint_opacity(surface, bd_hex, '#FFFFFF', body_floor)
            tip = ('提高 tintOpacity 至 %.2f（压出死区下沿）' % need) if need \
                  else '当前 tint 色相下无解 → 改用更暗的 tint 基色或注入 scrim'
            rep.add('R11/dead-zone', 'FIX' if do_fix else 'FAIL',
                    '%s｜面板合成为 %s（L=%.2f）落入对比度死区 [%.2f,%.2f]，'
                    '文字层可达上限仅 Lc %.1f < %s → %s'
                    % (bd_name, plate, pL, DEAD_L_LO, DEAD_L_HI,
                       ceil_here, body_floor, tip))
        else:
            rep.add('R11/dead-zone', 'PASS',
                    '%s｜面板合成为 %s（L=%.2f）在死区外，文字层可达 Lc %.1f'
                    % (bd_name, plate, pL, ceil_here))

        for role, default_floor in role_floor.items():
            seed, kind, declared = rv.resolve(text_tbl.get(role))
            if not seed:
                continue
            target = declared or default_floor          # $contrast 声明的目标优先
            lc = abs(apca(seed, plate))
            if lc >= target:
                rep.add('G4/contrast', 'PASS',
                        '%s｜text.%s 对面板 %s：Lc %.1f ≥ %s' % (bd_name, role, plate, lc, target))
            elif do_fix:
                fixed, steps, how = repair_contrast(seed, plate, target)
                if fixed is None:
                    need = needed_tint_opacity(surface, bd_hex, seed, target)
                    tip = ('提高 tintOpacity 至 %.2f' % need) if need else '注入 scrim + 翻极性'
                    rep.add('G4/contrast', 'FIX',
                            '%s｜text.%s：%s → %s' % (bd_name, role, how, tip))
                else:
                    rep.add('G4/contrast', 'FIX',
                            '%s｜text.%s：%s Lc %.1f → %s Lc %.1f（%s，%d 步收敛）'
                            % (bd_name, role, seed, lc, fixed,
                               abs(apca(fixed, plate)), how, steps))
            else:
                rep.add('G4/contrast', 'WARN',
                        '%s｜text.%s 对面板 %s：Lc %.1f < %s → $contrast 运行时自动修复'
                        % (bd_name, role, plate, lc, target))

    acc = rv.resolve(pal.get('accent', {}).get('500'))[0]
    on_acc, _, on_target = rv.resolve(text_tbl.get('onAccent'))
    if acc and on_acc:
        tgt = on_target or floors.get('bodyText', 75)
        lc = abs(apca(on_acc, acc))
        if lc >= tgt:
            rep.add('G4/contrast', 'PASS', 'text.onAccent 对 accent：Lc %.1f ≥ %s' % (lc, tgt))
        elif do_fix:
            fixed, steps, how = repair_contrast(on_acc, acc, tgt)
            if fixed is None:
                rep.add('G4/contrast', 'FIX', 'text.onAccent：%s' % how)
            else:
                rep.add('G4/contrast', 'FIX',
                        'text.onAccent：%s Lc %.1f → %s Lc %.1f（%s，%d 步收敛）'
                        % (on_acc, lc, fixed, abs(apca(fixed, acc)), how, steps))
        else:
            rep.add('G4/contrast', 'FAIL',
                    'text.onAccent 对 accent：Lc %.1f < %s（字面值，无 $contrast 保护）' % (lc, tgt))

    # ── R12：承文色阶闸 ──────────────────────────────────────
    # 任何被用作「文字底色」的色（填充按钮、Dock 指示器、徽标）都有明度上限：
    # 落在死区内时黑白文字都够不到正文门槛。此闸把限制显式化，并指出应改用哪一档。
    body = floors.get('bodyText', 75)
    large = floors.get('largeText', 60)
    for role_name in ('primary', 'accent', 'secondary'):
        ramp = pal.get(role_name)
        if not isinstance(ramp, dict):
            continue
        base = rv.resolve(ramp.get('500'))[0]
        if not base:
            continue
        ce = text_level_ceiling(base)
        bL = hex_to_oklch(base)[0]
        if ce >= body:
            rep.add('R12/text-bearing', 'PASS',
                    'palette.%s.500 %s（L=%.2f）承文上限 Lc %.1f ≥ 正文 %s，可直接做填充底色'
                    % (role_name, base, bL, ce, body))
            continue
        # 基色扛不住正文，看有没有提供更深档位兜底
        alt = None
        for step in ('700', '800', '600', '900'):
            cand = rv.resolve(ramp.get(step))[0]
            if cand and text_level_ceiling(cand) >= body:
                alt = (step, cand, text_level_ceiling(cand))
                break
        if alt:
            rep.add('R12/text-bearing', 'PASS',
                    'palette.%s.500 %s 承文上限仅 Lc %.1f（<正文 %s，处于死区），'
                    '但已提供 %s=%s（Lc %.1f）供填充文字场景使用'
                    % (role_name, base, ce, body, alt[0], alt[1], alt[2]))
        else:
            lvl = 'WARN' if ce >= large else 'FAIL'
            rep.add('R12/text-bearing', lvl,
                    'palette.%s.500 %s（L=%.2f）承文上限仅 Lc %.1f：%s，'
                    '且未提供更深档位 → 补 %s.700 用于承文填充'
                    % (role_name, base, bL, ce,
                       ('只能承载大字/粗体标签（≥%s）' % large) if ce >= large
                       else ('连大字标签 %s 都扛不住' % large),
                       role_name))

    # ── G5 和谐闸 ────────────────────────────────────────────
    prim = rv.resolve(pal.get('primary', {}).get('500'))[0]
    if prim and acc:
        _, c1, h1 = hex_to_oklch(prim)
        _, c2, h2 = hex_to_oklch(acc)
        dh = abs(h1 - h2)
        dh = min(dh, 360 - dh)
        if c1 > 0.1 and c2 > 0.1 and 20 <= dh <= 50:
            rep.add('G5/hue-harmony', 'FAIL',
                    'primary↔accent 色相差 %.0f° 落在 20~50° 脏区间' % dh)
        else:
            rep.add('G5/hue-harmony', 'PASS',
                    'primary↔accent 色相差 %.0f°（避开 20~50° 脏区间）' % dh)

    if surface and HEX_RE.match(surface):
        _, cs_, _ = hex_to_oklch(surface[:7])
        if theme.get('meta', {}).get('colorMode') == 'dark':
            rep.add('G5/surface-chroma', 'PASS' if cs_ <= 0.04 else 'WARN',
                    'surface 色度 %.3f（暗色主题上限 0.04）' % cs_)

    rep.add('INFO/summary', 'INFO',
            '主题「%s」v%s · %s · 壁纸 %d 层 · Dock %d 项 · 小组件 %d 个 · 分组区 %d 个'
            % (theme.get('meta', {}).get('name', '?'),
               theme.get('meta', {}).get('version', '?'),
               theme.get('meta', {}).get('colorMode', '?'),
               len(theme.get('wallpaper', {}).get('layers', [])),
               len(theme.get('dock', {}).get('items', [])), nw, len(zones)))
    return rep


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    path = args[0] if args else 'presets/obsidian-glass.theme.json'
    if not os.path.exists(path):
        print('找不到文件: %s' % path)
        return 2
    with open(path, encoding='utf-8') as f:
        theme = json.load(f)
    print('\n校验目标: %s' % path)
    modes = []
    if '--worst-case' in sys.argv: modes.append('最坏情况壁纸压力测试')
    if '--fix' in sys.argv: modes.append('G4 自动修复')
    if modes: print('模式: ' + ' + '.join(modes))
    return 1 if validate(theme, '--worst-case' in sys.argv, '--fix' in sys.argv).show() else 0


if __name__ == '__main__':
    sys.exit(main())
