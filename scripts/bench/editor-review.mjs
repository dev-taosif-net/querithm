// CPU profile + warm cursor-matching timings on the architecture's 12k-line fixture.
// node scripts/bench/editor-review.mjs <output-directory>
import { createServer } from 'vite';
import { Session } from 'node:inspector';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { bigProc } from './gen-schema.mjs';
const out = path.resolve(process.argv[2] ?? '/tmp/querithm-editor-review');
mkdirSync(out, { recursive: true });
const server = await createServer({ configFile: false, optimizeDeps: { noDiscovery: true, include: [] }, server: { middlewareMode: true, watch: null }, appType: 'custom' });
try {
  const { findInsertMatchingHighlight, findSyntaxPairHighlight } = await server.ssrLoadModule('/src/editor/syntaxPairs.ts');
  const text = bigProc('mssql');
  const session = new Session(); session.connect();
  const post = (method) => new Promise((resolve, reject) => session.post(method, (err, data) => err ? reject(err) : resolve(data)));
  await post('Profiler.enable'); await post('Profiler.start');
  const times = [];
  for (let i = 0; i < 201; i++) {
    const start = performance.now();
    findInsertMatchingHighlight(text, text.length - 2, 'tsql');
    findSyntaxPairHighlight(text, text.length - 2, 'tsql');
    times.push(performance.now() - start);
  }
  const { profile } = await post('Profiler.stop'); session.disconnect();
  const cold = times.shift(); times.sort((a,b) => a-b);
  const result = { fixture: 'bigProc(mssql)', coldMs: cold, warmP50Ms: times[100], warmP95Ms: times[190], samples: times.length };
  writeFileSync(path.join(out, 'cursor.cpuprofile'), JSON.stringify(profile));
  writeFileSync(path.join(out, 'cursor.json'), JSON.stringify(result, null, 2));
  console.log(result);
} finally { await server.close(); }
