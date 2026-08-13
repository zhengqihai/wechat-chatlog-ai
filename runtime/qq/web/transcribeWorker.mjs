import { createRequire as __weqCreateRequire } from 'node:module';
import { fileURLToPath as __weqFromFileUrl } from 'node:url';
import { dirname as __weqDirname } from 'node:path';
const require = __weqCreateRequire(import.meta.url);
const __weqFilename = __weqFromFileUrl(import.meta.url);
const __weqDirnameValue = __weqDirname(__weqFilename);

// ../desktop/src/main/transcribe/worker.ts
import { existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { createRequire } from "node:module";
var requireFn = createRequire(__weqFilename);
var TAG_RE = /<\|[^|]*\|>/g;
function toPlainText(text) {
  if (!text) return "";
  return text.replace(TAG_RE, "").replace(/\s+/g, " ").trim();
}
function prepareSherpaRuntimeEnv() {
  const platform = process.platform === "win32" ? "win" : process.platform;
  const platformPkg = `sherpa-onnx-${platform}-${process.arch}`;
  const candidates = [];
  try {
    const nodePkg = requireFn.resolve("sherpa-onnx-node/package.json");
    candidates.push(join(dirname(nodePkg), "..", platformPkg));
  } catch {
  }
  try {
    candidates.push(dirname(requireFn.resolve(`${platformPkg}/package.json`)));
  } catch {
  }
  const resourcesPath = process.resourcesPath;
  if (resourcesPath) {
    candidates.push(join(resourcesPath, "app.asar.unpacked", "node_modules", platformPkg));
  }
  candidates.push(join(process.cwd(), "node_modules", platformPkg));
  const dirs = candidates.filter((d) => d && existsSync(d));
  if (process.platform === "win32") {
    const existing = process.env.PATH ?? "";
    process.env.PATH = Array.from(/* @__PURE__ */ new Set([...dirs, ...existing.split(";").filter(Boolean)])).join(";");
  } else if (process.platform === "darwin") {
    const key = "DYLD_LIBRARY_PATH";
    const existing = process.env[key] ?? "";
    process.env[key] = Array.from(/* @__PURE__ */ new Set([...dirs, ...existing.split(":").filter(Boolean)])).join(":");
  } else {
    const key = "LD_LIBRARY_PATH";
    const existing = process.env[key] ?? "";
    process.env[key] = Array.from(/* @__PURE__ */ new Set([...dirs, ...existing.split(":").filter(Boolean)])).join(":");
  }
}
function emit(msg) {
  if (typeof process.send === "function") process.send(msg);
}
function normalizeBuffer(data) {
  if (Buffer.isBuffer(data)) return data;
  if (data instanceof Uint8Array) return Buffer.from(data);
  if (data && typeof data === "object" && data.type === "Buffer" && Array.isArray(data.data)) {
    return Buffer.from(data.data);
  }
  return Buffer.alloc(0);
}
function run(params) {
  try {
    prepareSherpaRuntimeEnv();
    let sherpa;
    try {
      sherpa = requireFn("sherpa-onnx-node");
    } catch (e) {
      emit({ type: "error", error: `\u8BED\u97F3\u8BC6\u522B\u5F15\u64CE\u52A0\u8F7D\u5931\u8D25: ${String(e)}` });
      process.exit(1);
      return;
    }
    const wav = normalizeBuffer(params.wavData);
    if (wav.length <= 44) {
      emit({ type: "final", text: "" });
      process.exit(0);
      return;
    }
    const recognizer = new sherpa.OfflineRecognizer({
      modelConfig: {
        senseVoice: { model: params.modelPath, useInverseTextNormalization: 1 },
        tokens: params.tokensPath,
        numThreads: 2,
        debug: 0
      }
    });
    const pcm = wav.subarray(44);
    const samples = new Float32Array(pcm.length >> 1);
    for (let i = 0; i < samples.length; i += 1) {
      samples[i] = pcm.readInt16LE(i * 2) / 32768;
    }
    const stream = recognizer.createStream();
    stream.acceptWaveform({ sampleRate: params.sampleRate, samples });
    recognizer.decode(stream);
    const result = recognizer.getResult(stream);
    emit({ type: "final", text: toPlainText(result?.text ?? "") });
    process.exit(0);
  } catch (e) {
    emit({ type: "error", error: String(e) });
    process.exit(1);
  }
}
var started = false;
process.once("message", (msg) => {
  if (started) return;
  started = true;
  run(msg);
});
process.once("disconnect", () => process.exit(0));
