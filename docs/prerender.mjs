import { build } from 'vite';
import { readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(new URL('.', import.meta.url).pathname);
const clientDist = join(root, 'dist');
const serverDist = join(root, 'dist-ssr');
const base = process.env.PAGES_BASE_PATH || '/';

await build({
  root,
  base,
  logLevel: 'warn',
  build: {
    ssr: 'src/entry-server.tsx',
    outDir: 'dist-ssr',
    emptyOutDir: true,
    rollupOptions: {
      output: { format: 'esm', entryFileNames: 'entry-server.js' },
    },
  },
  ssr: { noExternal: true },
});

const { routes } = await import(pathToFileURL(join(serverDist, 'entry-server.js')).href);

const template = readFileSync(join(clientDist, 'index.html'), 'utf-8');

const escapeHtml = (s) =>
  s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

for (const route of routes) {
  const html = route.render();
  const page = template
    .replace(/<title>[\s\S]*?<\/title>/, `<title>${escapeHtml(route.title)}</title>`)
    .replace(
      /(<meta\s+name="description"\s+content=")[^"]*(")/,
      `$1${escapeHtml(route.description)}$2`,
    )
    .replace('<div id="root"></div>', `<div id="root">${html}</div>`);

  const outPath =
    route.path === '/'
      ? join(clientDist, 'index.html')
      : join(clientDist, route.path.replace(/^\/+|\/+$/g, ''), 'index.html');
  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, page);
  console.log(`prerendered ${route.path}`);
}

writeFileSync(join(clientDist, '404.html'), readFileSync(join(clientDist, 'index.html'), 'utf-8'));

rmSync(serverDist, { recursive: true, force: true });
