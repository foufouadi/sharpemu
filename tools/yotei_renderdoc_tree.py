# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Locate the tree's scene attachments and sample them before post-processing."""
import os
import traceback
import renderdoc as rd

ROOT = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\sharpemu-upstream-903'
CAPTURE = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\run-structured\user\logs\capture_logs\PPSA26344\PPSA26344_capture.rdc'
OUT = os.path.join(ROOT, 'artifacts', 'yotei-renderdoc-tree')
os.makedirs(OUT, exist_ok=True)
log = open(os.path.join(OUT, 'report.txt'), 'w', encoding='utf-8')

def write(value):
    log.write(str(value) + '\n')
    log.flush()

def flatten(actions):
    for action in actions:
        yield action
        yield from flatten(action.children)

cap = controller = None
try:
    cap = rd.OpenCaptureFile()
    write(cap.OpenFile(CAPTURE, '', None))
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    write(status)
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError('Replay unavailable')
    textures = {t.resourceId: t for t in controller.GetTextures()}
    targets = {}
    for action in flatten(controller.GetRootActions()):
        if not action.flags & rd.ActionFlags.Drawcall:
            continue
        for rid in action.outputs:
            texture = textures.get(rid)
            if texture and texture.width >= 1500 and texture.height >= 800:
                entry = targets.setdefault(rid, [action.eventId, action.eventId, 0])
                entry[1] = action.eventId
                entry[2] += 1
    for rid, (first, last, count) in sorted(targets.items(), key=lambda item: item[1][0]):
        t = textures[rid]
        write('target=%s extent=%dx%d format=%s events=%d..%d draws=%d' %
              (rid, t.width, t.height, t.format.Name(), first, last, count))
    candidates = sorted(targets.items(), key=lambda item: item[1][2], reverse=True)[:8]
    for rid, (first, last, count) in candidates:
        t = textures[rid]
        controller.SetFrameEvent(last, True)
        write('SAMPLE target=%s last=%d' % (rid, last))
        for fx, fy in ((.5, .5), (.55, .7), (.3, .5), (.8, .8)):
            pixel = controller.PickPixel(rid, int(t.width * fx), int(t.height * fy),
                                         rd.Subresource(0, 0, 0), rd.CompType.Typeless)
            write('point=%s values=%s' % ((fx, fy), list(pixel.floatValue)))
        save = rd.TextureSave()
        save.resourceId = rid
        save.destType = rd.FileType.PNG
        save.alpha = rd.AlphaMapping.Discard
        controller.SaveTexture(save, os.path.join(OUT, 'target_%d.png' % int(rid)))
    for rid in textures:
        if int(rid) != 90892:
            continue
        t = textures[rid]
        controller.SetFrameEvent(targets[rid][1], True)
        for fx, fy in ((.5, .5), (.8, .8)):
            history = controller.PixelHistory(rid, int(t.width * fx), int(t.height * fy),
                                              rd.Subresource(0, 0, 0), rd.CompType.Typeless)
            write('HISTORY point=%s modifications=%d' % ((fx, fy), len(history)))
            for modification in history:
                write('event=%d passed=%s pre=%s post=%s' %
                      (modification.eventId, modification.Passed(),
                       list(modification.preMod.col.floatValue),
                       list(modification.postMod.col.floatValue)))
except Exception:
    write(traceback.format_exc())
finally:
    if controller:
        controller.Shutdown()
    if cap:
        cap.Shutdown()
    log.close()
os._exit(0)
