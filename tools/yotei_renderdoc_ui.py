"""Inspect the existing Yotei capture's final UI pass inside qrenderdoc."""
import os
import traceback
import renderdoc as rd

ROOT = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\sharpemu-upstream-903'
CAPTURE = r'C:\Users\foufouadi\.codex\worktrees\yotei-rendering\run-structured\user\logs\capture_logs\PPSA26344\PPSA26344_capture.rdc'
OUT = os.path.join(ROOT, 'artifacts', 'yotei-renderdoc-ui')
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
    status = cap.OpenFile(CAPTURE, '', None)
    write('open: ' + str(status))
    status, controller = cap.OpenCapture(rd.ReplayOptions(), None)
    write('replay: ' + str(status))
    if status != rd.ResultCode.Succeeded:
        raise RuntimeError('Replay unavailable: ' + str(status))
    textures = {t.resourceId: t for t in controller.GetTextures()}
    names = {r.resourceId: r.name for r in controller.GetResources()}
    actions = list(flatten(controller.GetRootActions()))
    draws = [a for a in actions if a.flags & rd.ActionFlags.Drawcall]
    ui = draws[-30:]
    for a in ui:
        write('draw eid=%d count=%d outputs=%s' % (a.eventId, a.numIndices, list(a.outputs)))
    # Only sample a few points in the final pass, avoiding per-action replay.
    for event in (20507, 20537):
        controller.SetFrameEvent(event, True)
        state = controller.GetPipelineState()
        for sampled in state.GetSamplers(rd.ShaderStage.Pixel):
            write('sampler fields: ' + str(dir(sampled)))
            write('sampler: ' + str({n: str(getattr(sampled.sampler, n)) for n in dir(sampled.sampler) if not n.startswith('_') and n not in ('this', 'thisown') and not callable(getattr(sampled.sampler, n))}))
        write('EVENT %d' % event)
        vkstate = controller.GetVulkanPipelineState()
        write('blend fields: ' + str([name for name in dir(vkstate) if 'blend' in name.lower()]))
        blend = getattr(vkstate, 'colorBlend', None)
        if blend:
            write('blend members: ' + str([name for name in dir(blend) if not name.startswith('_')]))
            for attachment in blend.blends:
                write('attachment ' + str(attachment))
        for stage in (rd.ShaderStage.Vertex, rd.ShaderStage.Pixel):
            shader = state.GetShader(stage)
            write('shader %s %s %s' % (stage, shader, names.get(shader, '')))
            reflection = state.GetShaderReflection(stage)
            if reflection:
                with open(os.path.join(OUT, 'shader_%d_%s.spv' % (event, stage)), 'wb') as f:
                    f.write(bytes(reflection.rawBytes))
            seen = set()
            for used in state.GetReadOnlyResources(stage) + state.GetReadWriteResources(stage):
                rid = used.descriptor.resource
                write('descriptor resource=%s byteOffset=%s byteSize=%s access=%s' % (rid, getattr(used.descriptor, 'byteOffset', '?'), getattr(used.descriptor, 'byteSize', '?'), getattr(used, 'access', '?')))
                write('access fields: ' + str({name: str(getattr(used.access, name)) for name in dir(used.access) if not name.startswith('_') and name not in ('this', 'thisown') and not callable(getattr(used.access, name))}))
                if rid in seen:
                    continue
                seen.add(rid)
                texture = textures.get(rid)
                if not texture and rid != rd.ResourceId.Null():
                    offset = getattr(used.descriptor, 'byteOffset', 0)
                    size = min(getattr(used.descriptor, 'byteSize', 0), 4096)
                    if size:
                        data = controller.GetBufferData(rid, offset, size)
                        with open(os.path.join(OUT, 'buffer_%d_%s.bin' % (event, int(rid))), 'wb') as f:
                            f.write(bytes(data))
                        write('buffer prefix: ' + bytes(data[:128]).hex())
                if texture:
                    write('read %s %dx%d %s' % (rid, texture.width, texture.height, texture.format.Name()))
                    swizzle = getattr(used.descriptor, 'swizzle', None)
                    if swizzle:
                        write('descriptor swizzle: ' + str([getattr(swizzle, c, '?') for c in ('red', 'green', 'blue', 'alpha')]))
                    if texture.format.Name() in ('R8_UNORM', 'BC3_UNORM', 'R8G8B8A8_UNORM'):
                        lo, hi = controller.GetMinMax(rid, rd.Subresource(0, 0, 0), rd.CompType.Typeless)
                        write('atlas min=%s max=%s' % (list(lo.floatValue), list(hi.floatValue)))
                        save = rd.TextureSave()
                        save.resourceId = rid
                        save.destType = rd.FileType.PNG
                        save.alpha = rd.AlphaMapping.Discard
                        controller.SaveTexture(save, os.path.join(OUT, 'atlas_%s.png' % int(rid)))
                if len(seen) >= 24:
                    write('remaining resources omitted')
                    break
        for target in state.GetOutputTargets():
            rid = target.resource
            texture = textures.get(rid)
            if texture:
                lo, hi = controller.GetMinMax(rid, rd.Subresource(0, 0, 0), rd.CompType.Typeless)
                write('output %s %dx%d %s min=%s max=%s' % (rid, texture.width, texture.height, texture.format.Name(), list(lo.floatValue), list(hi.floatValue)))
                save = rd.TextureSave()
                save.resourceId = rid
                save.destType = rd.FileType.PNG
                save.alpha = rd.AlphaMapping.Discard
                controller.SaveTexture(save, os.path.join(OUT, 'output_%d_%s.png' % (event, int(rid))))
    shader = next(r.resourceId for r in controller.GetResources() if int(r.resourceId) == 91718)
    mask = next(t.resourceId for t in textures.values() if int(t.resourceId) == 21790)
    write('mask usage: ' + str([(u.eventId, str(u.usage)) for u in controller.GetUsage(mask)]))
    for variant in ('mask_one',):
        with open(os.path.join(OUT, 'ui_' + variant + '.spv'), 'rb') as f:
            replacement, errors = controller.BuildTargetShader('main', rd.ShaderEncoding.SPIRV, f.read(), rd.ShaderCompileFlags(), rd.ShaderStage.Pixel)
        write('replacement %s: %s' % (variant, errors))
        if replacement != rd.ResourceId.Null():
            controller.ReplaceResource(shader, replacement)
            controller.SetFrameEvent(draws[-1].eventId, True)
            save = rd.TextureSave()
            save.resourceId = draws[-1].outputs[0]
            save.destType = rd.FileType.PNG
            save.alpha = rd.AlphaMapping.Discard
            controller.SaveTexture(save, os.path.join(OUT, 'ui_' + variant + '_final.png'))
            controller.SetFrameEvent(20507, True)
            target = next(t.resourceId for t in textures.values() if int(t.resourceId) == 91712)
            for x, y in ((1680, 1080), (1700, 1080), (1720, 1080), (1800, 1090), (1900, 1100)):
                pixel = controller.PickPixel(target, x, y, rd.Subresource(0, 0, 0), rd.CompType.Typeless)
                write('%s pixel %d,%d: %s' % (variant, x, y, list(pixel.floatValue)))
            controller.RemoveReplacement(shader)
            controller.FreeTargetResource(replacement)
    write('done')
except Exception:
    write(traceback.format_exc())
finally:
    if controller:
        controller.Shutdown()
    if cap:
        cap.Shutdown()
    log.close()
    os._exit(0)
