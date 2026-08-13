import { createRequire as __weqCreateRequire } from 'node:module';
import { fileURLToPath as __weqFromFileUrl } from 'node:url';
import { dirname as __weqDirname } from 'node:path';
const require = __weqCreateRequire(import.meta.url);
const __weqFilename = __weqFromFileUrl(import.meta.url);
const __weqDirnameValue = __weqDirname(__weqFilename);

// ../desktop/src/main/inject_worker.ts
import { createRequire } from "node:module";
import { dirname } from "node:path";
var requireFn = createRequire(__weqFilename);
function fail(error, code) {
  const payload = { ok: false, error };
  process.stderr.write(JSON.stringify(payload));
  process.exit(code);
}
async function main() {
  const pid = Number(process.argv[2]);
  const ntHelperPath = process.argv[3];
  if (!Number.isInteger(pid) || pid <= 0) fail(`bad pid: ${process.argv[2]}`, 2);
  if (!ntHelperPath) fail("missing nt_helper.node path (argv[3])", 2);
  try {
    process.chdir(dirname(ntHelperPath));
  } catch (e) {
    fail(`chdir failed: ${e instanceof Error ? e.message : String(e)}`, 2);
  }
  let nt;
  try {
    nt = requireFn(ntHelperPath);
  } catch (e) {
    fail(`require nt_helper.node failed: ${e instanceof Error ? e.message : String(e)}`, 1);
  }
  const initStatus = nt.getInitStatus();
  if (initStatus !== 0) fail(`nt_helper init failed (status ${initStatus})`, 1);
  try {
    const status = await nt.injectAndGetStatusEmbedded(pid);
    const payload = { ok: true, status };
    process.stdout.write(JSON.stringify(payload));
    process.exit(0);
  } catch (e) {
    fail(`inject failed: ${e instanceof Error ? e.message : String(e)}`, 1);
  }
}
void main();
