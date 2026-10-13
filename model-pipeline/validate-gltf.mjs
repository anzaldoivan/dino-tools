import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

function emit(value, exitCode) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
  process.exitCode = exitCode;
}

try {
  if (process.argv.length !== 4) {
    throw new Error('usage: node validate-gltf.mjs <validator-root> <input.gltf|input.glb>');
  }

  const validatorRoot = path.resolve(process.argv[2]);
  const inputPath = path.resolve(process.argv[3]);
  const require = createRequire(path.join(validatorRoot, 'package.json'));
  const validator = require('gltf-validator');
  const asset = await readFile(inputPath);
  const report = await validator.validateBytes(new Uint8Array(asset), {
    uri: path.basename(inputPath),
    format: path.extname(inputPath).toLowerCase() === '.glb' ? 'glb' : 'gltf',
    writeTimestamp: false,
    maxIssues: 0,
  });
  const errors = report?.issues?.numErrors ?? 1;

  emit({
    ok: errors === 0,
    validatorVersion: validator.version(),
    report,
  }, errors === 0 ? 0 : 1);
} catch (error) {
  emit({
    ok: false,
    error: error instanceof Error ? error.message : String(error),
  }, 2);
}
