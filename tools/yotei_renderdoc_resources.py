# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Inspect captured image creation and backing resources without launching the game."""
import os
import traceback
import renderdoc as rd

ROOT = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\sharpemu-upstream-903'
CAPTURE = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\run-structured\user\logs\capture_logs\PPSA26344\PPSA26344_capture.rdc'
log = open(os.path.join(ROOT, 'artifacts', 'yotei-renderdoc-ui', 'resources.txt'), 'w', encoding='utf-8')
def write(s):
    log.write(str(s) + '\n')
    log.flush()
def dump(o, depth=0):
    write(' ' * depth + '%s: %s %s' % (o.name, o.type.name, o.AsString()))
    if depth < 12:
        for i in range(o.NumChildren()):
            dump(o.GetChild(i), depth + 1)
try:
    cap = rd.OpenCaptureFile()
    cap.OpenFile(CAPTURE, '', None)
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    write(status)
    sf = controller.GetStructuredFile()
    resources = {int(r.resourceId): r for r in controller.GetResources()}
    for rid in (21790, 410840, 163):
        r = resources[rid]
        write('RESOURCE %s %s' % (rid, {n:str(getattr(r,n)) for n in dir(r) if not n.startswith('_') and n not in ('this','thisown') and not callable(getattr(r,n))}))
        for idx in r.initialisationChunks:
            dump(sf.chunks[idx])
    for b in controller.GetBuffers():
        if b.length >= 1 << 24:
            write('BUFFER %s length=%d' % (b.resourceId, b.length))
except Exception:
    write(traceback.format_exc())
log.close()
os._exit(0)
