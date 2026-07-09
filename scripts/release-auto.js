const { spawnSync } = require('node:child_process');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const isWindows = process.platform === 'win32';
const command = isWindows ? 'powershell' : 'bash';
const args = isWindows
  ? ['-ExecutionPolicy', 'Bypass', '-File', path.join(root, 'scripts', 'release.ps1')]
  : [path.join(root, 'scripts', 'build-release-in-linux-docker.sh')];

const result = spawnSync(command, args, {
  cwd: root,
  stdio: 'inherit',
  shell: false
});

process.exit(result.status ?? 1);
